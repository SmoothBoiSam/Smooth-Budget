using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Shared;

/// <summary>
/// Someone you share costs with. Linked: they have their own account, linked to yours, since then. Otherwise just a name.
/// InviteExpiresUtc: an invite you sent them that hasn't been used yet (the link itself is only shown when it's made).
/// </summary>
public record FriendDto(Guid Id, string Name, bool Linked, DateTime? LinkedSince, DateTime? InviteExpiresUtc);

/// <summary>The Shared page's friends list, and your name as your friends see it.</summary>
public record SharedOverviewDto(string? YourName, IReadOnlyList<FriendDto> Friends);

/// <summary>A new invite: the token goes into the link, and is never stored or shown again.</summary>
public record InviteCreatedDto(string Token, DateTime ExpiresUtc);

/// <summary>What the join screen shows before you sign up: who invited you, or why the link doesn't work.</summary>
public record InvitePeekDto(bool Valid, string? FromName, string? Problem);

public record SetNameRequest(string Name);
public record JoinRequest(string Token);

/// <summary>What happened: Found is false when there's no such friend; Problem says why it couldn't be done.</summary>
public record SharedResult(bool Found = true, string? Problem = null)
{
    public static readonly SharedResult Ok = new();
    public static readonly SharedResult Missing = new(false);
    public static SharedResult No(string problem) => new(true, problem);
}

/// <summary>
/// Friends (DECISIONS.md 205): as many as you like, each just a name until they link their own account. You make a
/// one-time invite for a friend; they open it and sign up (sign-up is otherwise closed), or sign in and join. The two
/// accounts are then linked: each keeps its own budget, and only what you send each other is shared.
/// </summary>
public class SharedService(AppDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    public const int MaxName = 60;

    public async Task<SharedOverviewDto> OverviewAsync(Guid householdId, CancellationToken ct)
    {
        var me = await db.Households.AsNoTracking().Where(h => h.Id == householdId).Select(h => h.DisplayName).SingleAsync(ct);
        var friends = await db.Friends.AsNoTracking().Where(f => f.HouseholdId == householdId).ToListAsync(ct);
        var linkIds = friends.Where(f => f.LinkId is not null).Select(f => f.LinkId!.Value).ToList();
        var since = await db.HouseholdLinks.AsNoTracking().Where(l => linkIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.CreatedUtc, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var invites = await db.FriendInvites.AsNoTracking()
            .Where(i => i.HouseholdId == householdId && i.UsedUtc == null && i.ExpiresUtc > now)
            .Select(i => new { i.FriendId, i.ExpiresUtc }).ToListAsync(ct);
        var list = friends
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FriendDto(f.Id, f.Name, f.LinkId is not null, f.LinkId is { } l ? since[l] : null,
                f.LinkId is null ? invites.Where(i => i.FriendId == f.Id).Select(i => (DateTime?)i.ExpiresUtc).Max() : null))
            .ToList();
        return new SharedOverviewDto(me, list);
    }

    public async Task<string?> SetNameAsync(Guid householdId, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxName) return "Your name is 1 to 60 characters.";
        var me = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        me.DisplayName = trimmed;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>A new friend, just a name for now. Names are unique among your friends, so you can tell them apart.</summary>
    public async Task<(FriendDto? Friend, string? Problem)> AddFriendAsync(Guid householdId, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (await NameProblemAsync(householdId, trimmed, null, ct) is { } problem) return (null, problem);
        var friend = new Friend { Id = Guid.NewGuid(), HouseholdId = householdId, Name = trimmed, CreatedUtc = clock.GetUtcNow().UtcDateTime };
        db.Friends.Add(friend);
        await db.SaveChangesAsync(ct);
        return (new FriendDto(friend.Id, friend.Name, false, null, null), null);
    }

    public async Task<SharedResult> RenameFriendAsync(Guid householdId, Guid friendId, string name, CancellationToken ct)
    {
        var friend = await db.Friends.SingleOrDefaultAsync(f => f.Id == friendId && f.HouseholdId == householdId, ct);
        if (friend is null) return SharedResult.Missing;
        var trimmed = name.Trim();
        if (await NameProblemAsync(householdId, trimmed, friendId, ct) is { } problem) return SharedResult.No(problem);
        friend.Name = trimmed;
        await db.SaveChangesAsync(ct);
        return SharedResult.Ok;
    }

    /// <summary>Removes a friend from your list. If they're linked, the link ends for both of you first.</summary>
    public async Task<bool> RemoveFriendAsync(Guid householdId, Guid friendId, CancellationToken ct)
    {
        var friend = await db.Friends.AsNoTracking().SingleOrDefaultAsync(f => f.Id == friendId && f.HouseholdId == householdId, ct);
        if (friend is null) return false;
        if (friend.LinkId is { } linkId) await EndLinkAsync(linkId, ct);
        await db.Friends.Where(f => f.Id == friendId).ExecuteDeleteAsync(ct); // their unused invites go with them
        return true;
    }

    /// <summary>A new one-time invite for this friend; an older unused one stops working. Not once they're linked, or before you've said your name.</summary>
    public async Task<(InviteCreatedDto? Invite, SharedResult Result)> CreateInviteAsync(Guid householdId, Guid friendId, CancellationToken ct)
    {
        var friend = await db.Friends.AsNoTracking().SingleOrDefaultAsync(f => f.Id == friendId && f.HouseholdId == householdId, ct);
        if (friend is null) return (null, SharedResult.Missing);
        if (friend.LinkId is not null) return (null, SharedResult.No($"You're already linked with {friend.Name}."));
        var me = await db.Households.AsNoTracking().Where(h => h.Id == householdId).Select(h => h.DisplayName).SingleAsync(ct);
        if (string.IsNullOrWhiteSpace(me)) return (null, SharedResult.No("Add your name first, so they know who's inviting them."));

        await db.FriendInvites.Where(i => i.FriendId == friendId && i.UsedUtc == null).ExecuteDeleteAsync(ct);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var invite = new FriendInvite { Id = Guid.NewGuid(), HouseholdId = householdId, FriendId = friendId, TokenHash = Hash(token), CreatedUtc = now, ExpiresUtc = now + InviteLifetime };
        db.FriendInvites.Add(invite);
        await db.SaveChangesAsync(ct);
        return (new InviteCreatedDto(token, invite.ExpiresUtc), SharedResult.Ok);
    }

    public async Task CancelInviteAsync(Guid householdId, Guid friendId, CancellationToken ct) =>
        await db.FriendInvites.Where(i => i.HouseholdId == householdId && i.FriendId == friendId && i.UsedUtc == null).ExecuteDeleteAsync(ct);

    /// <summary>Who an invite is from, for the join screen. Says plainly when the link is used, expired or wrong.</summary>
    public async Task<InvitePeekDto> PeekAsync(string token, CancellationToken ct)
    {
        var (invite, problem) = await FindAsync(token, ct);
        if (invite is null) return new InvitePeekDto(false, null, problem);
        var from = await db.Households.AsNoTracking().Where(h => h.Id == invite.HouseholdId).Select(h => h.DisplayName).SingleAsync(ct);
        return new InvitePeekDto(true, from, null);
    }

    /// <summary>Is this a working invite? Used before creating an account from it.</summary>
    public async Task<string?> CheckAsync(string token, CancellationToken ct) => (await FindAsync(token, ct)).Problem;

    /// <summary>
    /// Links the account that opened the invite to the one that made it, and uses the invite up. The friend it was made
    /// for becomes linked; on the joining side, a friend you already keep by the inviter's name becomes linked (keeping
    /// its history), or a new one is added.
    /// </summary>
    public async Task<string?> JoinAsync(Guid householdId, string token, CancellationToken ct)
    {
        var (found, problem) = await FindAsync(token, ct);
        if (found is null) return problem;
        if (found.HouseholdId == householdId) return "That's your own invite. Send it to the friend it's for.";
        var inviterId = found.HouseholdId;
        if (await db.HouseholdLinks.AnyAsync(l => l.EndedUtc == null &&
                ((l.HouseholdAId == inviterId && l.HouseholdBId == householdId) || (l.HouseholdAId == householdId && l.HouseholdBId == inviterId)), ct))
            return "You're already linked with each other.";
        var theirFriend = await db.Friends.SingleAsync(f => f.Id == found.FriendId, ct);
        if (theirFriend.LinkId is not null) return "That invite is for someone who's already linked. Ask for a new one.";

        var now = clock.GetUtcNow().UtcDateTime;
        var link = new HouseholdLink { Id = Guid.NewGuid(), HouseholdAId = inviterId, HouseholdBId = householdId, CreatedUtc = now };
        db.HouseholdLinks.Add(link);
        theirFriend.LinkId = link.Id;

        var inviterName = await db.Households.AsNoTracking().Where(h => h.Id == inviterId).Select(h => h.DisplayName).SingleAsync(ct) ?? "Friend";
        var mine = await db.Friends.Where(f => f.HouseholdId == householdId).ToListAsync(ct);
        var same = mine.FirstOrDefault(f => f.LinkId is null && string.Equals(f.Name, inviterName, StringComparison.OrdinalIgnoreCase));
        if (same is not null) same.LinkId = link.Id;
        else
        {
            var name = inviterName;
            for (var n = 2; mine.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{inviterName[..Math.Min(inviterName.Length, MaxName - 4)]} {n}";
            db.Friends.Add(new Friend { Id = Guid.NewGuid(), HouseholdId = householdId, Name = name, LinkId = link.Id, CreatedUtc = now });
        }

        (await db.FriendInvites.SingleAsync(i => i.Id == found.Id, ct)).UsedUtc = now;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Ends the link with this friend, for both of you. You each keep the other as a friend without an account, history and all.</summary>
    public async Task<SharedResult> UnlinkAsync(Guid householdId, Guid friendId, CancellationToken ct)
    {
        var friend = await db.Friends.AsNoTracking().SingleOrDefaultAsync(f => f.Id == friendId && f.HouseholdId == householdId, ct);
        if (friend is null) return SharedResult.Missing;
        if (friend.LinkId is not { } linkId) return SharedResult.No($"{friend.Name} isn't linked.");
        await EndLinkAsync(linkId, ct);
        return SharedResult.Ok;
    }

    private async Task EndLinkAsync(Guid linkId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.HouseholdLinks.Where(l => l.Id == linkId && l.EndedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(l => l.EndedUtc, now), ct);
        await db.Friends.Where(f => f.LinkId == linkId).ExecuteUpdateAsync(s => s.SetProperty(f => f.LinkId, (Guid?)null), ct);
    }

    private async Task<string?> NameProblemAsync(Guid householdId, string name, Guid? except, CancellationToken ct)
    {
        if (name.Length is 0 or > MaxName) return "A name is 1 to 60 characters.";
        var names = await db.Friends.AsNoTracking().Where(f => f.HouseholdId == householdId && f.Id != except).Select(f => f.Name).ToListAsync(ct);
        return names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ? $"You already have a friend named {name}." : null;
    }

    private async Task<(FriendInvite? Invite, string? Problem)> FindAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return (null, "That invite link isn't complete. Ask for a new one.");
        var hash = Hash(token.Trim());
        var invite = await db.FriendInvites.AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null) return (null, "That invite link doesn't work. Ask for a new one.");
        if (invite.UsedUtc is not null) return (null, "That invite has already been used.");
        if (invite.ExpiresUtc <= clock.GetUtcNow().UtcDateTime) return (null, "That invite has expired. Ask for a new one.");
        return (invite, null);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

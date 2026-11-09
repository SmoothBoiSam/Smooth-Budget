using System.Collections.Concurrent;

namespace SmoothBudget.Api.Plaid;

/// <param name="ItemId">The connection being updated (sign in again, add accounts); null for a new bank.</param>
public record PendingLink(string LinkToken, Guid? ItemId, bool AddAccounts);

/// <summary>
/// The Plaid Link session each user has open, so it can be picked up again after a bank's own sign-in sends them back
/// to /plaid-oauth (DECISIONS.md 185). On a phone that page often opens in the browser rather than the installed app,
/// with nothing saved there, so it's kept on the server for the signed-in user. Only in memory, for as long as a link
/// token lasts; never logged. A restart only means starting Add accounts again.
/// </summary>
public class PlaidOAuthPending(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);
    private readonly ConcurrentDictionary<string, (PendingLink Link, DateTimeOffset Expires)> byUser = new();

    public void Save(string userId, PendingLink link) => byUser[userId] = (link, clock.GetUtcNow() + Lifetime);

    public PendingLink? Get(string userId)
    {
        if (!byUser.TryGetValue(userId, out var entry)) return null;
        if (entry.Expires > clock.GetUtcNow()) return entry.Link;
        byUser.TryRemove(userId, out _);
        return null;
    }

    public void Clear(string userId) => byUser.TryRemove(userId, out _);
}

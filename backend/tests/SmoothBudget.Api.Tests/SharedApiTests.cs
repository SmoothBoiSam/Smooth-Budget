using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Shared;

namespace SmoothBudget.Api.Tests;

/// <summary>Friends, with or without accounts, and linking with a one-time invite (DECISIONS.md 205). All data invented.</summary>
public class SharedApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private void At(int day) => app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, day, 19, 0, 0, TimeSpan.Zero));

    private async Task<HttpClient> UserAsync(string? name = null)
    {
        var c = app.CreateClient();
        await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"), Json);
        if (name is not null)
            await c.PutAsJsonAsync("/api/shared/name", new SetNameRequest(name), Json);
        return c;
    }

    private static async Task<FriendDto> AddAsync(HttpClient c, string name)
    {
        var res = await c.PostAsJsonAsync("/api/shared/friends", new SetNameRequest(name), Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<FriendDto>(Json))!;
    }

    private static Task<HttpResponseMessage> InviteFor(HttpClient c, Guid friendId) => c.PostAsJsonAsync($"/api/shared/friends/{friendId}/invite", new { }, Json);

    private static async Task<InviteCreatedDto> InviteAsync(HttpClient c, Guid friendId) =>
        (await (await InviteFor(c, friendId)).Content.ReadFromJsonAsync<InviteCreatedDto>(Json))!;

    private static async Task<SharedOverviewDto> OverviewAsync(HttpClient c) => (await c.GetFromJsonAsync<SharedOverviewDto>("/api/shared", Json))!;

    private static async Task<FriendDto> FriendAsync(HttpClient c, string name) => (await OverviewAsync(c)).Friends.Single(f => f.Name == name);

    private async Task<HttpClient> SignUpFromAsync(string token, string name)
    {
        var c = app.CreateClient();
        var reg = await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1", token, name), Json);
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);
        return c;
    }

    [Fact]
    public async Task A_friend_starts_as_a_name_and_an_invite_links_their_new_account()
    {
        At(9);
        var sam = await UserAsync();
        var jane = await AddAsync(sam, " Jane ");
        Assert.Equal(("Jane", false), (jane.Name, jane.Linked));
        // Names tell friends apart, so no two the same.
        Assert.Equal(HttpStatusCode.BadRequest, (await sam.PostAsJsonAsync("/api/shared/friends", new SetNameRequest("jane"), Json)).StatusCode);

        // A name of your own comes first, so the invite can say who it's from.
        Assert.Equal(HttpStatusCode.Conflict, (await InviteFor(sam, jane.Id)).StatusCode);
        await sam.PutAsJsonAsync("/api/shared/name", new SetNameRequest("Sam"), Json);
        var invite = await InviteAsync(sam, jane.Id);
        Assert.Equal(new DateTime(2026, 11, 16, 19, 0, 0), invite.ExpiresUtc);
        Assert.Equal(invite.ExpiresUtc, (await FriendAsync(sam, "Jane")).InviteExpiresUtc);

        // Only a hash is stored, never the token itself.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.FriendInvites.AnyAsync(i => i.TokenHash == invite.Token));
        }

        var peek = (await app.CreateClient().GetFromJsonAsync<InvitePeekDto>($"/api/shared/invites/{invite.Token}", Json))!;
        Assert.Equal((true, "Sam"), (peek.Valid, peek.FromName));

        // Signing up from the invite needs a name.
        var noName = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1", invite.Token), Json);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var her = await SignUpFromAsync(invite.Token, " Jane ");
        var mine = await FriendAsync(sam, "Jane");
        Assert.Equal((jane.Id, true, (DateTime?)null), (mine.Id, mine.Linked, mine.InviteExpiresUtc));
        var hers = await OverviewAsync(her);
        Assert.Equal("Jane", hers.YourName);
        Assert.Equal(("Sam", true), (hers.Friends.Single().Name, hers.Friends.Single().Linked));

        // Used up: it works once. Linked: no second invite.
        var again = (await app.CreateClient().GetFromJsonAsync<InvitePeekDto>($"/api/shared/invites/{invite.Token}", Json))!;
        Assert.Equal((false, "That invite has already been used."), (again.Valid, again.Problem));
        Assert.Equal(HttpStatusCode.Conflict, (await InviteFor(sam, jane.Id)).StatusCode);

        // Unlinking ends it for both of you; you keep each other as friends without accounts.
        Assert.Equal(HttpStatusCode.NoContent, (await her.DeleteAsync($"/api/shared/friends/{hers.Friends.Single().Id}/link")).StatusCode);
        Assert.False((await FriendAsync(sam, "Jane")).Linked);
        Assert.False((await FriendAsync(her, "Sam")).Linked);
    }

    [Fact]
    public async Task You_can_have_many_friends_linked_or_not()
    {
        At(9);
        var sam = await UserAsync("Sam");
        var jane = await AddAsync(sam, "Jane");
        var alex = await AddAsync(sam, "Alex");
        await AddAsync(sam, "Pat"); // never makes an account

        // Jane already kept a tab with "Sam" before her account was linked: that friend becomes linked, history and all.
        var her = await UserAsync("Jane");
        var hadSam = await AddAsync(her, "Sam");
        Assert.Equal(HttpStatusCode.NoContent, (await her.PostAsJsonAsync("/api/shared/join", new JoinRequest((await InviteAsync(sam, jane.Id)).Token), Json)).StatusCode);
        Assert.Equal((hadSam.Id, true), ((await FriendAsync(her, "Sam")).Id, (await FriendAsync(her, "Sam")).Linked));

        await SignUpFromAsync((await InviteAsync(sam, alex.Id)).Token, "Alex");

        var friends = (await OverviewAsync(sam)).Friends;
        Assert.Equal(["Alex", "Jane", "Pat"], friends.Select(f => f.Name));
        Assert.Equal([true, true, false], friends.Select(f => f.Linked));

        // Two accounts link once: a second friend entry for the same person can't link to her again.
        var again = await AddAsync(sam, "Jane (work)");
        var dup = await her.PostAsJsonAsync("/api/shared/join", new JoinRequest((await InviteAsync(sam, again.Id)).Token), Json);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // Removing a linked friend ends the link for both; she keeps Sam as a name.
        Assert.Equal(HttpStatusCode.NoContent, (await sam.DeleteAsync($"/api/shared/friends/{jane.Id}")).StatusCode);
        Assert.DoesNotContain((await OverviewAsync(sam)).Friends, f => f.Id == jane.Id);
        Assert.False((await FriendAsync(her, "Sam")).Linked);
    }

    [Fact]
    public async Task Every_friend_can_have_the_name_on_their_transfers_but_no_two_the_same()
    {
        At(9);
        var sam = await UserAsync("Sam");
        Task<HttpResponseMessage> Save(string? name, Guid? friendId = null) =>
            sam.PutAsJsonAsync("/api/settings/partner", new SmoothBudget.Api.Transactions.PartnerSettingsDto(name, friendId), Json);

        // Saved the old way (no friend given): a friend is made from it.
        Assert.Equal(HttpStatusCode.OK, (await Save("JANE Q DOE")).StatusCode);
        var jane = await FriendAsync(sam, "Jane");
        Assert.Equal("JANE Q DOE", jane.TransferName);

        // Any number of friends (DECISIONS.md 211), each with their own name.
        var pat = await AddAsync(sam, "Pat");
        Assert.Equal(HttpStatusCode.OK, (await Save("PAT SMITH", pat.Id)).StatusCode);
        Assert.Equal(("JANE Q DOE", "PAT SMITH"), ((await FriendAsync(sam, "Jane")).TransferName, (await FriendAsync(sam, "Pat")).TransferName));
        // Two the same couldn't be told apart.
        Assert.Equal(HttpStatusCode.Conflict, (await Save("pat smith", jane.Id)).StatusCode);

        // Clearing one leaves the other.
        Assert.Equal(HttpStatusCode.OK, (await Save(null, jane.Id)).StatusCode);
        Assert.Equal((null, "PAT SMITH"), ((await FriendAsync(sam, "Jane")).TransferName, (await FriendAsync(sam, "Pat")).TransferName));

        // Removing a friend stops tracking their transfers.
        Assert.Equal(HttpStatusCode.NoContent, (await sam.DeleteAsync($"/api/shared/friends/{pat.Id}")).StatusCode);
        var setting = (await sam.GetFromJsonAsync<SmoothBudget.Api.Transactions.PartnerSettingsDto>("/api/settings/partner", Json))!;
        Assert.Null(setting.PartnerName);
    }

    [Fact]
    public async Task An_invite_expires_after_a_week_and_a_wrong_one_does_nothing()
    {
        At(1);
        var sam = await UserAsync("Sam");
        var jane = await AddAsync(sam, "Jane");
        var invite = await InviteAsync(sam, jane.Id);
        At(9);
        var expired = await app.CreateClient().PostAsJsonAsync("/api/auth/register",
            new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1", invite.Token, "Jane"), Json);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        var still = await FriendAsync(sam, "Jane");
        Assert.Equal((false, (DateTime?)null), (still.Linked, still.InviteExpiresUtc));

        var wrong = (await app.CreateClient().GetFromJsonAsync<InvitePeekDto>("/api/shared/invites/not-a-real-token", Json))!;
        Assert.False(wrong.Valid);
    }

    [Fact]
    public async Task Not_with_your_own_invite_and_a_new_invite_replaces_the_old_one()
    {
        At(9);
        var sam = await UserAsync("Sam");
        var jane = await AddAsync(sam, "Jane");
        var first = await InviteAsync(sam, jane.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await sam.PostAsJsonAsync("/api/shared/join", new JoinRequest(first.Token), Json)).StatusCode);

        var second = await InviteAsync(sam, jane.Id);
        Assert.False((await app.CreateClient().GetFromJsonAsync<InvitePeekDto>($"/api/shared/invites/{first.Token}", Json))!.Valid);
        Assert.True((await app.CreateClient().GetFromJsonAsync<InvitePeekDto>($"/api/shared/invites/{second.Token}", Json))!.Valid);

        // Someone else's friend can't be touched.
        var other = await UserAsync("Alex");
        Assert.Equal(HttpStatusCode.NotFound, (await InviteFor(other, jane.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/shared/friends/{jane.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/shared/friends/{jane.Id}", new SetNameRequest("X"), Json)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await sam.PutAsJsonAsync($"/api/shared/friends/{jane.Id}", new SetNameRequest("Janie"), Json)).StatusCode);
        Assert.Equal(jane.Id, (await FriendAsync(sam, "Janie")).Id);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Shared;
using SmoothBudget.Api.Transactions;

namespace SmoothBudget.Api.Tests;

/// <summary>"Try the demo" (DECISIONS.md 213): a fresh copy of the fake data for each visitor, gone after a day.</summary>
public class DemoApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<(HttpClient C, MeResponse Me)> StartAsync()
    {
        var c = app.CreateClient();
        var res = await c.PostAsync("/api/auth/demo", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (c, (await res.Content.ReadFromJsonAsync<MeResponse>(Json))!);
    }

    [Fact]
    public async Task Each_visitor_gets_their_own_copy_from_the_same_start()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var (a, meA) = await StartAsync();
        var (b, meB) = await StartAsync();
        Assert.True(meA.Demo);
        Assert.NotEqual(meA.HouseholdId, meB.HouseholdId);
        Assert.True((await a.GetFromJsonAsync<MeResponse>("/api/auth/me", Json))!.Demo);

        // Both start with the same made-up data.
        var txA = (await a.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items;
        var txB = (await b.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items;
        Assert.NotEmpty(txA);
        Assert.Equal(txA.Count, txB.Count);

        // What one does doesn't touch the other.
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/api/shared/friends", new SetNameRequest("Robin"), Json)).StatusCode);
        Assert.DoesNotContain((await b.GetFromJsonAsync<SharedOverviewDto>("/api/shared", Json))!.Friends, f => f.Name == "Robin");
    }

    [Fact]
    public async Task Banks_invites_and_notifications_are_off_in_a_copy()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var (c, _) = await StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/plaid/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/push")).StatusCode);
        var friend = (await (await c.PostAsJsonAsync("/api/shared/friends", new SetNameRequest("Robin"), Json)).Content.ReadFromJsonAsync<FriendDto>(Json))!;
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"/api/shared/friends/{friend.Id}/invite", new { }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/shared/join", new JoinRequest("x"), Json)).StatusCode);
    }

    [Fact]
    public async Task A_copy_is_deleted_a_day_later()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var (_, old) = await StartAsync();
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero));
        await StartAsync(); // starting another clears out expired ones

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Households.AnyAsync(h => h.Id == old.HouseholdId));
        Assert.False(await db.Users.AnyAsync(u => u.HouseholdId == old.HouseholdId));
        Assert.False(await db.Transactions.AnyAsync(t => t.HouseholdId == old.HouseholdId));
    }

    [Fact]
    public async Task Screens_that_load_together_all_get_an_answer()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var (c, _) = await StartAsync();
        // Home, the tab badge and Tasks ask for the paychecks at the same moment on a copy nobody has opened yet. Each
        // keeps the predictions, and two at once used to deadlock in the database (DECISIONS.md 251).
        var paths = new[] { "/api/home/summary", "/api/payday", "/api/home/summary", "/api/payday", "/api/budget/summary", "/api/payday", "/api/debt", "/api/savings" };
        var results = await Task.WhenAll(paths.Select(p => c.GetAsync(p)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}

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

    [Fact]
    public async Task Refreshing_categories_from_the_bank_files_the_demo_the_same_way_again()
    {
        // DECISIONS.md 307: each demo transaction carries the bank's label for its category, as a bank's export does, so
        // "Refresh categories from bank" files it back where it was instead of leaving everything unfiled.
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
        var (c, _) = await StartAsync();
        async Task<(long, long, int, string)> Budget()
        {
            var s = (await c.GetFromJsonAsync<Budget.BudgetSummaryDto>("/api/budget/summary", Json))!;
            var lines = string.Join(", ", s.Groups.SelectMany(g => g.Categories).Select(x => $"{x.Name} {x.LimitCents}/{x.SpentCents}"));
            return (s.LeftToSpendCents, s.UnfiledSpendingCents, s.UncategorizedCount, lines);
        }
        var before = await Budget();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/budget/categories/reset-from-bank", null)).StatusCode);
        Assert.Equal(before, await Budget());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(20)]
    [InlineData(23)]
    [InlineData(36)] // Dec 6, the month's last day
    public async Task The_demos_cash_never_went_below_zero(int novemberDay)
    {
        // DECISIONS.md 307: the demo pays its cards what it charges, and today's balance follows from an opening balance and
        // every seeded row, so Cash, worked back from today, stays positive whatever day the demo starts.
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 19, 0, 0, TimeSpan.Zero).AddDays(novemberDay - 1));
        var (c, _) = await StartAsync();
        var cash = (await c.GetFromJsonAsync<Accounts.CashDto>("/api/accounts/cash", Json))!;
        Assert.True(cash.History.Min(p => p.BalanceCents) > 0, $"lowest: {cash.History.Min(p => p.BalanceCents)}");
    }
}

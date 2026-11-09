using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Budget;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Home;
using SmoothBudget.Api.Import;
using SmoothBudget.Api.Seeding;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Tests;

/// <summary>Milestone 7: the fixed contribution (SPEC section 3 rent example, Appendix D matching, late flag) and paying back without a tally (DECISIONS.md 100). All data invented.</summary>
public class PartnerApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string ChaseChecking = "Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #";
    private const string ChaseCard = "Transaction Date,Post Date,Description,Category,Type,Amount,Memo";

    private void At(int month, int day) => app.Clock.SetUtcNow(new DateTimeOffset(2026, month, day, 19, 0, 0, TimeSpan.Zero));

    /// <summary>A household with the standard categories, Rent at $1,650, partner "Jane Partner" named on Nov 7.</summary>
    private async Task<(HttpClient C, Guid Checking, Guid Rent, Guid Groceries)> SetupAsync()
    {
        At(11, 7);
        var c = app.CreateClient();
        var reg = await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var household = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.AddRange(DemoSeeder.DefaultCategories(household));
            await db.SaveChangesAsync();
        }
        await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto("Jane Partner"), Json);
        var cats = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!;
        var rent = cats.Single(x => x.Name == "Rent");
        await c.PutAsJsonAsync($"/api/budget/categories/{rent.Id}", new UpdateCategoryRequest("Rent", CategoryType.FixedBill, 1_650_00, null, false, "permanent"), Json);
        var checking = (await (await c.PostAsJsonAsync("/api/accounts", new { name = "Test Checking", type = "Checking", mask = "1111" }, Json))
            .Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;
        return (c, checking, rent.Id, cats.Single(x => x.Name == "Groceries").Id);
    }

    private static async Task ImportAsync(HttpClient c, Guid account, string header, params string[] rows)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(header + "\n" + string.Join("\n", rows), Encoding.UTF8, "text/csv"), "file", "x.csv");
        form.Add(new StringContent(account.ToString()), "accountId");
        var res = await c.PostAsync("/api/import/csv", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static string Zelle(int day, decimal amount, string reference) =>
        $"CREDIT,11/{day:00}/2026,\"Zelle payment from JANE PARTNER {reference}\",{amount:0.00},PARTNERFI_TO_CHASE,1000.00,,";

    private static async Task<BudgetSummaryDto> Budget(HttpClient c) => (await c.GetFromJsonAsync<BudgetSummaryDto>("/api/budget/summary", Json))!;
    private static BudgetCategoryLineDto Line(BudgetSummaryDto b, string name) => b.Groups.SelectMany(g => g.Categories).Single(x => x.Name == name);

    [Fact]
    public async Task Rent_example_the_800_offsets_rent_and_the_budget_comes_out_the_same_as_counting_it_as_income()
    {
        var (c, _, rent, _) = await SetupAsync();
        await c.PutAsJsonAsync("/api/budget/months/current/income", new IncomeRequest(2_460_86, 800_00), Json);
        var asIncome = await Budget(c);

        var res = await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00), Json);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var asOffset = await Budget(c);

        // SPEC section 3: the Rent limit is the full rent; the $800 offsets it; your net rent is rent minus $800.
        var r = Line(asOffset, "Rent");
        Assert.Equal((1_650_00L, 800_00L, 850_00L), (r.LimitCents, r.PartnerOffsetCents, r.LimitCents - r.PartnerOffsetCents));
        // Counted once either way: Left to Spend and income vs planned don't change.
        Assert.Equal((asIncome.LeftToSpendCents, asIncome.UnallocatedCents, asIncome.PlannedCents),
            (asOffset.LeftToSpendCents, asOffset.UnallocatedCents, asOffset.PlannedCents));
        Assert.Equal(800_00, asOffset.PartnerExpectedCents);
        Assert.Equal("expected", Assert.Single(asOffset.Contributions!).State); // counts from the start of the month
    }

    [Fact]
    public async Task The_largest_transfer_in_the_window_is_her_contribution_and_200_is_flagged_and_short()
    {
        var (c, checking, rent, _) = await SetupAsync();
        await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00), Json);
        At(11, 21);
        await ImportAsync(c, checking, ChaseChecking, Zelle(19, 42.50m, "A1"), Zelle(20, 200m, "A2"));

        var txns = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items;
        var two = txns.Single(t => t.Description.EndsWith("A2"));
        Assert.Equal(("PartnerContribution", true), (two.Kind, two.Unsure));       // $200 would otherwise look like paying back
        Assert.Equal("Jane's contribution toward Rent: $200.00, not the usual $800.00.", two.CategoryReason);
        Assert.Equal("PartnerTransfer", txns.Single(t => t.Description.EndsWith("A1")).Kind);

        var during = await Budget(c);
        var status = Assert.Single(during.Contributions!);
        Assert.Equal(("differentAmount", 200_00L, 0L), (status.State, status.ReceivedCents, during.PartnerShortfallCents)); // the rest may still come

        At(11, 25); // the window has passed: Left to Spend is recomputed without the missing $600
        var after = await Budget(c);
        Assert.Equal(600_00, after.PartnerShortfallCents);
        Assert.Equal(during.LeftToSpendCents - 600_00, after.LeftToSpendCents);
    }

    [Fact]
    public async Task Nothing_by_the_end_of_the_window_is_late_on_home_and_budget()
    {
        var (c, _, rent, _) = await SetupAsync();
        await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00), Json);
        At(11, 24);
        var onTime = (await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;
        Assert.Equal("expected", Assert.Single(onTime.Contributions!).State);

        At(11, 25);
        var home = (await c.GetFromJsonAsync<HomeSummaryDto>("/api/home/summary", Json))!;
        Assert.Equal(("late", 800_00L), (Assert.Single(home.Contributions!).State, home.PartnerShortfallCents));
        Assert.Equal(onTime.LeftToSpendCents - 800_00, home.LeftToSpendCents);
    }

    [Fact]
    public async Task Paying_you_back_for_groceries_nets_out_and_the_overview_shows_it()
    {
        var (c, checking, rent, groceries) = await SetupAsync();
        await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00), Json);
        At(11, 12);
        var card = (await (await c.PostAsJsonAsync("/api/accounts", new { name = "Test Card", type = "CreditCard", mask = "4321" }, Json))
            .Content.ReadFromJsonAsync<AccountDto>(Json))!.Id;
        await ImportAsync(c, card, ChaseCard, "11/10/2026,11/11/2026,FRESH MARKET,Groceries,Sale,-120.00,");
        await ImportAsync(c, checking, ChaseChecking, Zelle(11, 60m, "HALF"));

        // No split, no tally: the  counts in full until you label her $60 as paying you back for groceries.
        Assert.Equal(120_00, Line(await Budget(c), "Groceries").SpentCents);
        var half = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items.Single(t => t.Description.EndsWith("HALF"));
        Assert.Equal(groceries, half.SuggestedCategoryId);
        await c.PatchAsJsonAsync($"/api/transactions/{half.Id}", new UpdateTransactionRequest(groceries, false, null), Json);
        Assert.Equal(60_00, Line(await Budget(c), "Groceries").SpentCents);

        var o = (await c.GetFromJsonAsync<PartnerOverviewDto>("/api/partner", Json))!;
        Assert.Equal(("Rent", 800_00L, "expected"), (Assert.Single(o.Contributions).CategoryName, o.Contributions[0].AmountCents, o.Contributions[0].ThisMonth!.State));
        Assert.Contains(o.Recent, a => a.Purpose == "payback" && a.CategoryName == "Groceries" && a.AmountCents == -60_00);
    }

    [Fact]
    public async Task A_transfer_you_tag_as_her_contribution_counts_even_after_the_window()
    {
        var (c, checking, rent, _) = await SetupAsync();
        await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00), Json);
        At(11, 28);
        await ImportAsync(c, checking, ChaseChecking, Zelle(27, 800m, "LATE"));
        Assert.Equal("late", Assert.Single((await Budget(c)).Contributions!).State); // outside the window: not matched automatically

        var t = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items.Single(x => x.Description.EndsWith("LATE"));
        await c.PatchAsJsonAsync($"/api/transactions/{t.Id}", new UpdateTransactionRequest(null, false, null, PartnerPurpose: "contribution"), Json);
        var b = await Budget(c);
        Assert.Equal(("received", 0L), (Assert.Single(b.Contributions!).State, b.PartnerShortfallCents));
    }

    [Fact]
    public async Task Rent_she_sent_before_the_month_started_can_count_for_this_month_and_be_undone()
    {
        var (c, checking, rent, _) = await SetupAsync();
        await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00), Json);
        await ImportAsync(c, checking, ChaseChecking, Zelle(5, 800m, "EARLY")); // the budget month started Nov 6
        At(11, 25);
        var before = (await c.GetFromJsonAsync<PartnerOverviewDto>("/api/partner", Json))!;
        Assert.Equal("late", before.Contributions[0].ThisMonth!.State);
        var early = Assert.Single(before.EarlyCandidates);
        Assert.Equal((new DateOnly(2026, 11, 5), 800_00L), (early.Date, early.AmountCents));

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/partner/early/{early.Id}", null)).StatusCode);
        var b = await Budget(c);
        Assert.Equal(("received", 0L), (Assert.Single(b.Contributions!).State, b.PartnerShortfallCents));
        var after = (await c.GetFromJsonAsync<PartnerOverviewDto>("/api/partner", Json))!;
        Assert.Empty(after.EarlyCandidates);
        Assert.Equal(early.Id, Assert.Single(after.CountedEarly).Id);

        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/partner/early/{Guid.NewGuid()}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/partner/early/{early.Id}")).StatusCode);
        Assert.Equal("late", Assert.Single((await Budget(c)).Contributions!).State);
    }

    [Fact]
    public async Task Each_friend_has_their_own_transfers_and_contributions()
    {
        var (c, checking, rent, _) = await SetupAsync();
        var jane = (await c.GetFromJsonAsync<Shared.SharedOverviewDto>("/api/shared", Json))!.Friends.Single(f => f.Name == "Jane");
        var pat = (await (await c.PostAsJsonAsync("/api/shared/friends", new Shared.SetNameRequest("Pat"), Json)).Content.ReadFromJsonAsync<Shared.FriendDto>(Json))!;
        Assert.True((await c.PutAsJsonAsync("/api/settings/partner", new PartnerSettingsDto("PAT ROOMIE", pat.Id), Json)).IsSuccessStatusCode);
        var utilities = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/budget/categories", Json))!.Single(x => x.Name == "Utilities & internet").Id;

        // With two friends tracked, a contribution says whose it is.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00), Json)).StatusCode);
        Assert.True((await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00, FriendId: jane.Id), Json)).IsSuccessStatusCode);
        Assert.True((await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(utilities, 100_00, FriendId: pat.Id), Json)).IsSuccessStatusCode);

        At(11, 21);
        await ImportAsync(c, checking, ChaseChecking, Zelle(20, 800m, "J1"),
            "CREDIT,11/20/2026,\"Zelle payment from PAT ROOMIE P1\",100.00,PARTNERFI_TO_CHASE,1000.00,,");
        var txns = (await c.GetFromJsonAsync<TransactionListDto>("/api/transactions", Json))!.Items;
        var fromJane = txns.Single(t => t.Description.EndsWith("J1"));
        var fromPat = txns.Single(t => t.Description.EndsWith("P1"));
        Assert.Equal(("Jane", "PartnerContribution", "Jane's contribution toward Rent."), (fromJane.FriendName, fromJane.Kind, fromJane.CategoryReason));
        Assert.Equal(("Pat", "PartnerContribution", "Pat's contribution toward Utilities & internet."), (fromPat.FriendName, fromPat.Kind, fromPat.CategoryReason));

        // Both count in the budget, each under its own friend.
        var b = await Budget(c);
        Assert.Equal(["Jane received", "Pat received"], b.Contributions!.Select(s => $"{s.FriendName} {s.State}").Order());
        Assert.Equal(900_00, b.PartnerExpectedCents);
        Assert.Equal("Jane and Pat", b.PartnerName);

        // Each friend's page shows only theirs.
        var o = (await c.GetFromJsonAsync<PartnerOverviewDto>($"/api/partner?friendId={pat.Id}", Json))!;
        Assert.Equal(("PAT ROOMIE", "Utilities & internet", "received"), (o.PartnerName, Assert.Single(o.Contributions).CategoryName, o.Contributions[0].ThisMonth!.State));
        Assert.Equal(100_00L * -1, Assert.Single(o.Recent).AmountCents);

        // Removing Pat takes their contribution with them; Jane's stays.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/shared/friends/{pat.Id}")).StatusCode);
        Assert.Equal("Jane received", $"{Assert.Single((await Budget(c)).Contributions!).FriendName} {(await Budget(c)).Contributions![0].State}");
    }

    [Fact]
    public async Task Bad_arrangements_are_rejected()
    {
        var (c, _, rent, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 0), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(Guid.NewGuid(), 800_00), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/partner/contributions", new ArrangementRequest(rent, 800_00, 25, 18), Json)).StatusCode);
    }
}

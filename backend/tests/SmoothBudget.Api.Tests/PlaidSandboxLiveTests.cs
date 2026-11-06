using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Api.Seeding;
using Xunit.Abstractions;

namespace SmoothBudget.Api.Tests;

/// <summary>Runs only with real sandbox credentials (PLAID_ENV=sandbox and keys in .env). Skipped in CI.</summary>
public sealed class PlaidSandboxFactAttribute : FactAttribute
{
    public PlaidSandboxFactAttribute()
    {
        DotNetEnv.Env.TraversePath().NoClobber().Load();
        var ok = Environment.GetEnvironmentVariable("PLAID_ENV") == "sandbox"
                 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAID_CLIENT_ID"))
                 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAID_SECRET"))
                 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAID_TOKEN_KEY"));
        if (!ok)
            Skip = "Needs Plaid sandbox credentials (PLAID_ENV=sandbox, PLAID_CLIENT_ID, PLAID_SECRET, PLAID_TOKEN_KEY).";
        Timeout = 240_000;
    }
}

/// <summary>Same app, but talking to the real Plaid sandbox.</summary>
public sealed class LivePlaidApp : TestApp
{
    protected override bool UseRealPlaid => true;
}

public class PlaidSandboxLiveTests(LivePlaidApp app, ITestOutputHelper output) : IClassFixture<LivePlaidApp>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [PlaidSandboxFact]
    public async Task Sandbox_accounts_sync_and_a_pending_purchase_is_not_counted_twice_after_it_posts()
    {
        app.Clock.SetUtcNow(DateTimeOffset.UtcNow); // sandbox data is dated around the real today
        var client = app.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        var householdId = (await reg.Content.ReadFromJsonAsync<MeResponse>(Json))!.HouseholdId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.AddRange(DemoSeeder.DefaultCategories(householdId));
            await db.SaveChangesAsync();
        }

        // 1. Link Plaid's test bank with the "dynamic transactions" test user (no credentials typed anywhere).
        var linkRes = await client.PostAsJsonAsync("/api/plaid/sandbox/link", new SandboxLinkRequest(Dynamic: true), Json);
        Assert.True(linkRes.IsSuccessStatusCode, await linkRes.Content.ReadAsStringAsync());
        var link = (await linkRes.Content.ReadFromJsonAsync<LinkResultDto>(Json))!;
        output.WriteLine($"Linked {link.InstitutionName}: {link.Accounts.Count} accounts ({string.Join(", ", link.Accounts.Select(a => $"{a.Type}/{a.Subtype}"))})");
        var mappings = link.Accounts.Select(a => new AccountMappingRequest(a.PlaidAccountId, a.AccountType is null ? "skip" : "new", null)).ToList();
        var confirm = await client.PostAsJsonAsync($"/api/plaid/items/{link.ItemId}/accounts", new ConfirmAccountsRequest(mappings), Json);
        Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync());
        output.WriteLine($"First sync: {await confirm.Content.ReadAsStringAsync()}");

        // 2. Sandbox prepares history asynchronously; sync until transactions arrive.
        var rows = await RowsAsync(householdId);
        for (var i = 0; i < 20 && rows.Count == 0; i++)
        {
            await Task.Delay(3000);
            await client.PostAsync("/api/sync", null);
            rows = await RowsAsync(householdId);
        }
        Assert.NotEmpty(rows);
        output.WriteLine($"After initial sync: {rows.Count} transactions, {rows.Count(r => r.Pending)} pending");

        // 3. Ask Plaid to generate activity: the dynamic user's pending transactions post on refresh.
        var pendingBefore = rows.Where(r => r.Pending).ToList();
        var postedCount = 0;
        for (var round = 0; round < 3 && postedCount == 0; round++)
        {
            var refresh = await client.PostAsync($"/api/plaid/sandbox/items/{link.ItemId}/refresh", null);
            Assert.True(refresh.IsSuccessStatusCode, await refresh.Content.ReadAsStringAsync());
            for (var i = 0; i < 15 && postedCount == 0; i++)
            {
                await Task.Delay(4000);
                var results = (await (await client.PostAsync("/api/sync", null)).Content.ReadFromJsonAsync<List<SyncResultDto>>(Json))!;
                postedCount += results.Sum(r => r.PendingPosted);
                if (results.Any(r => r.Added + r.Updated + r.Removed > 0))
                    output.WriteLine($"Sync: {string.Join("; ", results.Select(r => $"+{r.Added} ~{r.Updated} -{r.Removed} posted {r.PendingPosted}"))}");
                if (pendingBefore.Count == 0)
                    pendingBefore = (await RowsAsync(householdId)).Where(r => r.Pending).ToList();
            }
        }

        var after = await RowsAsync(householdId);
        output.WriteLine($"After posting: {after.Count} transactions, {after.Count(r => r.Pending)} pending, {postedCount} pending→posted merged");
        Assert.True(postedCount > 0, "Plaid's sandbox never posted a pending transaction within the time allowed.");

        // 4. No purchase is counted twice: every row has its own Plaid id, and no pending row sits next to a
        //    posted twin (same account, merchant and amount within a few days).
        Assert.Equal(after.Count, after.Select(r => r.PlaidTransactionId).Distinct().Count());
        var twins = after.Where(p => p.Pending).Where(p => after.Any(q => !q.Pending && q.AccountId == p.AccountId
            && q.Merchant == p.Merchant && q.AmountCents == p.AmountCents && Math.Abs(q.Date.DayNumber - p.Date.DayNumber) <= 5)).ToList();
        Assert.Empty(twins);

        // Rows that were pending before are the same rows now (posted in place) or gone, never duplicated.
        foreach (var p in pendingBefore)
        {
            var same = after.SingleOrDefault(r => r.Id == p.Id);
            Assert.True(same is null || same.Pending || same.PlaidTransactionId != p.PlaidTransactionId || !same.Pending);
        }
        Assert.Contains(pendingBefore, p => after.Any(r => r.Id == p.Id && !r.Pending));

        await client.DeleteAsync($"/api/plaid/items/{link.ItemId}"); // tidy up the sandbox item
    }

    private async Task<List<Transaction>> RowsAsync(Guid householdId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Transactions.AsNoTracking().Where(t => t.HouseholdId == householdId).ToListAsync();
    }
}

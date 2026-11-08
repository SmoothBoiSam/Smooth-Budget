using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Plaid;
using SmoothBudget.Core.Security;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Plaid;

public record SyncResultDto(
    Guid ItemId, string? InstitutionName, bool Ok, int Added, int Updated, int Removed,
    int PendingPosted, int MatchedToCsv, int SkippedAsCsvHistory, string? Error);

/// <summary>One sync at a time per Plaid item, so two syncs never race on the same cursor.</summary>
public class PlaidItemLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    public SemaphoreSlim For(Guid itemId) => _locks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Pulls new, changed and removed transactions for one Plaid item with /transactions/sync and stores
/// them. All pages are fetched before anything is saved, then applied in one database transaction with
/// the new cursor, so a failure leaves the previous state and cursor intact.
/// </summary>
public class PlaidSyncService(CategorizationService categorization,
    AppDbContext db, PlaidClient plaid, SecretBox secrets, TransferDetectionService transfers,
    PlaidItemLocks locks, TimeProvider clock, ILogger<PlaidSyncService> log, Budget.BudgetRefreshTracker budgetTracker)
{
    public async Task<SyncResultDto> SyncAsync(Guid itemId, CancellationToken ct = default)
    {
        var gate = locks.For(itemId);
        await gate.WaitAsync(ct);
        try
        {
            return await SyncLockedAsync(itemId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SyncResultDto> SyncLockedAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.PlaidItems.SingleAsync(i => i.Id == itemId, ct);
        if (item.Status == PlaidItemStatus.PendingSetup)
            return Fail(item, "Choose which accounts to import first.");

        var now = clock.GetUtcNow().UtcDateTime;
        item.LastAttemptUtc = now;
        var accessToken = secrets.Decrypt(item.AccessTokenEncrypted);

        List<PlaidTransaction> added = [], modified = [];
        List<string> removed = [];
        List<PlaidAccount>? accountsSeen = null;
        string? cursor = item.Cursor;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                added.Clear(); modified.Clear(); removed.Clear();
                cursor = item.Cursor;
                while (true)
                {
                    var page = await plaid.SyncTransactionsAsync(accessToken, cursor, ct);
                    added.AddRange(page.Added);
                    modified.AddRange(page.Modified);
                    removed.AddRange(page.Removed.Select(r => r.TransactionId));
                    accountsSeen = page.Accounts ?? accountsSeen;
                    cursor = page.NextCursor;
                    if (!page.HasMore)
                        break;
                }
                break;
            }
            catch (PlaidException ex) when (ex.ErrorCode == "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION" && attempt < 3)
            {
                // Plaid's guidance: restart the whole pagination from the original cursor.
            }
            catch (PlaidException ex)
            {
                item.Status = ex.LoginRequired ? PlaidItemStatus.LoginRequired : PlaidItemStatus.Error;
                item.StatusMessage = ex.LoginRequired
                    ? "Your bank needs you to sign in again."
                    : Truncate(ex.DisplayMessage ?? $"Sync failed ({ex.ErrorCode}).", 500);
                await db.SaveChangesAsync(ct);
                log.LogWarning("Plaid sync failed for item {ItemId}: {ErrorType}/{ErrorCode}", item.Id, ex.ErrorType, ex.ErrorCode);
                return Fail(item, item.StatusMessage);
            }
        }

        var accounts = await db.Accounts.Where(a => a.PlaidItemId == item.Id && a.PlaidAccountId != null)
            .ToDictionaryAsync(a => a.PlaidAccountId!, ct);

        foreach (var pa in accountsSeen ?? [])
        {
            if (accounts.TryGetValue(pa.AccountId, out var acct) && pa.Balances.Current is { } current)
            {
                acct.CurrentBalanceCents = BalanceCents(acct.Type, current);
                if (pa.Balances.Limit is { } limit)
                    acct.CreditLimitCents = (long)Math.Round(limit * 100m, 0, MidpointRounding.AwayFromZero);
            }
        }

        // Only accounts the user chose to import.
        PlaidTxn? Map(PlaidTransaction t) => accounts.TryGetValue(t.AccountId, out var a) ? ToPlaidTxn(t) : null;
        var addedTxns = added.Select(Map).OfType<PlaidTxn>().ToList();
        var modifiedTxns = modified.Select(Map).OfType<PlaidTxn>().ToList();

        var referenced = addedTxns.Concat(modifiedTxns)
            .SelectMany(t => t.PendingTransactionId is null ? [t.TransactionId] : new[] { t.TransactionId, t.PendingTransactionId })
            .Concat(removed).Distinct().ToList();
        var stored = await db.Transactions
            .Where(t => t.PlaidTransactionId != null && referenced.Contains(t.PlaidTransactionId) && t.HouseholdId == item.HouseholdId)
            .ToListAsync(ct);
        var storedById = stored.ToDictionary(t => t.Id);

        var plan = PlaidSyncPlanner.Plan(
            stored.Select(t => new StoredPlaidRow(t.Id, t.PlaidTransactionId!, t.Pending)),
            addedTxns, modifiedTxns, removed);

        var categorize = await categorization.ContextAsync(item.HouseholdId, ct);

        int inserted = 0, adopted = 0, skipped = 0, posted = 0;
        var touchedDates = new List<DateOnly>();

        // New transactions: in accounts with CSV history, match against it first so nothing is counted twice.
        foreach (var group in plan.Inserts.GroupBy(t => t.AccountId))
        {
            var account = accounts[group.Key];
            var csvEntities = await db.Transactions
                .Where(t => t.AccountId == account.Id && t.PlaidTransactionId == null && t.Source == DataSource.Csv)
                .ToDictionaryAsync(t => t.Id, ct);
            var csvRows = csvEntities.Values.Select(t => new CsvRow(t.Id, t.Date, t.AmountCents)).ToList();
            // The CSV's period counts every CSV row, including ones matched to Plaid in earlier syncs.
            var csvRange = await db.Transactions
                .Where(t => t.AccountId == account.Id && t.Source == DataSource.Csv)
                .GroupBy(_ => 1)
                .Select(g => new { First = g.Min(t => t.Date), Last = g.Max(t => t.Date) })
                .SingleOrDefaultAsync(ct);

            foreach (var d in CsvOverlapMatcher.Match(group, csvRows, csvRange is null ? null : (csvRange.First, csvRange.Last)))
            {
                switch (d.Action)
                {
                    case OverlapAction.Adopt:
                        csvEntities[d.AdoptRowId!.Value].PlaidTransactionId = d.Txn.TransactionId; // keep the CSV row and its category
                        adopted++;
                        break;
                    case OverlapAction.Skip:
                        skipped++;
                        break;
                    default:
                        db.Transactions.Add(NewTransaction(item.HouseholdId, account, d.Txn, categorize, now));
                        touchedDates.Add(d.Txn.Date);
                        inserted++;
                        break;
                }
            }
        }

        foreach (var u in plan.Updates)
        {
            var row = storedById[u.RowId];
            var account = accounts[u.Data.AccountId];
            touchedDates.Add(row.Date);
            Apply(row, account, u.Data, categorize);
            touchedDates.Add(row.Date);
            if (u.ReplacesPending) posted++;
        }

        foreach (var id in plan.Deletes)
        {
            touchedDates.Add(storedById[id].Date);
            db.Transactions.Remove(storedById[id]);
        }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            if (touchedDates.Count > 0)
                await transfers.RunAsync(item.HouseholdId, touchedDates.Min(), touchedDates.Max(), ct);
            if (touchedDates.Count > 0)
                await categorization.MatchContributionsAsync(item.HouseholdId, ct); // which transfer was her contribution
            item.Cursor = cursor;
            item.LastSyncedUtc = now;
            item.Status = PlaidItemStatus.Active;
            item.StatusMessage = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        log.LogInformation("Plaid sync for item {ItemId}: {Inserted} new, {Updated} updated ({Posted} posted), {Deleted} removed, {Adopted} matched to CSV, {Skipped} skipped",
            item.Id, inserted, plan.Updates.Count, posted, plan.Deletes.Count, adopted, skipped);
        budgetTracker.MarkChanged(item.HouseholdId); // new transactions: the automatic budget refreshes
        return new SyncResultDto(item.Id, item.InstitutionName, true, inserted, plan.Updates.Count, plan.Deletes.Count, posted, adopted, skipped, null);
    }

    public static PlaidTxn ToPlaidTxn(PlaidTransaction t) => new(
        t.TransactionId, t.AccountId,
        (long)Math.Round(t.Amount * 100m, 0, MidpointRounding.AwayFromZero), // Plaid: positive = money out, like the app
        t.AuthorizedDate ?? t.Date, // the purchase date, as for card CSVs (SPEC section 3)
        t.Name, t.MerchantName, t.Pending, t.PendingTransactionId,
        t.PersonalFinanceCategory?.Primary, t.PersonalFinanceCategory?.Detailed, t.PersonalFinanceCategory?.ConfidenceLevel);

    public static long BalanceCents(AccountType type, decimal current)
    {
        var cents = (long)Math.Round(current * 100m, 0, MidpointRounding.AwayFromZero);
        return type == AccountType.CreditCard ? -cents : cents; // Plaid reports a card's balance owed as positive
    }

    private static Transaction NewTransaction(Guid householdId, Account account, PlaidTxn t, CategorizationContext categorize, DateTime now)
    {
        var row = new Transaction
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = account.Id, Merchant = "",
            Source = DataSource.Plaid, CreatedUtc = now,
        };
        Apply(row, account, t, categorize);
        return row;
    }

    private static void Apply(Transaction row, Account account, PlaidTxn t, CategorizationContext categorize)
    {
        var description = Core.Import.MerchantText.Sanitize(t.Name);
        row.PlaidTransactionId = t.TransactionId;
        row.Date = t.Date;
        row.AmountCents = t.AmountCents;
        row.Pending = t.Pending;
        row.Description = Truncate(description, 300);
        row.Merchant = Truncate(Core.Import.MerchantText.Normalize(Core.Import.MerchantText.Sanitize(t.MerchantName ?? t.Name)), 200);
        row.BankCategory = (t.CategoryDetailed ?? t.CategoryPrimary) is { } label ? Truncate(label, 100) : null;
        row.BankCategoryPrimary = t.CategoryPrimary is { } p ? Truncate(p, 60) : null;
        row.BankConfidence = t.CategoryConfidence is { } c ? Truncate(c, 20) : null;

        // Never override the user's own choices or what transfer detection already decided.
        if (row.CategorySource == CategorySource.User || row.IsTransfer)
            return;
        row.Kind = PlaidMapping.KindFor(account.Type == AccountType.CreditCard, t.AmountCents, t.CategoryPrimary, t.CategoryDetailed);
        categorize.Apply(row); // rules, your past choices, then Plaid's category (with its confidence); tags partner money
    }

    private static SyncResultDto Fail(PlaidItem item, string message) =>
        new(item.Id, item.InstitutionName, false, 0, 0, 0, 0, 0, 0, message);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

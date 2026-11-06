using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Transactions;
using SmoothBudget.Core.Import;
using SmoothBudget.Core.Plaid;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Import;

public record ImportResultDto(
    Guid BatchId,
    string Format,
    int RowCount,
    int Imported,
    int Duplicates,
    int Transfers,
    int Categorized,
    int NeedsCategory,
    IReadOnlyList<RowError> Errors,
    // Lines that are the same transaction as one your bank already synced through Plaid.
    int AlreadySynced = 0,
    // Lines with no exact match, dated well inside the period bank sync already covers.
    int CoveredBySync = 0);

public class ImportException(string message) : Exception(message);

/// <summary>
/// CSV import pipeline: parse → skip rows already in the account → categorize (rules, then bank category)
/// → save → detect card payments and own-account transfers across all of the household's accounts.
/// </summary>
public class ImportService(AppDbContext db, TransferDetectionService transfers, TimeProvider clock)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;

    public async Task<ImportResultDto> ImportAsync(Guid householdId, Guid accountId, string fileName, string csvText, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.HouseholdId == householdId, ct)
            ?? throw new ImportException("Account not found.");

        var parsed = BankCsvParser.Parse(csvText);
        if (parsed.Format == BankCsvFormat.Unknown)
            throw new ImportException(parsed.Errors.FirstOrDefault()?.Message ?? "Unrecognized file.");
        if (parsed.IsCardFormat != (account.Type == AccountType.CreditCard))
            throw new ImportException(
                $"This looks like a {Describe(parsed.Format)} export, but \"{account.Name}\" is a {account.Type switch { AccountType.CreditCard => "credit card", AccountType.Savings => "savings", _ => "checking" }} account.");
        if (parsed.Rows.Count == 0)
            throw new ImportException("No transactions found in the file." + (parsed.Errors.Count > 0 ? $" {parsed.Errors.Count} rows could not be read." : ""));

        // The same real account set up twice (one CSV account, one synced from the bank) would count
        // everything twice. Send the file to the synced account instead.
        if (account.PlaidItemId is null && account.Mask is not null)
        {
            var twin = await db.Accounts.FirstOrDefaultAsync(a => a.HouseholdId == householdId && a.Id != account.Id
                && a.PlaidItemId != null && a.Type == account.Type && a.Mask == account.Mask, ct);
            if (twin is not null)
                throw new ImportException(
                    $"\"{twin.Name}\" ••{twin.Mask} already syncs this account from your bank. Import the file into \"{twin.Name}\" instead so nothing is counted twice.");
        }

        var min = parsed.Rows.Min(r => r.Date);
        var max = parsed.Rows.Max(r => r.Date);
        var existing = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Fingerprint != null && t.Date >= min && t.Date <= max)
            .GroupBy(t => t.Fingerprint!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var (notYetImported, duplicates) = ImportDeduplicator.SelectNew(parsed.Rows, existing);

        // Rows Plaid already synced into this account (and no CSV line has claimed yet). A CSV line that
        // is one of them is not added again; the synced row is stamped with the line's fingerprint so
        // re-importing the same file later recognizes it too (DECISIONS.md 60).
        var synced = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Source == DataSource.Plaid && t.Fingerprint == null)
            .ToDictionaryAsync(t => t.Id, ct);
        var syncedRange = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Source == DataSource.Plaid)
            .GroupBy(_ => 1)
            .Select(g => new { First = g.Min(t => t.Date), Last = g.Max(t => t.Date) })
            .SingleOrDefaultAsync(ct);
        var fresh = new List<ParsedRow>();
        int alreadySynced = 0, coveredBySync = 0;
        foreach (var o in OverlapMatcher.Match(
                     notYetImported.Select(r => new OverlapItem<ParsedRow>(r, r.Date, r.AmountCents, Pending: false)),
                     synced.Values.Select(t => new CsvRow(t.Id, t.Date, t.AmountCents)).ToList(),
                     r => r.Fingerprint,
                     syncedRange is null ? null : (syncedRange.First, syncedRange.Last)))
        {
            switch (o.Action)
            {
                case OverlapAction.Adopt:
                    synced[o.AdoptRowId!.Value].Fingerprint = o.Item.Fingerprint;
                    alreadySynced++;
                    break;
                case OverlapAction.Skip:
                    coveredBySync++;
                    break;
                default:
                    fresh.Add(o.Item);
                    break;
            }
        }

        var rules = await db.CategoryRules.Where(r => r.HouseholdId == householdId)
            .Select(r => new MerchantRule(r.CategoryId, r.Pattern, r.CreatedUtc)).ToListAsync(ct);
        var categoryIds = await db.Categories.Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .ToDictionaryAsync(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var batch = new ImportBatch
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = accountId,
            FileName = Path.GetFileName(fileName) is { Length: > 0 } n ? n[..Math.Min(n.Length, 200)] : "upload.csv",
            Format = parsed.Format, ImportedUtc = now, RowCount = parsed.Rows.Count + parsed.Errors.Count,
            ImportedCount = fresh.Count, DuplicateCount = duplicates + alreadySynced + coveredBySync, ErrorCount = parsed.Errors.Count,
        };
        db.ImportBatches.Add(batch);

        var created = fresh.Select(r =>
        {
            var (categoryId, source) = Categorizer.Categorize(r.Merchant, r.BankCategory, rules, categoryIds);
            return new Transaction
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, AccountId = accountId, ImportBatchId = batch.Id,
                Date = r.Date, AmountCents = r.AmountCents, Merchant = Truncate(r.Merchant, 200), Description = Truncate(r.Description, 300),
                Kind = r.Kind, BankCategory = r.BankCategory is null ? null : Truncate(r.BankCategory, 100),
                CategoryId = categoryId, CategorySource = source, Fingerprint = r.Fingerprint, Source = DataSource.Csv, CreatedUtc = now,
            };
        }).ToList();
        db.Transactions.AddRange(created);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            if (created.Count > 0)
                await transfers.RunAsync(householdId, created.Min(t => t.Date), created.Max(t => t.Date), ct);
            await tx.CommitAsync(ct);
        }

        return new ImportResultDto(
            batch.Id, Describe(parsed.Format), batch.RowCount, created.Count, duplicates,
            Transfers: created.Count(t => t.IsTransfer),
            Categorized: created.Count(t => t.CategoryId is not null && !t.IsTransfer),
            NeedsCategory: created.Count(t => TransactionRules.NeedsCategory(t.CategoryId is not null, t.IsTransfer, t.Kind, t.AmountCents)),
            parsed.Errors, alreadySynced, coveredBySync);
    }

    /// <summary>Removes everything one import added.</summary>
    public async Task<bool> UndoAsync(Guid householdId, Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId && b.HouseholdId == householdId, ct);
        if (batch is null)
            return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Transactions.Where(t => t.ImportBatchId == batchId).ExecuteDeleteAsync(ct);
        db.ImportBatches.Remove(batch);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public static string Describe(BankCsvFormat format) => format switch
    {
        BankCsvFormat.ChaseDebit => "Chase checking/savings",
        BankCsvFormat.ChaseCredit => "Chase credit card",
        BankCsvFormat.AppleCard => "Apple Card",
        _ => "unknown",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

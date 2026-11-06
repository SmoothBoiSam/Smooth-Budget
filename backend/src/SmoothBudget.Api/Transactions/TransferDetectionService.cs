using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

/// <summary>Runs <see cref="TransferDetector"/> over a date window of one household's transactions and saves the result.</summary>
public class TransferDetectionService(AppDbContext db)
{
    /// <summary>Looks this far around imported dates so a payment can pair with a row from an earlier import.</summary>
    public const int PaddingDays = TransferDetector.MatchWindowDays + 2;

    public async Task RunAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var start = from.AddDays(-PaddingDays);
        var end = to.AddDays(PaddingDays);
        var txns = await db.Transactions
            .Include(t => t.Account)
            .Where(t => t.HouseholdId == householdId && t.Date >= start && t.Date <= end)
            .ToListAsync(ct);
        if (txns.Count == 0)
            return;

        var candidates = txns.Select(t => new TransferCandidate(
            t.Id, t.AccountId, ToKind(t.Account.Type), t.Date, t.AmountCents, t.Kind,
            $"{t.Description} {t.Merchant}", t.TransferGroupId, t.CategorySource == CategorySource.User)).ToList();

        var categories = await db.Categories
            .Where(c => c.HouseholdId == householdId && !c.IsHidden)
            .OrderBy(c => c.SortOrder)
            .Select(c => new { c.Id, c.Type })
            .ToListAsync(ct);
        var debtCategory = categories.FirstOrDefault(c => c.Type == CategoryType.DebtPayment)?.Id;
        var savingsCategory = categories.FirstOrDefault(c => c.Type == CategoryType.SavingsTransfer)?.Id;

        var byId = txns.ToDictionary(t => t.Id);
        foreach (var d in TransferDetector.Detect(candidates))
        {
            var t = byId[d.Id];
            t.IsTransfer = true;
            t.TransferGroupId = d.GroupId ?? t.TransferGroupId;
            t.Kind = d.Role is TransferRole.CardPaymentSent or TransferRole.CardPaymentReceived
                ? TransactionKind.CardPayment
                : TransactionKind.Transfer;
            t.CategoryId = d.Role switch
            {
                // SPEC section 3: the card payment counts as a debt payment, not new spending.
                TransferRole.CardPaymentSent => debtCategory,
                TransferRole.OwnTransferOut when d.CounterpartAccount == AccountKind.Savings => savingsCategory,
                _ => null,
            };
            t.CategorySource = t.CategoryId is null ? CategorySource.None : CategorySource.Transfer;
        }
        await db.SaveChangesAsync(ct);
    }

    public static AccountKind ToKind(AccountType type) => type switch
    {
        AccountType.CreditCard => AccountKind.CreditCard,
        AccountType.Savings => AccountKind.Savings,
        _ => AccountKind.Checking,
    };
}

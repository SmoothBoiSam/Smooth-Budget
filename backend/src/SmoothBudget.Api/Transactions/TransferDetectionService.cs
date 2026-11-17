using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Transactions;

/// <summary>
/// Runs <see cref="TransferDetector"/> over a date window of one household's transactions and saves the result. A newly
/// found transfer is only the most likely answer (DECISIONS.md 259): it waits in Review like everything else, filed as a
/// transfer with the account its other half is on, until you confirm one half. Then the other half follows on its own
/// (<see cref="TransferFiling.CompleteAsync"/>), which this runs at the end too.
/// </summary>
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

        // Settled by you (or your best guess) is never changed. Still waiting is what can look like a transfer by amount alone.
        static bool Locked(Transaction t) => t.CategorySource is CategorySource.User or CategorySource.BestGuess;
        var candidates = txns.Select(t => new TransferCandidate(
            t.Id, t.AccountId, ToKind(t.Account.Type), t.Date, t.AmountCents, t.Kind,
            $"{t.Description} {t.Merchant}", t.TransferGroupId, Locked(t),
            Waiting: !Locked(t) && !t.IsTransfer && (t.NeedsReview || TransactionRules.NeedsCategory(t.CategoryId is not null, t.IsTransfer, t.Kind, t.AmountCents)))).ToList();

        var filing = new TransferFiling(db, householdId);
        var byId = txns.ToDictionary(t => t.Id);
        foreach (var d in TransferDetector.Detect(candidates))
        {
            var t = byId[d.Id];
            var wasTransfer = t.IsTransfer;
            var waiting = t.NeedsReview;
            if (wasTransfer && !waiting)
            {
                // Settled already: its filing stays as it is, it only learns which account its other half is on.
                t.TransferAccountId ??= d.CounterpartAccountId;
                t.TransferGroupId = d.GroupId ?? t.TransferGroupId;
                continue;
            }
            var name = await filing.FileAsync(t, d.CounterpartAccountId, null, null, create: false, ct);
            if (d.Role is TransferRole.CardPaymentSent or TransferRole.CardPaymentReceived)
                t.Kind = TransactionKind.CardPayment;
            t.TransferGroupId = d.GroupId ?? t.TransferGroupId;
            t.CategorySource = t.CategoryId is null ? CategorySource.None : CategorySource.Transfer;
            t.CategoryUnsure = false;
            // New: the most likely answer, waiting for you. Already a transfer before: left as it was settled.
            t.NeedsReview = wasTransfer ? waiting : true;
            if (!wasTransfer || t.CategoryReason is null)
                t.CategoryReason = d.CounterpartAccountId is null
                    ? "Looks like a transfer between your accounts."
                    : $"Looks like a transfer {(t.AmountCents > 0 ? "to" : "from")} {name}: the same amount {(t.AmountCents > 0 ? "landed there" : "left there")}.";
        }
        await db.SaveChangesAsync(ct);
        await TransferFiling.CompleteAsync(db, householdId, ct);
    }

    public static AccountKind ToKind(AccountType type) => type switch
    {
        AccountType.CreditCard => AccountKind.CreditCard,
        AccountType.Savings => AccountKind.Savings,
        _ => AccountKind.Checking,
    };
}

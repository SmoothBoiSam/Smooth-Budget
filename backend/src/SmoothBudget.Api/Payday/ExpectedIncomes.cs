using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Core.Pay;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Api.Payday;

/// <param name="LandedDate">The day the deposit it matched came in; null while it's still to come.</param>
/// <param name="Late">Not seen by <see cref="ExpectedIncomes.LateDays"/> days after it was due: the budget stops counting it.</param>
public record ExpectedIncomeDto(Guid Id, string Name, long AmountCents, DateOnly ExpectedDate, DateOnly? LandedDate, long? LandedCents, bool Late);
public record ExpectedIncomeRequest(string? Name, long AmountCents, DateOnly ExpectedDate);

/// <summary>
/// Income you expect apart from your paychecks, like a bonus (DECISIONS.md 256). Until it lands, the budget month it's due
/// in counts it with your paychecks. When a deposit arrives around that day for about that much (or one you marked as
/// other income), it's matched: the deposit counts as other income from then on, so it's never counted twice, and it
/// waits in Review for you to confirm (DECISIONS.md 257).
/// </summary>
public static class ExpectedIncomes
{
    /// <summary>How early and how late a deposit can land and still be the one you expected.</summary>
    public const int EarlyDays = 5, LateDays = 10;
    /// <summary>How far back bonuses are listed (on Income, and in Review's Bonus dropdown).</summary>
    public const int ShownDays = 90;

    public static async Task<List<ExpectedIncomeDto>> ListAsync(AppDbContext db, Guid householdId, DateOnly today, CancellationToken ct)
    {
        var rows = await db.ExpectedIncomes.AsNoTracking().Where(x => x.HouseholdId == householdId && x.ExpectedDate >= today.AddDays(-ShownDays))
            .OrderBy(x => x.ExpectedDate).ToListAsync(ct);
        var ids = rows.Where(x => x.DepositTransactionId != null).Select(x => x.DepositTransactionId!.Value).ToList();
        var deposits = await db.Transactions.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => (t.Date, Cents: -t.AmountCents), ct);
        return rows.Select(x =>
        {
            // A deposit whose account you deleted (DECISIONS.md 318) still landed: on the day it was due, for what was expected.
            var landed = x.DepositTransactionId is not { } id ? ((DateOnly, long)?)null
                : deposits.TryGetValue(id, out var d) ? d : (x.ExpectedDate, x.AmountCents);
            return new ExpectedIncomeDto(x.Id, x.Name, x.AmountCents, x.ExpectedDate, landed?.Item1, landed?.Item2,
                landed is null && today > x.ExpectedDate.AddDays(LateDays));
        }).ToList();
    }

    /// <summary>What's still to come in a budget month: due inside it, not landed, not late.</summary>
    public static async Task<List<ExpectedIncome>> StillComingAsync(AppDbContext db, Guid householdId, DateOnly start, DateOnly end, DateOnly today, CancellationToken ct) =>
        await db.ExpectedIncomes.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.DepositTransactionId == null && x.ExpectedDate >= start && x.ExpectedDate <= end
                        && x.ExpectedDate >= today.AddDays(-LateDays))
            .OrderBy(x => x.ExpectedDate).ToListAsync(ct);

    /// <summary>
    /// Matches expected income to deposits, after paychecks are matched. One you marked as other income in Review around
    /// that day is taken first, whatever its amount; otherwise the closest in amount within a quarter (or $50). A deposit
    /// that's since been marked as something else lets go of it.
    /// </summary>
    public static async Task MatchAsync(AppDbContext db, Guid householdId, CancellationToken ct)
    {
        var all = await db.ExpectedIncomes.Where(x => x.HouseholdId == householdId).ToListAsync(ct);
        if (all.Count == 0) return;
        var changed = false;

        var linkedIds = all.Where(x => x.DepositTransactionId != null).Select(x => x.DepositTransactionId!.Value).ToList();
        // Let go only of a deposit since marked as something else. One that's gone with an account you deleted (DECISIONS.md
        // 318) still landed: let go, the bonus would be expected all over again.
        var notIncome = (await db.Transactions.AsNoTracking()
            .Where(t => linkedIds.Contains(t.Id) && !(t.Kind == TransactionKind.Income && t.IncomeType != IncomeType.Paycheck && !t.IsTransfer))
            .Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        foreach (var x in all.Where(x => x.DepositTransactionId is { } id && notIncome.Contains(id)))
        {
            x.DepositTransactionId = null;
            changed = true;
        }

        var open = all.Where(x => x.DepositTransactionId == null).OrderBy(x => x.ExpectedDate).ToList();
        if (open.Count > 0)
        {
            var from = open[0].ExpectedDate.AddDays(-EarlyDays);
            var to = open[^1].ExpectedDate.AddDays(LateDays);
            var taken = all.Where(x => x.DepositTransactionId != null).Select(x => x.DepositTransactionId!.Value).ToHashSet();
            var paychecks = (await db.PayPeriodForecasts.AsNoTracking().Where(f => f.HouseholdId == householdId && f.DepositTransactionId != null)
                .Select(f => f.DepositTransactionId!.Value).ToListAsync(ct)).ToHashSet();
            var deposits = await db.Transactions
                .Where(t => t.HouseholdId == householdId && t.Kind == TransactionKind.Income && t.AmountCents < 0 && !t.IsTransfer && !t.IsPartnerTransfer
                            && t.CategorySource != CategorySource.User && t.Account.IncludedInBudget && t.Account.Type != AccountType.CreditCard
                            && t.Date >= from && t.Date <= to)
                .ToListAsync(ct);
            foreach (var x in open)
            {
                var pick = deposits
                    .Where(d => !taken.Contains(d.Id) && d.Date >= x.ExpectedDate.AddDays(-EarlyDays) && d.Date <= x.ExpectedDate.AddDays(LateDays))
                    // One you settled yourself is never taken: in Review you pick the bonus it is (DECISIONS.md 259).
                    .Select(d => (d, Off: Math.Abs(-d.AmountCents - x.AmountCents)))
                    .Where(c => !paychecks.Contains(c.d.Id) && c.Off <= Math.Max(x.AmountCents / 4, 50_00))
                    .OrderBy(c => c.Off).Select(c => c.d).FirstOrDefault();
                if (pick is null) continue;
                x.DepositTransactionId = pick.Id;
                taken.Add(pick.Id);
                if (pick.CategorySource != CategorySource.User)
                {
                    // It's the income you said was coming: other income, and it waits in Review for you to confirm
                    // (DECISIONS.md 257). Marked as anything else there, it lets go of the bonus.
                    pick.IncomeType = IncomeType.Other;
                    pick.NeedsReview = true;
                    pick.CategoryReason = $"Looks like your {x.Name} ({PayEngine.Dollars(x.AmountCents)} expected around {x.ExpectedDate:MMM d}).";
                }
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }
}

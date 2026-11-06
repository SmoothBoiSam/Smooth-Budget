namespace SmoothBudget.Core.Debt;

public enum DebtStrategy
{
    /// <summary>Extra money goes to the highest APR first (the owner's choice, SPEC section 3). Least interest.</summary>
    Avalanche,
    /// <summary>Extra money goes to the smallest balance first. Shown as a comparison.</summary>
    Snowball,
}

/// <param name="BalanceCents">What's owed, positive.</param>
/// <param name="AprBps">Annual percentage rate in basis points: 2499 = 24.99%.</param>
/// <param name="MinimumCents">The minimum payment each month (capped at what's owed).</param>
public sealed record DebtInput(Guid Id, string Name, long BalanceCents, int AprBps, long MinimumCents);

/// <summary>One month of the plan: what was owed at the end of it, the interest charged and what was paid.</summary>
public sealed record PayoffMonth(int Month, long TotalBalanceCents, long InterestCents, long PaidCents, IReadOnlyDictionary<Guid, long> BalancesCents);

/// <param name="PaidOffInMonth">1-based month in which it reached $0 (null if it never does within the horizon).</param>
public sealed record DebtOutcome(Guid Id, string Name, int? PaidOffInMonth, long InterestCents, long PaidCents);

/// <param name="Months">Months until every debt is $0 (or the horizon, when it never gets there).</param>
/// <param name="NeverPaysOff">True when the payments don't outrun the interest.</param>
/// <param name="MonthlyPaymentCents">Minimums plus extra: the same total every month until the last debt is gone.</param>
public sealed record PayoffResult(
    DebtStrategy Strategy, long MonthlyPaymentCents, int Months, bool NeverPaysOff, long TotalInterestCents, long TotalPaidCents,
    IReadOnlyList<DebtOutcome> Debts, IReadOnlyList<PayoffMonth> Schedule);

/// <summary>
/// The debt payoff engine (SPEC 4.4). Each month, for every debt:
/// <list type="number">
/// <item>Interest accrues on the balance at APR ÷ 12 (monthly compounding), rounded to the cent, half away from zero.</item>
/// <item>Every debt gets its minimum payment (or what's left of it, if less).</item>
/// <item>The rest of the monthly payment (the extra, plus minimums freed up by debts already paid off) goes to
/// the target debt: highest APR first for Avalanche (smaller balance breaks ties), smallest balance first for
/// Snowball (higher APR breaks ties). Whatever the target can't absorb moves to the next one.</item>
/// </list>
/// The total paid each month stays the same until everything is paid, which is what makes the "rollover" work.
/// Results match standard amortization tables to the cent (see the tests).
/// </summary>
public static class DebtPayoff
{
    /// <summary>50 years. A plan that isn't done by then is reported as never paying off.</summary>
    public const int HorizonMonths = 600;

    public static PayoffResult Simulate(IReadOnlyList<DebtInput> debts, long extraCents, DebtStrategy strategy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(extraCents);
        foreach (var d in debts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(d.BalanceCents);
            ArgumentOutOfRangeException.ThrowIfNegative(d.AprBps);
            ArgumentOutOfRangeException.ThrowIfNegative(d.MinimumCents);
        }

        var balance = debts.ToDictionary(d => d.Id, d => d.BalanceCents);
        var interest = debts.ToDictionary(d => d.Id, _ => 0L);
        var paid = debts.ToDictionary(d => d.Id, _ => 0L);
        var doneIn = new Dictionary<Guid, int>();
        foreach (var d in debts.Where(d => d.BalanceCents == 0))
            doneIn[d.Id] = 0;
        var monthly = debts.Sum(d => d.MinimumCents) + extraCents;
        var schedule = new List<PayoffMonth>();
        var month = 0;

        while (balance.Values.Any(b => b > 0) && month < HorizonMonths)
        {
            month++;
            long monthInterest = 0, monthPaid = 0;
            foreach (var d in debts.Where(d => balance[d.Id] > 0))
            {
                var i = MonthlyInterest(balance[d.Id], d.AprBps);
                balance[d.Id] += i;
                interest[d.Id] += i;
                monthInterest += i;
            }

            var available = monthly;
            foreach (var d in debts.Where(d => balance[d.Id] > 0))
            {
                var p = Math.Min(Math.Min(d.MinimumCents, balance[d.Id]), available);
                Pay(d.Id, p);
            }
            foreach (var d in Order(debts.Where(d => balance[d.Id] > 0), balance, strategy))
            {
                if (available == 0) break;
                Pay(d.Id, Math.Min(available, balance[d.Id]));
            }

            foreach (var d in debts.Where(d => balance[d.Id] == 0 && !doneIn.ContainsKey(d.Id)))
                doneIn[d.Id] = month;
            schedule.Add(new PayoffMonth(month, balance.Values.Sum(), monthInterest, monthPaid, new Dictionary<Guid, long>(balance)));

            // A plan whose payments can't cover the interest grows forever; stop early and say so.
            if (month >= 12 && schedule[^1].TotalBalanceCents >= schedule[^12].TotalBalanceCents)
                break;

            void Pay(Guid id, long cents)
            {
                balance[id] -= cents;
                paid[id] += cents;
                available -= cents;
                monthPaid += cents;
            }
        }

        var never = balance.Values.Any(b => b > 0);
        return new PayoffResult(strategy, monthly, month, never, interest.Values.Sum(), paid.Values.Sum(),
            debts.Select(d => new DebtOutcome(d.Id, d.Name, doneIn.TryGetValue(d.Id, out var m) ? m : null, interest[d.Id], paid[d.Id])).ToList(),
            schedule);
    }

    /// <summary>The order extra money goes in. Ties fall back to the other rule, then the name, so it's stable.</summary>
    public static IEnumerable<DebtInput> Order(IEnumerable<DebtInput> debts, IReadOnlyDictionary<Guid, long> balances, DebtStrategy strategy) =>
        strategy == DebtStrategy.Avalanche
            ? debts.OrderByDescending(d => d.AprBps).ThenBy(d => balances[d.Id]).ThenBy(d => d.Name, StringComparer.Ordinal)
            : debts.OrderBy(d => balances[d.Id]).ThenByDescending(d => d.AprBps).ThenBy(d => d.Name, StringComparer.Ordinal);

    /// <summary>One month of interest: balance × APR ÷ 12, to the cent, half away from zero.</summary>
    public static long MonthlyInterest(long balanceCents, int aprBps) =>
        (long)Math.Round(balanceCents * (decimal)aprBps / 120_000m, 0, MidpointRounding.AwayFromZero);
}

namespace SmoothBudget.Core.Plaid;

/// <summary>A row already stored in the account, from the other source.</summary>
public sealed record CsvRow(Guid RowId, DateOnly Date, long AmountCents);

public enum OverlapAction
{
    /// <summary>New transaction: store it.</summary>
    Insert,
    /// <summary>The same transaction is already stored from the other source: link to it instead of adding a copy.</summary>
    Adopt,
    /// <summary>Dated well inside the period the other source already covers, with no match: that source is the record, skip it.</summary>
    Skip,
}

public sealed record OverlapDecision(PlaidTxn Txn, OverlapAction Action, Guid? AdoptRowId);

/// <summary>An incoming row from either source: a Plaid transaction or a CSV line.</summary>
public sealed record OverlapItem<T>(T Item, DateOnly Date, long AmountCents, bool Pending);

public sealed record OverlapResult<T>(T Item, OverlapAction Action, Guid? AdoptRowId);

/// <summary>
/// Stops double counting when the same account gets transactions from both a CSV and Plaid, in
/// either order (SPEC: "a CSV-sourced account must look identical to a Plaid account").
/// <list type="bullet">
/// <item>An incoming transaction matches an existing row from the other source when the amount is the
/// same and the dates are within <see cref="MatchDays"/> days (CSV posting dates and Plaid purchase dates
/// differ by a few days). The closest date wins, and each existing row is matched at most once.</item>
/// <item>Pending transactions never appear in a CSV, so a pending Plaid transaction is always new.</item>
/// <item>Unmatched and outside the period the existing rows cover, or near its edges (<see cref="MatchDays"/>
/// at the start, <see cref="EdgeDays"/> at the end): new. Unmatched and well inside that period: skipped,
/// because the other source already covers it.</item>
/// </list>
/// </summary>
public static class OverlapMatcher
{
    public const int MatchDays = 5;
    public const int EdgeDays = 7;

    /// <param name="existing">Rows from the other source that are still free to be matched.</param>
    /// <param name="covered">
    /// The date range the other source covers, from all its rows in the account, including ones already
    /// matched earlier. Defaults to the range of <paramref name="existing"/>.
    /// </param>
    public static IReadOnlyList<OverlapResult<T>> Match<T>(IEnumerable<OverlapItem<T>> incoming, IReadOnlyCollection<CsvRow> existing,
        Func<T, string> tieBreak, (DateOnly First, DateOnly Last)? covered = null)
    {
        var list = incoming.ToList();
        if (covered is null && existing.Count == 0)
            return list.Select(i => new OverlapResult<T>(i.Item, OverlapAction.Insert, null)).ToList();

        var first = (covered?.First ?? existing.Min(r => r.Date)).DayNumber;
        var last = (covered?.Last ?? existing.Max(r => r.Date)).DayNumber;
        var available = existing.ToList();
        var result = new List<OverlapResult<T>>();

        foreach (var i in list.OrderBy(i => i.Date).ThenBy(i => tieBreak(i.Item), StringComparer.Ordinal))
        {
            var day = i.Date.DayNumber;
            if (i.Pending || day < first - MatchDays || day > last + MatchDays)
            {
                result.Add(new OverlapResult<T>(i.Item, OverlapAction.Insert, null));
                continue;
            }

            var match = available
                .Where(r => r.AmountCents == i.AmountCents && Math.Abs(r.Date.DayNumber - day) <= MatchDays)
                .OrderBy(r => Math.Abs(r.Date.DayNumber - day))
                .ThenBy(r => r.Date)
                .ThenBy(r => r.RowId)
                .FirstOrDefault();
            if (match is not null)
            {
                available.Remove(match);
                result.Add(new OverlapResult<T>(i.Item, OverlapAction.Adopt, match.RowId));
                continue;
            }

            // The end edge is wider: transactions can post after an export was downloaded or a sync ran.
            var nearEdge = day < first + MatchDays || day > last - EdgeDays;
            result.Add(new OverlapResult<T>(i.Item, nearEdge ? OverlapAction.Insert : OverlapAction.Skip, null));
        }
        return result;
    }
}

/// <summary>Plaid transactions arriving in an account that already has CSV history.</summary>
public static class CsvOverlapMatcher
{
    public const int MatchDays = OverlapMatcher.MatchDays;
    public const int EdgeDays = OverlapMatcher.EdgeDays;

    public static IReadOnlyList<OverlapDecision> Match(IEnumerable<PlaidTxn> incoming, IReadOnlyCollection<CsvRow> csvRows,
        (DateOnly First, DateOnly Last)? covered = null) =>
        OverlapMatcher.Match(incoming.Select(t => new OverlapItem<PlaidTxn>(t, t.Date, t.AmountCents, t.Pending)), csvRows, t => t.TransactionId, covered)
            .Select(r => new OverlapDecision(r.Item, r.Action, r.AdoptRowId))
            .ToList();
}

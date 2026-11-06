namespace SmoothBudget.Core.Plaid;

/// <summary>A CSV-imported row in an account that is now linked to Plaid.</summary>
public sealed record CsvRow(Guid RowId, DateOnly Date, long AmountCents);

public enum OverlapAction
{
    /// <summary>New transaction: store it.</summary>
    Insert,
    /// <summary>The same transaction is already there from a CSV: link that row to Plaid instead of adding a copy.</summary>
    Adopt,
    /// <summary>Dated well inside the period the CSV already covers, with no match: the CSV is the record, skip it.</summary>
    Skip,
}

public sealed record OverlapDecision(PlaidTxn Txn, OverlapAction Action, Guid? AdoptRowId);

/// <summary>
/// Stops double counting when an account has CSV history and is then linked to Plaid, which also
/// sends history (SPEC: "a CSV-sourced account must look identical to a Plaid account").
/// <list type="bullet">
/// <item>Pending transactions, and anything dated more than <see cref="MatchDays"/> days after the last CSV date, are new.</item>
/// <item>Otherwise a CSV row with the same amount within <see cref="MatchDays"/> days is the same
/// transaction (CSV posting dates and Plaid purchase dates differ by a few days); the closest date wins.</item>
/// <item>An unmatched transaction near the end of the CSV (<see cref="EdgeDays"/>) is kept, since it may have
/// posted after the export was downloaded. Older unmatched ones are skipped: that period is covered by the CSV.</item>
/// </list>
/// </summary>
public static class CsvOverlapMatcher
{
    public const int MatchDays = 5;
    public const int EdgeDays = 7;

    public static IReadOnlyList<OverlapDecision> Match(IEnumerable<PlaidTxn> incoming, IReadOnlyCollection<CsvRow> csvRows)
    {
        var list = incoming.ToList();
        if (csvRows.Count == 0)
            return list.Select(t => new OverlapDecision(t, OverlapAction.Insert, null)).ToList();

        var lastCsvDate = csvRows.Max(r => r.Date);
        var available = csvRows.ToList();
        var result = new List<OverlapDecision>();

        foreach (var t in list.OrderBy(t => t.Date).ThenBy(t => t.TransactionId, StringComparer.Ordinal))
        {
            // Pending ones never appear in a CSV, and anything well past the CSV's end can't be in it.
            // Near the end we still look for a match: a card purchase from the CSV's last day can carry a
            // later Plaid date.
            if (t.Pending || t.Date.DayNumber > lastCsvDate.DayNumber + MatchDays)
            {
                result.Add(new OverlapDecision(t, OverlapAction.Insert, null));
                continue;
            }

            var match = available
                .Where(r => r.AmountCents == t.AmountCents && Math.Abs(r.Date.DayNumber - t.Date.DayNumber) <= MatchDays)
                .OrderBy(r => Math.Abs(r.Date.DayNumber - t.Date.DayNumber))
                .ThenBy(r => r.Date)
                .ThenBy(r => r.RowId)
                .FirstOrDefault();
            if (match is not null)
            {
                available.Remove(match);
                result.Add(new OverlapDecision(t, OverlapAction.Adopt, match.RowId));
            }
            else
            {
                var nearEdge = t.Date.DayNumber > lastCsvDate.DayNumber - EdgeDays;
                result.Add(new OverlapDecision(t, nearEdge ? OverlapAction.Insert : OverlapAction.Skip, null));
            }
        }
        return result;
    }
}

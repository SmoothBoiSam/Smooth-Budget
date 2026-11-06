namespace SmoothBudget.Core.Import;

/// <summary>
/// Decides which rows of a CSV are new. Bank CSVs carry no transaction ids, so a row is identified
/// by its fingerprint (date, amount, description). Two genuinely identical purchases on the same day
/// are both kept: for each fingerprint we only insert as many rows as the file has beyond what the
/// account already holds. Re-importing an overlapping export therefore never duplicates anything.
/// </summary>
public static class ImportDeduplicator
{
    public static (IReadOnlyList<ParsedRow> New, int Duplicates) SelectNew(
        IEnumerable<ParsedRow> incoming,
        IReadOnlyDictionary<string, int> existingCountByFingerprint)
    {
        var seen = new Dictionary<string, int>();
        var fresh = new List<ParsedRow>();
        var duplicates = 0;

        foreach (var row in incoming)
        {
            var key = row.Fingerprint;
            var nth = seen[key] = seen.GetValueOrDefault(key) + 1;
            if (nth <= existingCountByFingerprint.GetValueOrDefault(key))
                duplicates++;
            else
                fresh.Add(row);
        }
        return (fresh, duplicates);
    }
}

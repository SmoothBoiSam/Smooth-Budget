namespace SmoothBudget.Core.Plaid;

/// <summary>
/// A transaction from Plaid's /transactions/sync, already in the app's conventions:
/// positive cents = money out (Plaid uses the same sign), and <see cref="Date"/> is the purchase
/// (authorized) date when Plaid has one, otherwise the posted date.
/// </summary>
public sealed record PlaidTxn(
    string TransactionId,
    string AccountId,
    long AmountCents,
    DateOnly Date,
    string Name,
    string? MerchantName,
    bool Pending,
    string? PendingTransactionId,
    string? CategoryPrimary,
    string? CategoryDetailed);

/// <summary>A row the app already stores for a Plaid transaction.</summary>
public sealed record StoredPlaidRow(Guid RowId, string PlaidTransactionId, bool Pending);

/// <param name="ReplacesPending">True when a posted transaction takes over its pending row.</param>
public sealed record PlannedUpdate(Guid RowId, PlaidTxn Data, bool ReplacesPending);

public sealed record SyncPlan(IReadOnlyList<PlaidTxn> Inserts, IReadOnlyList<PlannedUpdate> Updates, IReadOnlyList<Guid> Deletes);

/// <summary>
/// Turns one complete /transactions/sync result (all pages) into inserts, updates and deletes.
/// <para>
/// Pending to posted (SPEC milestone 3): when a posted transaction arrives with a
/// <c>pending_transaction_id</c>, it takes over the pending row instead of creating a second one. Amount,
/// date and the pending flag change; the row (and the user's category) stays, so the purchase is never
/// counted twice. The pending id's later "removed" event finds nothing to delete. This works whether the
/// pending row came from an earlier sync or from this same batch, and whatever order Plaid lists things in.
/// </para>
/// </summary>
public static class PlaidSyncPlanner
{
    private sealed class Entry(Guid? rowId, bool storedPending)
    {
        public Guid? RowId { get; } = rowId;
        public PlaidTxn? Data { get; set; }
        public bool Deleted { get; set; }
        public bool ReplacesPending { get; set; }
        public bool IsPending => Data?.Pending ?? storedPending;
    }

    public static SyncPlan Plan(
        IEnumerable<StoredPlaidRow> stored,
        IEnumerable<PlaidTxn> added,
        IEnumerable<PlaidTxn> modified,
        IEnumerable<string> removed)
    {
        var entries = stored.ToDictionary(s => s.PlaidTransactionId, s => new Entry(s.RowId, s.Pending));
        var addedList = added.ToList();

        // Posted transactions that point at a pending one are applied last, so the pending row exists
        // (stored or newly added) no matter how Plaid ordered the batch.
        foreach (var t in addedList.Where(t => t.PendingTransactionId is null).Concat(addedList.Where(t => t.PendingTransactionId is not null)))
            Upsert(entries, t);
        foreach (var t in modified)
            Upsert(entries, t);
        foreach (var id in removed)
        {
            if (entries.TryGetValue(id, out var e))
                e.Deleted = true;
        }

        var inserts = new List<PlaidTxn>();
        var updates = new List<PlannedUpdate>();
        var deletes = new List<Guid>();
        foreach (var e in entries.Values.Distinct())
        {
            if (e.RowId is { } rowId)
            {
                if (e.Deleted) deletes.Add(rowId);
                else if (e.Data is not null) updates.Add(new PlannedUpdate(rowId, e.Data, e.ReplacesPending));
            }
            else if (!e.Deleted && e.Data is not null)
            {
                inserts.Add(e.Data);
            }
        }
        return new SyncPlan(inserts, updates, deletes);
    }

    private static void Upsert(Dictionary<string, Entry> entries, PlaidTxn t)
    {
        if (entries.TryGetValue(t.TransactionId, out var same))
        {
            same.Data = t;
            same.Deleted = false;
            return;
        }
        if (t.PendingTransactionId is { } pendingId
            && entries.TryGetValue(pendingId, out var pending)
            && !pending.Deleted
            && pending.IsPending)
        {
            entries.Remove(pendingId);
            pending.Data = t;
            pending.ReplacesPending = pending.RowId is not null;
            entries[t.TransactionId] = pending;
            return;
        }
        entries[t.TransactionId] = new Entry(null, t.Pending) { Data = t };
    }
}

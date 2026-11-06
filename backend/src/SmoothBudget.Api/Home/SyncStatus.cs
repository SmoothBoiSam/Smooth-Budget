using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Home;

public record ItemSyncInfo(string? InstitutionName, PlaidItemStatus Status, string? StatusMessage, DateTime? LastSyncedUtc);

/// <param name="State">"none" (no bank connections), "ok", "stale" or "error".</param>
/// <param name="LastSyncedUtc">The oldest last-successful sync across connections: how old the data could be.</param>
public record SyncStatusDto(string State, DateTime? LastSyncedUtc, string? Message);

/// <summary>
/// SPEC section 4.2: if bank data is stale or a sync failed, Home must say so instead of showing a
/// confident number. Errors win over staleness; staleness means no successful sync in the last day.
/// </summary>
public static class SyncStatus
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    public static SyncStatusDto For(IReadOnlyList<ItemSyncInfo> items, DateTime nowUtc)
    {
        var live = items.Where(i => i.Status != PlaidItemStatus.PendingSetup).ToList();
        if (live.Count == 0)
            return new SyncStatusDto("none", null, null);

        var oldest = live.Any(i => i.LastSyncedUtc is null) ? null : live.Min(i => i.LastSyncedUtc);
        var broken = live.FirstOrDefault(i => i.Status is PlaidItemStatus.LoginRequired or PlaidItemStatus.Error);
        if (broken is not null)
            return new SyncStatusDto("error", oldest,
                $"{broken.InstitutionName ?? "A bank"}: {broken.StatusMessage ?? "sync failed"} Numbers may be out of date.");

        var stale = live.FirstOrDefault(i => i.LastSyncedUtc is null || nowUtc - i.LastSyncedUtc > StaleAfter);
        if (stale is not null)
            return new SyncStatusDto("stale", oldest,
                $"{stale.InstitutionName ?? "A bank"} hasn't synced in over a day. Pull down to refresh.");

        return new SyncStatusDto("ok", oldest, null);
    }
}

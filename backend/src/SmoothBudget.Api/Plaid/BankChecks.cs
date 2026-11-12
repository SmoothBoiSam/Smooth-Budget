using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Plaid;

/// <summary>
/// What "Update bank data" did (DECISIONS.md 242): the banks Plaid checked, the soonest a skipped bank can be checked again,
/// banks Plaid wouldn't check, and every connection's sync.
/// </summary>
public record BankUpdateDto(IReadOnlyList<string> Checked, DateTime? NextCheckUtc, IReadOnlyList<string> Problems, IReadOnlyList<SyncResultDto> Results);

/// <summary>Where one connection stands on bank checks (DECISIONS.md 242). NextUtc is null when it can be checked now.</summary>
public record BankCheckDto(DateTime? LastUtc, int ThisMonth, int PerMonth, DateTime? NextUtc, long? CostCents);

/// <summary>
/// "Update bank data" (DECISIONS.md 242) asks Plaid to go to the bank right away (/transactions/refresh), which Plaid
/// charges for each time. Free pull to refresh only collects what Plaid already has. So each connection gets one check
/// every few hours and a monthly cap, both set by environment variables.
/// </summary>
public static class BankChecks
{
    private static DateOnly MonthOf(DateTime utc) => new(utc.Year, utc.Month, 1);

    public static int UsedThisMonth(PlaidItem item, DateTime nowUtc) => item.BankCheckMonth == MonthOf(nowUtc) ? item.BankChecksThisMonth : 0;

    /// <summary>When the next check is allowed, or null for now.</summary>
    public static DateTime? NextAllowedUtc(PlaidItem item, DateTime nowUtc, PlaidOptions o)
    {
        if (UsedThisMonth(item, nowUtc) >= o.BankChecksPerMonth)
            return MonthOf(nowUtc).AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var after = item.LastBankCheckUtc?.AddHours(o.BankCheckCooldownHours);
        return after > nowUtc ? after : null;
    }

    public static void Record(PlaidItem item, DateTime nowUtc)
    {
        item.BankChecksThisMonth = UsedThisMonth(item, nowUtc) + 1;
        item.BankCheckMonth = MonthOf(nowUtc);
        item.LastBankCheckUtc = nowUtc;
    }

    public static BankCheckDto For(PlaidItem item, DateTime nowUtc, PlaidOptions o)
    {
        var used = UsedThisMonth(item, nowUtc);
        return new BankCheckDto(item.LastBankCheckUtc, used, o.BankChecksPerMonth, NextAllowedUtc(item, nowUtc, o),
            o.BankCheckPriceCents is { } price ? (long)price * used : null);
    }
}

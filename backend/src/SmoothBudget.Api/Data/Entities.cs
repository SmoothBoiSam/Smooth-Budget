using Microsoft.AspNetCore.Identity;
using SmoothBudget.Core.Budgeting;

namespace SmoothBudget.Api.Data;

/// <summary>
/// A login. Every user belongs to exactly one household; all financial data hangs off the household,
/// so a partner login can be added later by creating a second user in the same household.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;
}

public class Household
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Nominal start day of the budget month (SPEC: the 7th). Weekend shifts are applied by BudgetMonthCalendar.</summary>
    public int BudgetMonthStartDay { get; set; } = BudgetMonthCalendar.DefaultStartDay;

    /// <summary>IANA time zone used to decide what "today" is. The owner is Denver-based.</summary>
    public string TimeZoneId { get; set; } = "America/Denver";

    public DateTime CreatedUtc { get; set; }

    public List<AppUser> Users { get; set; } = [];
}

public enum AccountType { Checking = 0, Savings = 1, CreditCard = 2 }

/// <summary>Where an account's data comes from. CSV and Plaid accounts must behave identically everywhere.</summary>
public enum DataSource { Manual = 0, Csv = 1, Plaid = 2, Seed = 3 }

public class Account
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public AccountType Type { get; set; }

    /// <summary>Last four digits only. Full account numbers are never stored.</summary>
    public string? Mask { get; set; }

    public long CurrentBalanceCents { get; set; }
    public bool IncludedInBudget { get; set; } = true;
    public DataSource Source { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public class Category
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public CategoryType Type { get; set; }
    public long MonthlyLimitCents { get; set; }

    /// <summary>Eating out, shopping, entertainment: trimmed first when income falls short (SPEC section 3).</summary>
    public bool IsDiscretionary { get; set; }

    public bool IsSplit { get; set; }
    public decimal SplitPercent { get; set; } = 50m;
    public int SortOrder { get; set; }
    public bool IsHidden { get; set; }
}

public class Transaction
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Integer cents. Positive = money out (purchase), negative = money in (refund, paycheck, transfer in).</summary>
    public long AmountCents { get; set; }

    public required string Merchant { get; set; }
    public bool Pending { get; set; }
    public bool IsTransfer { get; set; }

    /// <summary>Owner's share, 0..1. 1 = all of it.</summary>
    public decimal SplitFactor { get; set; } = 1m;

    public long PartnerOffsetCents { get; set; }
    public DataSource Source { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public class BudgetMonth
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public long IncomeExpectedCents { get; set; }

    /// <summary>Net overspend carried in from the previous month, as a positive number of cents.</summary>
    public long CarriedInDeficitCents { get; set; }

    // Filled in when the month closes (milestone 4).
    public long? ClosingNetCents { get; set; }
    public long? SweepAmountCents { get; set; }
    public string? SweepDestination { get; set; }
}

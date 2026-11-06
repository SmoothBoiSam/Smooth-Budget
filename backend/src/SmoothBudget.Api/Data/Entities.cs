using Microsoft.AspNetCore.Identity;
using SmoothBudget.Core.Budgeting;
using SmoothBudget.Core.Import;
using SmoothBudget.Core.Transactions;

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

    /// <summary>Where a month's leftover money goes (SPEC section 3): debt by default, savings, or a split.</summary>
    public SweepTarget SweepTarget { get; set; } = SweepTarget.Debt;

    /// <summary>For <see cref="SweepTarget.Split"/>: the share that goes to savings, 0 to 100.</summary>
    public int SweepSavingsPercent { get; set; } = 50;

    /// <summary>The partner's name as it appears on incoming transfers ("Zelle payment from ..."); set in Settings.</summary>
    public string? PartnerName { get; set; }

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

    /// <summary>From the owner's point of view: money in a bank account is positive, a card balance owed is negative.</summary>
    public long CurrentBalanceCents { get; set; }
    public bool IncludedInBudget { get; set; } = true;
    public DataSource Source { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Set when the account is linked through Plaid. An account can start as CSV and be linked later.</summary>
    public string? PlaidAccountId { get; set; }
    public Guid? PlaidItemId { get; set; }
    public PlaidItem? PlaidItem { get; set; }
}

public enum PlaidItemStatus
{
    /// <summary>Linked, waiting for the user to say which accounts to import.</summary>
    PendingSetup = 0,
    Active = 1,
    /// <summary>The bank needs the user to sign in again (Plaid update mode).</summary>
    LoginRequired = 2,
    Error = 3,
}

/// <summary>One Plaid connection to one bank login.</summary>
public class PlaidItem
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string PlaidItemId { get; set; }
    public string? InstitutionId { get; set; }
    public string? InstitutionName { get; set; }

    /// <summary>AES-GCM encrypted with PLAID_TOKEN_KEY. Never logged, never returned by the API.</summary>
    public required string AccessTokenEncrypted { get; set; }

    /// <summary>Plaid /transactions/sync cursor; null until the first sync.</summary>
    public string? Cursor { get; set; }

    public PlaidItemStatus Status { get; set; }
    public string? StatusMessage { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
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

    /// <summary>Normalized merchant key used for rules and display (e.g. "KING SOOPERS DENVER CO").</summary>
    public required string Merchant { get; set; }

    /// <summary>The bank's description, with long digit runs masked.</summary>
    public string Description { get; set; } = "";

    public TransactionKind Kind { get; set; }
    public CategorySource CategorySource { get; set; }

    /// <summary>The most specific bank category label: Plaid's detailed category, or a CSV's category column.</summary>
    public string? BankCategory { get; set; }

    /// <summary>Plaid's primary category (e.g. FOOD_AND_DRINK), when known.</summary>
    public string? BankCategoryPrimary { get; set; }

    /// <summary>Plaid's confidence in its category (VERY_HIGH, HIGH, MEDIUM, LOW, UNKNOWN), when known.</summary>
    public string? BankConfidence { get; set; }

    /// <summary>The category is an automatic guess the user should confirm. It still counts in the budget.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>Why it has this category, in plain words ("The bank calls it ...").</summary>
    public string? CategoryReason { get; set; }

    public bool Pending { get; set; }
    public bool IsTransfer { get; set; }

    /// <summary>Shared by the two sides of a detected card payment or own-account transfer.</summary>
    public Guid? TransferGroupId { get; set; }

    /// <summary>Date|amount|description key used to skip rows already imported into this account.</summary>
    public string? Fingerprint { get; set; }

    public Guid? ImportBatchId { get; set; }

    /// <summary>Plaid's transaction_id. When a pending transaction posts, this changes to the posted id on the same row.</summary>
    public string? PlaidTransactionId { get; set; }

    /// <summary>Owner's share, 0..1. 1 = all of it.</summary>
    public decimal SplitFactor { get; set; } = 1m;

    public long PartnerOffsetCents { get; set; }
    public DataSource Source { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Merchant pattern to category (SPEC section 6.2). Matches any merchant containing the pattern.</summary>
public class CategoryRule
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Pattern { get; set; }
    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public DateTime CreatedUtc { get; set; }
}

/// <summary>One CSV upload. Lets the user see what an import did and undo it.</summary>
public class ImportBatch
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public required string FileName { get; set; }
    public BankCsvFormat Format { get; set; }
    public DateTime ImportedUtc { get; set; }
    public int RowCount { get; set; }
    public int ImportedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
}

public class BudgetMonth
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    /// <summary>The owner's own expected income this month (not partner money).</summary>
    public long IncomeExpectedCents { get; set; }

    /// <summary>Expected partner contributions this month.</summary>
    public long PartnerExpectedCents { get; set; }

    /// <summary>
    /// Net overspend carried in, as a positive number of cents. For the first month on record this is the
    /// starting value; for later months it's derived from the previous month's close and frozen when this
    /// month closes.
    /// </summary>
    public long CarriedInDeficitCents { get; set; }

    /// <summary>The month's final Left to Spend; set when the month closes. Negative = deficit carried forward.</summary>
    public long? ClosingNetCents { get; set; }

    /// <summary>When the month was closed (frozen). Null while it's open or still in its grace period.</summary>
    public DateTime? ClosedUtc { get; set; }

    public List<BudgetMonthCategory> Categories { get; set; } = [];
}

/// <summary>
/// One category in one budget month: a "this month only" limit override while the month runs; the limit
/// frozen when the month ends (<see cref="ClosingLimitCents"/>, so later limit changes never rewrite it);
/// and the amount spent, frozen when it closes (so each category's own over/under stays on record).
/// </summary>
public class BudgetMonthCategory
{
    public Guid Id { get; set; }
    public Guid BudgetMonthId { get; set; }
    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public long? LimitOverrideCents { get; set; }
    public long? ClosingLimitCents { get; set; }
    public long? ClosingSpentCents { get; set; }
}

/// <summary>A "send $X to …" item from a month's surplus. The app can't move money, so the user marks it done.</summary>
public class SweepTask
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid BudgetMonthId { get; set; }
    public BudgetMonth BudgetMonth { get; set; } = null!;
    public SweepKind Kind { get; set; }
    public long AmountCents { get; set; }
    public required string Destination { get; set; }
    public DateTime? DoneUtc { get; set; }
}

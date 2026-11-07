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
    /// <summary>
    /// Labeling transfers between you starts after this day (DECISIONS.md 92, 100): the day before you first named your
    /// partner. Money from her on or before it is her contribution, as it always was; after it, transfers wait for a label.
    /// </summary>
    public DateOnly? PartnerLabelsStartAfter { get; set; }
    /// <summary>How extra debt payments are aimed: Avalanche (highest APR first, the default) or Snowball.</summary>
    public SmoothBudget.Core.Debt.DebtStrategy DebtStrategy { get; set; }
    /// <summary>Extra paid toward debt each month beyond the minimums. Null = whatever the budget plans for card payments.</summary>
    public long? ExtraDebtPaymentCents { get; set; }
    /// <summary>Where money left over in the budget goes, in order (SPEC 4.5): "StarterFund,Debt,Savings" by default.</summary>
    public string SavingsPriority { get; set; } = "StarterFund,Debt,Savings";

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
    /// <summary>A credit card's limit, from the bank when it reports one (for utilization).</summary>
    public long? CreditLimitCents { get; set; }
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

    /// <summary>Filed automatically and not yet confirmed by the user. It still counts in the budget.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>The automatic category is a real guess (ambiguous or low confidence), not just "likely right".</summary>
    public bool CategoryUnsure { get; set; }

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
    /// <summary>Money to or from the partner (by the name in Settings): it waits for you to say what it was for (DECISIONS.md 100).</summary>
    public bool IsPartnerTransfer { get; set; }
    /// <summary>For a transfer waiting for a label: the category the app thinks it was for (shown pre-selected, counted nowhere).</summary>
    public Guid? SuggestedCategoryId { get; set; }

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

/// <summary>
/// A debt for the payoff plan (SPEC 4.4): a credit card (linked to its account, whose balance it follows) or a
/// loan entered by hand. APR and minimum come from the bank when it shares them (Plaid Liabilities) and are always editable.
/// </summary>
public class DebtAccount
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    /// <summary>The credit card account this debt follows. Null for a loan entered by hand.</summary>
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }
    public required string Name { get; set; }
    /// <summary>What's owed, positive. For a linked card it's refreshed from the account's balance.</summary>
    public long BalanceCents { get; set; }
    /// <summary>APR in basis points (2499 = 24.99%). Null until you (or the bank) fill it in.</summary>
    public int? AprBps { get; set; }
    public long? MinimumPaymentCents { get; set; }
    /// <summary>Day of the month the payment is due (1-31; months shorter than that use their last day).</summary>
    public int? DueDay { get; set; }
    /// <summary>For cards without a limit from the bank, or loans with an original amount.</summary>
    public long? CreditLimitCents { get; set; }
    /// <summary>What was owed when tracking started, for "paid down so far".</summary>
    public long StartingBalanceCents { get; set; }
    public DateOnly StartedOn { get; set; }
    /// <summary>The APR and minimum came from the bank (Plaid Liabilities) and you haven't changed them.</summary>
    public bool FromBank { get; set; }
    /// <summary>Left out of the plan (e.g. a card you pay in full every month).</summary>
    public bool IsHidden { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// What you decided about one detected bill or subscription (SPEC 4.7). Detection itself runs fresh from the
/// transactions each time; this only keeps your choices, matched by the series' key (merchant, plus amount when
/// one merchant bills several).
/// </summary>
public class RecurringItem
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Key { get; set; }
    /// <summary>Your name for it (null = the detected name).</summary>
    public string? Name { get; set; }
    /// <summary>Bill or subscription, when you changed it (null = the detected guess).</summary>
    public SmoothBudget.Core.Transactions.RecurringKind? Kind { get; set; }
    /// <summary>The day of the month it's due, when you set one.</summary>
    public int? DueDay { get; set; }
    /// <summary>You said yes, it's recurring.</summary>
    public bool Confirmed { get; set; }
    /// <summary>You said it isn't recurring (a false positive): hidden from the list and totals.</summary>
    public bool Dismissed { get; set; }
    /// <summary>You said you canceled it. A charge after this day brings it back.</summary>
    public DateOnly? CanceledOn { get; set; }
    /// <summary>The price you acknowledged after a change, so the flag clears until it changes again.</summary>
    public long? AcknowledgedAmountCents { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// A fixed contribution from the partner toward one category (SPEC 3 and 4.8; the data model's PartnerArrangement).
/// It belongs to the household, so a partner login can be added later without moving it.
/// </summary>
public class PartnerArrangement
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public long AmountCents { get; set; }
    /// <summary>When it usually arrives in the budget month (Appendix D: the 18th to the 24th).</summary>
    public int WindowFromDay { get; set; } = 18;
    public int WindowToDay { get; set; } = 24;
    /// <summary>The first budget month it applies to; earlier months aren't changed.</summary>
    public DateOnly StartsOn { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A savings goal (SPEC 4.5; the data model's SavingsGoal). Progress is its savings account's balance (goals sharing an
/// account fill in priority order) or an amount you track by hand.
/// </summary>
public class SavingsGoal
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public long TargetCents { get; set; }
    /// <summary>The savings account whose balance is this goal's progress. Null: tracked by hand.</summary>
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }
    /// <summary>Saved so far, for a goal tracked by hand.</summary>
    public long? ManualSavedCents { get; set; }
    /// <summary>What you plan to put in each month. Null: your actual pace into its account.</summary>
    public long? MonthlyCents { get; set; }
    /// <summary>The starter emergency fund (the rainy-day fund): first in the default priority.</summary>
    public bool IsStarter { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// The pay profile (SPEC 5.1), with the pay rules table and any edits to the learned withholding model stored as JSON
/// (null = the defaults from Appendix A, and the model learned from your stubs).
/// </summary>
public class PayProfile
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    /// <summary>Completed years of service: picks the hourly rate from the rules table.</summary>
    public int YearsOfService { get; set; }
    public bool FullTime { get; set; } = true;
    /// <summary>Eligible for holiday pay (full-time, past 90 days).</summary>
    public bool HolidayPay { get; set; } = true;
    /// <summary>With fewer than 2 stubs: net pay as a percent of gross (the flat, low-confidence estimate).</summary>
    public int FlatNetPercent { get; set; } = 80;
    public string? RulesJson { get; set; }
    public string? ModelJson { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One month's imported schedule (SPEC 5.2). Re-importing replaces it. Only what the pay engine needs is kept.</summary>
public class ScheduleMonth
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    /// <summary>"text" (printed values) or "csv" (estimated).</summary>
    public required string Source { get; set; }
    /// <summary>The parsed pairings, days and legs (SmoothBudget.Core.Pay.ParsedSchedule).</summary>
    public required string Json { get; set; }
    /// <summary>Awarded reserve this month: the 76-hour guarantee replaces trip credit for base pay.</summary>
    public bool Reserve { get; set; }
    /// <summary>Pairing keys you flew as overtime, newline-separated.</summary>
    public string OvertimeKeys { get; set; } = "";
    /// <summary>Non-flying entries you switched on as paid, newline-separated.</summary>
    public string PaidEntryKeys { get; set; } = "";
    public DateTime ImportedUtc { get; set; }
}

/// <summary>A confirmed pay stub's numbers (SPEC 5.3). Never the file, name, address, SSN or bank account.</summary>
public class PayStub
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public DateOnly PayDate { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public long GrossCents { get; set; }
    public long NetCents { get; set; }
    /// <summary>Every parsed line (SmoothBudget.Core.Pay.PayStubData).</summary>
    public required string Json { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Predicted vs actual for one check (SPEC 5.5). The prediction is kept as it stood on payday.</summary>
public class PayPeriodForecast
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public DateOnly PayDate { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public long PredictedGrossCents { get; set; }
    public long PredictedNetCents { get; set; }
    public long LowNetCents { get; set; }
    public long HighNetCents { get; set; }
    public long? ActualGrossCents { get; set; }
    public long? ActualNetCents { get; set; }
    /// <summary>"deposit" (matched in your checking account) or "stub" (from the uploaded pay stub, which wins).</summary>
    public string? ActualSource { get; set; }
    public Guid? DepositTransactionId { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A payday plan item you checked off.</summary>
public class PaydayChecklistItem
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public DateOnly PayDate { get; set; }
    public required string Key { get; set; }
    public DateTime CreatedUtc { get; set; }
}

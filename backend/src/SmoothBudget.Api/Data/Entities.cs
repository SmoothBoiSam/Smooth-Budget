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

    /// <summary>The one savings/debt split (DECISIONS.md 321): what's left each month after bills, spending and card minimums, and a closed month's leftover. Debt by default.</summary>
    public SweepTarget SweepTarget { get; set; } = SweepTarget.Debt;

    /// <summary>For <see cref="SweepTarget.Split"/>: the share that goes to savings, 0 to 100.</summary>
    public int SweepSavingsPercent { get; set; } = 50;

    /// <summary>
    /// No longer used: the name on a friend's transfers is now kept on each friend (<see cref="Friend.TransferName"/>,
    /// DECISIONS.md 211), and migration FriendMoneyPerFriend copied it there. Kept so the old value isn't lost.
    /// </summary>
    public string? PartnerName { get; set; }

    /// <summary>Your name as a linked partner sees it ("Sam asks for $800…", DECISIONS.md 205).</summary>
    public string? DisplayName { get; set; }
    /// <summary>
    /// No longer used (now <see cref="Friend.LabelsStartAfter"/>, DECISIONS.md 211).
    /// Labeling transfers between you starts after this day (DECISIONS.md 92, 100): the day before you first named your
    /// partner. Money from her on or before it is her contribution, as it always was; after it, transfers wait for a label.
    /// </summary>
    public DateOnly? PartnerLabelsStartAfter { get; set; }
    /// <summary>How extra debt payments are aimed: Avalanche (highest APR first, the default) or Snowball.</summary>
    public SmoothBudget.Core.Debt.DebtStrategy DebtStrategy { get; set; }
    /// <summary>No longer used (DECISIONS.md 321): the extra is the split's debt share. Kept so old rows load.</summary>
    public long? ExtraDebtPaymentCents { get; set; }
    /// <summary>No longer used (DECISIONS.md 321): the savings/debt split replaced the order. Kept so old rows load.</summary>
    public string SavingsPriority { get; set; } = "StarterFund,Debt,Savings";
    /// <summary>The budget updates itself whenever anything it depends on changes (DECISIONS.md 135).</summary>
    public bool AutoBudget { get; set; } = true;
    /// <summary>Notification types you turned off in More → Settings (DECISIONS.md 163). Everything else is on.</summary>
    public List<string> NotificationsOff { get; set; } = [];

    /// <summary>
    /// When bill categories became one line per bill (DECISIONS.md 279): done once per household, so a bill that later
    /// lands in another bill's line isn't moved without asking.
    /// </summary>
    public DateTime? BillLinesSplitUtc { get; set; }

    /// <summary>A copy of the demo started from the sign-in screen (DECISIONS.md 213): deleted after this time. Null for real households.</summary>
    public DateTime? DemoUntilUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<AppUser> Users { get; set; } = [];
}

public enum AccountType { Checking = 0, Savings = 1, CreditCard = 2 }

/// <summary>Where an account's data comes from. CSV and Plaid accounts must behave identically everywhere.</summary>
public enum DataSource { Manual = 0, Csv = 1, Plaid = 2, Seed = 3 }

/// <summary>What money in is, when you said so in Review (DECISIONS.md 254): a paycheck, other income (a bonus, a rebate), or a
/// friend's contribution. Unset: the app decides.</summary>
public enum IncomeType { Paycheck = 1, Other = 2, Friend = 3 }

public class Account
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public AccountType Type { get; set; }

    /// <summary>Last four digits only. Full account numbers are never stored.</summary>
    public string? Mask { get; set; }

    /// <summary>
    /// What you call it (More → Account nicknames, DECISIONS.md 310), shown everywhere instead of its name from the bank
    /// and its last four digits. <see cref="Name"/> stays what the bank or the import called it.
    /// </summary>
    public string? Nickname { get; set; }

    /// <summary>
    /// A savings account's share of each month's savings, in percent (Savings → Payment settings, DECISIONS.md 314). Null
    /// or 0 is none; with no account given one, it all goes to the first savings account.
    /// </summary>
    public int? SavingsPercent { get; set; }

    /// <summary>From the owner's point of view: money in a bank account is positive, a card balance owed is negative.</summary>
    public long CurrentBalanceCents { get; set; }
    /// <summary>
    /// For an account the bank doesn't sync: the day <see cref="CurrentBalanceCents"/> was true (a CSV's newest line, or
    /// the day you typed it). Transactions after it move the balance; with none ever given, all of them do (DECISIONS.md 189).
    /// </summary>
    public DateOnly? BalanceAsOf { get; set; }
    /// <summary>A credit card's limit, from the bank when it reports one (for utilization).</summary>
    public long? CreditLimitCents { get; set; }
    public bool IncludedInBudget { get; set; } = true;
    /// <summary>
    /// The day you said it's closed (More → Accounts, DECISIONS.md 318). A closed account keeps its history (past months,
    /// transfers, bills) but leaves everything about now: balances, cash, debt, savings, the payday tasks and choices.
    /// </summary>
    public DateOnly? ClosedOn { get; set; }
    /// <summary>The account that took over from this closed one: what a bill, a sweep or a task naming this one goes to now.</summary>
    public Guid? ReplacedById { get; set; }
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

    /// <summary>"Update bank data" (DECISIONS.md 242): Plaid charges for each, so they're counted per UTC calendar month.</summary>
    public DateTime? LastBankCheckUtc { get; set; }
    public DateOnly? BankCheckMonth { get; set; }
    public int BankChecksThisMonth { get; set; }
}

public class Category
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public CategoryType Type { get; set; }
    public long MonthlyLimitCents { get; set; }
    /// <summary>You set this limit yourself, so the automatic budget leaves it alone (until you put it back to automatic).</summary>
    public bool LimitSetByUser { get; set; }

    /// <summary>
    /// When the month's money runs short, the automatic budget squeezes low priority spending first, then medium, then
    /// high (DECISIONS.md 182). It replaced "nice-to-have" (which became low).
    /// </summary>
    public SmoothBudget.Core.Budgeting.CategoryPriority Priority { get; set; } = SmoothBudget.Core.Budgeting.CategoryPriority.Medium;

    public bool IsSplit { get; set; }
    public decimal SplitPercent { get; set; } = 50m;
    public int SortOrder { get; set; }
    public bool IsHidden { get; set; }

    /// <summary>
    /// Names of categories merged into this one (DECISIONS.md 144). Transactions the app would have filed under one of
    /// them land here instead, so merging "Groceries" into "Food" keeps catching groceries.
    /// </summary>
    public List<string> MergedNames { get; set; } = [];

    /// <summary>When it was added. Categories from before this was kept read as long ago (DECISIONS.md 180).</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>"Keep it" on the suggestion to archive it: it isn't suggested again for a while (DECISIONS.md 180).</summary>
    public DateTime? KeptUnusedUtc { get; set; }
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

    /// <summary>
    /// The other account (DECISIONS.md 259): for a transfer, the one it went to or came from (null with <see cref="IsTransfer"/>
    /// set: an account not in the app, or a loan you track by hand, in <see cref="DebtAccountId"/>); for interest earned,
    /// the savings account that paid it.
    /// </summary>
    public Guid? TransferAccountId { get; set; }
    public Account? TransferAccount { get; set; }

    /// <summary>Date|amount|description key used to skip rows already imported into this account.</summary>
    public string? Fingerprint { get; set; }

    public Guid? ImportBatchId { get; set; }

    /// <summary>Plaid's transaction_id. When a pending transaction posts, this changes to the posted id on the same row.</summary>
    public string? PlaidTransactionId { get; set; }

    /// <summary>Owner's share, 0..1. 1 = all of it.</summary>
    public decimal SplitFactor { get; set; } = 1m;
    /// <summary>Money to or from the partner (by the name in Settings): it waits for you to say what it was for (DECISIONS.md 100).</summary>
    public bool IsPartnerTransfer { get; set; }
    /// <summary>Which friend the money is to or from, when <see cref="IsPartnerTransfer"/> (DECISIONS.md 211).</summary>
    public Guid? FriendId { get; set; }
    public Friend? Friend { get; set; }
    /// <summary>The debt this money was paid toward, or the card or loan an interest charge was for (DECISIONS.md 232).</summary>
    public Guid? DebtAccountId { get; set; }
    public DebtAccount? DebtAccount { get; set; }
    /// <summary>For a transfer waiting for a label: the category the app thinks it was for (shown pre-selected, counted nowhere).</summary>
    public Guid? SuggestedCategoryId { get; set; }
    /// <summary>Her contribution sent early: it counts for the budget month starting on this date, not the one it landed in (DECISIONS.md 166).</summary>
    public DateOnly? CountsForMonthStart { get; set; }
    /// <summary>Income you marked in Review as a paycheck or other income (DECISIONS.md 254). Null: the app decides from your pay dates.</summary>
    public IncomeType? IncomeType { get; set; }

    public long PartnerOffsetCents { get; set; }
    public DataSource Source { get; set; }
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
    /// <summary>You typed this month's income yourself, so the automatic budget doesn't replace it with the Payday forecast.</summary>
    public bool IncomeSetByUser { get; set; }
    /// <summary>What the plan still needs beyond the month's money after the automatic budget squeezed everything it can.</summary>
    public long AutoShortCents { get; set; }
    /// <summary>
    /// Money set aside in earlier months for a planned expense paid this month (DECISIONS.md 327). It's already in checking,
    /// so it's part of the month's money, like income that has landed.
    /// </summary>
    public long SetAsideInCents { get; set; }
    /// <summary>Left out of the automatic budget because the bank accounts can't cover it this month (DECISIONS.md 233).</summary>
    public long AutoHeldBackCents { get; set; }
    /// <summary>The automatic budget's last change, in plain words, and when.</summary>
    public string? AutoNote { get; set; }
    public DateTime? AutoNoteUtc { get; set; }

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
/// <summary>
/// A big one-time expense you're setting money aside for (DECISIONS.md 327). It has its own bill line (a category named
/// after it), which plans what each month sets aside and, in the month it's paid, what it costs. Spending filed there is
/// what's been paid on it.
/// </summary>
public class PlannedExpense
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public long AmountCents { get; set; }
    public DateOnly DueOn { get; set; }
    /// <summary>The payday of the check you were on when you added it: the first check that sets money aside.</summary>
    public DateOnly FirstPay { get; set; }
    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
    public DateTime CreatedUtc { get; set; }
}

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
    /// <summary>
    /// For a card that isn't bank-linked (DECISIONS.md 253): the balance you typed and the day you typed it. What's owed
    /// is that, plus what the card was charged and less what it was paid on later days.
    /// </summary>
    public long? TypedBalanceCents { get; set; }
    public DateOnly? TypedOn { get; set; }
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
    /// <summary>You said this is the same bill as another one (its price changed), so its charges join that one (DECISIONS.md 181).</summary>
    public string? MergedIntoKey { get; set; }
    /// <summary>You said it isn't the same bill as this one, so the app stops asking.</summary>
    public string? NotSameAsKey { get; set; }
    /// <summary>You said it's still going ("keep looking", DECISIONS.md 200): the day you said so. Its later charges are found even at a new price.</summary>
    public DateOnly? KeepLookingOn { get; set; }
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
    /// <summary>The friend who sends it (DECISIONS.md 211). Their transfers are the ones matched to it.</summary>
    public Guid FriendId { get; set; }
    public Friend Friend { get; set; } = null!;
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
    /// <summary>No longer used (DECISIONS.md 321): a goal's monthly amount comes from the split. Kept so old rows load.</summary>
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
    /// <summary>Completed years of service, used only when no date of hire is set.</summary>
    public int YearsOfService { get; set; }
    /// <summary>The InFlight date of hire: years of service (and the rate) follow from it, advancing on each anniversary.</summary>
    public DateOnly? HireDate { get; set; }
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
    /// <summary>Trips you dropped after importing (left out of pay), newline-separated.</summary>
    public string DroppedKeys { get; set; } = "";
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

/// <summary>
/// Income you expect apart from your paychecks, like a bonus (DECISIONS.md 256): what you think it'll be after taxes and
/// the day it should land. The budget counts it in its month until the deposit arrives, then the deposit itself.
/// </summary>
public class ExpectedIncome
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public long AmountCents { get; set; }
    public DateOnly ExpectedDate { get; set; }
    /// <summary>The deposit it arrived as, once matched.</summary>
    public Guid? DepositTransactionId { get; set; }
    public DateTime CreatedUtc { get; set; }
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

/// <summary>A phone or browser that asked for notifications (DECISIONS.md 151). Only the push service's address and keys.</summary>
public class PushDevice
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>A notification already sent, so the same news never arrives twice ("bills:2026-12-03").</summary>
public class NotificationLog
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Key { get; set; }
    public DateTime SentUtc { get; set; }
}

/// <summary>
/// Someone you share costs with (DECISIONS.md 205): a partner, a roommate, a friend. As many as you like. Without an
/// account they're just a name you keep a tab with; once they link their own account (LinkId), what you send each other
/// shows up on both sides. Unlinking makes them a name again, keeping the history.
/// </summary>
public class Friend
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public required string Name { get; set; }
    public Guid? LinkId { get; set; }

    /// <summary>
    /// Their name as your bank shows it on their transfers ("Zelle payment from …"). Money from that name is theirs, not
    /// your income, and their fixed contributions count toward your bills (DECISIONS.md 207, 211). Any number of friends can
    /// have one; no two the same.
    /// </summary>
    public string? TransferName { get; set; }
    /// <summary>
    /// Labeling their transfers starts after this day (DECISIONS.md 92, 100): the day before you first added the name.
    /// Money from them on or before it is their contribution, as it always was; after it, transfers wait for a label.
    /// </summary>
    public DateOnly? LabelsStartAfter { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A one-time link inviting a friend to link their own account (DECISIONS.md 205). Only a hash of the token is stored;
/// the token itself is shown once, in the link. It works for 7 days, once. One per friend at a time.
/// </summary>
public class FriendInvite
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid FriendId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

/// <summary>
/// Two accounts linked to share a tab (DECISIONS.md 205); each side has a Friend pointing at it. Each keeps its own budget; only what you send each other
/// crosses over. Unlinking ends it (EndedUtc) without deleting it, so what was shared keeps its history.
/// </summary>
public class HouseholdLink
{
    public Guid Id { get; set; }
    public Guid HouseholdAId { get; set; }
    public Guid HouseholdBId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
}

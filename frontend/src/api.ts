// Thin client for the backend. The frontend never computes money; it only displays what the API returns.

export type Status = 'healthy' | 'low' | 'overspent'

/** Flight hours to pick up to cover a shortfall, at today's rate and the latest stub's take-home share. */
export interface PickUp {
  hours: number
  rateCents: number
  takeHomePerHourCents: number
}

export interface HomeSummary {
  budgetMonth: { start: string; end: string; label: string }
  today: string
  leftToSpendCents: number
  status: Status
  totalVariableLimitCents: number
  totalVariableSpentCents: number
  carriedInDeficitCents: number
  overBudgetCategoryCount: number
  overBudgetCents: number
  daysRemaining: number
  safeToSpendPerDayCents: number
  uncategorizedCount: number
  lastUpdatedUtc: string | null
  computedUtc: string
  sync: SyncStatus
  pendingSweeps: PendingSweep[]
  reviewCount: number
  /** The next 3 bills due. */
  upcomingBills: { name: string; amountCents: number; date: string }[] | null
  /** The partner's fixed contributions this month (late / different amount flags). */
  contributions: ContributionStatus[] | null
  partnerShortfallCents: number
  /** Spending not filed in a category yet (it still comes off Left to Spend). */
  unfiledSpendingCents: number
  /** How far the plan goes beyond the month's money (it comes off Left to Spend). */
  overplannedCents: number
  /** No income figure this month while there is a plan: Left to Spend can't check the plan against your money. */
  incomeUnknown: boolean
  /** How much Left to Spend was lowered because the accounts can't cover more (DECISIONS.md 183). */
  cashShortCents: number
  /** Left to Spend checked against the bank; null unless every checking balance comes from the bank. */
  cash: CashCheck | null
  /** When the accounts come up short: the flight hours that would make it up (DECISIONS.md 192). */
  pickUp: PickUp | null
  /** The next paycheck, once a schedule or pay stub is in. */
  nextPayday: { payDate: string; hasSchedule: boolean; netCents: number; lowNetCents: number; highNetCents: number; lowConfidence: boolean } | null
  /** Accounts that won't cover the bills due before the next payday. */
  cashWarnings: { account: string; billsCents: number; shortCents: number; billCount: number; nextPayDate: string | null }[] | null
}

export interface Me {
  email: string
  householdId: string
  /** A demo copy started from the sign-in screen (DECISIONS.md 213): made-up data, gone in a day. */
  demo: boolean
}

export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }

  /** The server couldn't be reached at all (no connection, or it's down). */
  get offline() {
    return this.status === 0
  }
}

export const OFFLINE_MESSAGE = "Can't reach Smooth Budget right now. Check your connection and try again."

/** fetch, but a network failure becomes an ApiError with status 0 and a plain message. */
async function send(input: string, init: RequestInit): Promise<Response> {
  try {
    const res = await fetch(input, init)
    // The dev proxy and most hosts answer 502-504 when the API itself is down.
    if (res.status >= 502 && res.status <= 504) throw new ApiError(0, OFFLINE_MESSAGE)
    return res
  } catch (e) {
    if (e instanceof ApiError) throw e
    throw new ApiError(0, OFFLINE_MESSAGE)
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await send(`/api${path}`, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) {
    let message = `Request failed (${res.status})`
    try {
      const problem = await res.json()
      if (problem.errors) message = Object.values(problem.errors).flat().join(' ')
      else if (problem.detail) message = problem.detail
      else if (problem.title) message = problem.title
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, message)
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T)
}

export const api = {
  me: () => request<Me>('GET', '/auth/me'),
  login: (email: string, password: string) => request<Me>('POST', '/auth/login', { email, password }),
  /** With a partner's invite (DECISIONS.md 205), sign-up works even while it's closed, and links the two accounts. */
  register: (email: string, password: string, invite?: string, name?: string) => request<Me>('POST', '/auth/register', { email, password, invite, name }),
  logout: () => request<void>('POST', '/auth/logout'),
  /** A fresh copy of the demo, just for this browser (DECISIONS.md 213). */
  demo: () => request<Me>('POST', '/auth/demo'),
  homeSummary: () => request<HomeSummary>('GET', '/home/summary'),
}

export interface Account {
  id: string
  name: string
  type: 'Checking' | 'Savings' | 'CreditCard'
  mask: string | null
  source: string
  includedInBudget: boolean
  bankSynced: boolean
}

export interface Category {
  id: string
  name: string
  type: 'FixedBill' | 'Variable' | 'DebtPayment' | 'SavingsTransfer'
  monthlyLimitCents: number
  priority: Priority
  sortOrder: number
}

/**
 * The group a category is listed under (DECISIONS.md 196, 202). Card interest works as a bill line underneath (it's
 * added to the card, not paid on its own), but it's shown with the debt payments everywhere you pick or see categories.
 */
export function listedType(c: { name: string; type: Category['type'] }): Category['type'] {
  return c.type === 'FixedBill' && c.name.trim().toLowerCase() === 'card interest' ? 'DebtPayment' : c.type
}

export interface Transaction {
  id: string
  date: string
  amountCents: number
  merchant: string
  description: string
  kind: 'Purchase' | 'Refund' | 'Income' | 'Fee' | 'CardPayment' | 'Transfer' | 'Other' | 'PartnerContribution' | 'PartnerTransfer'
  categoryId: string | null
  categoryName: string | null
  categorySource: 'None' | 'Bank' | 'Rule' | 'Transfer' | 'User' | 'Learned' | 'BestGuess'
  isTransfer: boolean
  pending: boolean
  accountId: string
  accountName: string
  bankCategory: string | null
  /** Filed automatically and not confirmed yet: every automatic category waits for you. */
  needsReview: boolean
  /** The app's least certain guesses; shown first when reviewing. */
  unsure: boolean
  /** Money to or from a friend (by the name on their transfers), and which friend (DECISIONS.md 211). */
  isPartnerTransfer: boolean
  friendId: string | null
  friendName: string | null
  /** While money between you waits for a label: the category the app suggests (counted nowhere). */
  suggestedCategoryId: string | null
  categoryReason: string | null
}

export interface ImportResult {
  batchId: string
  format: string
  rowCount: number
  imported: number
  duplicates: number
  transfers: number
  categorized: number
  needsCategory: number
  errors: { rowNumber: number; message: string }[]
  alreadySynced: number
  coveredBySync: number
  /** Set from the file's newest row, for a checking or savings account the bank doesn't sync. */
  balanceSetCents: number | null
}

export interface ImportBatch {
  id: string
  accountId: string
  accountName: string
  fileName: string
  format: string
  importedUtc: string
  rowCount: number
  importedCount: number
  duplicateCount: number
  errorCount: number
}

export interface Rule {
  id: string
  pattern: string
  categoryId: string
  categoryName: string
  createdUtc: string
}

async function upload<T>(path: string, form: FormData): Promise<T> {
  const res = await send(`/api${path}`, { method: 'POST', credentials: 'same-origin', body: form })
  if (!res.ok) {
    let message = `Upload failed (${res.status})`
    try {
      const problem = await res.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, message)
  }
  return (await res.json()) as T
}

export const budgetApi = {
  accounts: () => request<Account[]>('GET', '/accounts'),
  createAccount: (name: string, type: Account['type'], mask: string | null) =>
    request<Account>('POST', '/accounts', { name, type, mask }),
  categories: () => request<Category[]>('GET', '/budget/categories'),
  /** back = how many budget months before this one (0 = this month). */
  monthTransactions: (back = 0) =>
    request<{ from: string; to: string; items: Transaction[]; label: string | null }>('GET', back > 0 ? `/transactions?back=${back}` : '/transactions'),
  /** The newest few transactions from any month, for Home (DECISIONS.md 171). */
  recent: (count = 5) => request<Transaction[]>('GET', `/transactions/recent?count=${count}`),
  /** Every transaction matching all the filters set (DECISIONS.md 164); newest first, up to 200, with the full count and total. */
  search: (f: SearchFilters) => {
    const p = new URLSearchParams()
    if (f.q.trim()) p.set('q', f.q.trim())
    if (f.categoryId) p.set('categoryId', f.categoryId)
    if (f.min) p.set('min', f.min)
    if (f.max) p.set('max', f.max)
    if (f.from) p.set('from', f.from)
    if (f.to) p.set('to', f.to)
    return request<{ items: Transaction[]; total: number; netCents: number }>('GET', `/transactions/search?${p}`)
  },
  inbox: () => request<Transaction[]>('GET', '/transactions/inbox'),
  inboxCount: () => request<{ count: number; review: number }>('GET', '/transactions/inbox/count'),
  review: (scope: ReviewScope = 'month') => request<Transaction[]>('GET', `/transactions/review?scope=${scope}`),
  reviewSummary: () => request<ReviewSummary>('GET', '/transactions/review/summary'),
  confirmLikely: (scope: ReviewScope | 'all') => request<{ confirmed: number }>('POST', '/transactions/review/confirm-likely', { scope }),
  /** "I don't remember": keep the app's best guess, or file it under Other. */
  bestGuess: (id: string) => request<Transaction>('POST', `/transactions/${id}/best-guess`),
  bestGuessAll: (scope: ReviewScope | 'all') => request<{ settled: number }>('POST', '/transactions/review/best-guess', { scope }),
  context: (id: string) => request<TransactionContext>('GET', `/transactions/${id}/context`),
  confirmGuesses: (ids: string[]) => request<{ confirmed: number }>('POST', '/transactions/review/confirm', { ids }),
  autoCategorize: () => request<AutoCategorizeResult>('POST', '/transactions/auto-categorize'),
  suggestedCategories: () => request<SuggestedCategory[]>('GET', '/transactions/suggested-categories'),
  updateTransaction: (
    id: string,
    categoryId: string | null,
    isTransfer: boolean,
    rulePattern: string | null,
    extra: { partnerPurpose?: PartnerPurpose } = {},
  ) =>
    request<{ transaction: Transaction; ruleAppliedTo: number; billChargesMoved: number }>('PATCH', `/transactions/${id}`, {
      categoryId,
      isTransfer,
      rulePattern,
      partnerPurpose: extra.partnerPurpose ?? null,
    }),
  importCsv: (accountId: string, file: File) => {
    const form = new FormData()
    form.append('accountId', accountId)
    form.append('file', file)
    return upload<ImportResult>('/import/csv', form)
  },
  batches: () => request<ImportBatch[]>('GET', '/import/batches'),
  undoBatch: (id: string) => request<void>('DELETE', `/import/batches/${id}`),
  rules: () => request<Rule[]>('GET', '/rules'),
  deleteRule: (id: string) => request<void>('DELETE', `/rules/${id}`),
}

export type Health = 'onTrack' | 'low' | 'over'

export interface BudgetCategoryLine {
  id: string
  name: string
  type: Category['type']
  priority: Priority
  limitCents: number
  hasMonthOverride: boolean
  spentCents: number
  remainingCents: number
  health: Health
  transactionCount: number
  /** What the partner's fixed contribution covers of this line (Rent: the $800). */
  partnerOffsetCents: number
}

export interface BudgetGroup {
  type: Category['type']
  label: string
  countsTowardLeftToSpend: boolean
  limitCents: number
  spentCents: number
  categories: BudgetCategoryLine[]
}

export interface BudgetSummary {
  budgetMonth: { start: string; end: string; label: string }
  today: string
  leftToSpendCents: number
  status: Status
  totalVariableLimitCents: number
  totalVariableSpentCents: number
  carriedInDeficitCents: number
  daysRemaining: number
  safeToSpendPerDayCents: number
  uncategorizedCount: number
  incomeExpectedCents: number
  plannedCents: number
  unallocatedCents: number
  incomeReceivedCents: number
  partnerExpectedCents: number
  partnerReceivedCents: number
  partnerName: string | null
  reviewCount: number
  groups: BudgetGroup[]
  lastMonth: LastMonth | null
  pendingSweeps: PendingSweep[]
  contributions: ContributionStatus[] | null
  /** What Left to Spend is missing because a contribution is late or short. */
  partnerShortfallCents: number
  /** Spending not filed in a category yet (it still comes off Left to Spend). */
  unfiledSpendingCents: number
  /** How far the plan goes beyond the month's money (it comes off Left to Spend). */
  overplannedCents: number
  /** How much the cash check lowered Left to Spend. */
  cashShortCents: number
  /** The automatic budget: on or off, its last change, any shortfall, and the limits you set yourself. */
  auto: { on: boolean; note: string | null; noteUtc: string | null; shortCents: number; setByYou: string[]; incomeSetByYou: boolean; heldBackCents: number } | null
}

export const budgetSummaryApi = {
  get: () => request<BudgetSummary>('GET', '/budget/summary'),
  /** A category's spending in each of the last six budget months and this one so far (DECISIONS.md 222). */
  history: (categoryId: string) =>
    request<{ months: CategoryMonth[]; unsqueezedCents: number | null }>('GET', `/budget/categories/${categoryId}/history`),
}

export interface CategoryMonth {
  /** The budget month's label: the 1st of its calendar month. */
  month: string
  start: string
  end: string
  spentCents: number
  /** This month, still going. */
  current: boolean
}

export interface SyncStatus {
  state: 'none' | 'ok' | 'stale' | 'error'
  lastSyncedUtc: string | null
  message: string | null
}

export interface PlaidConfig {
  enabled: boolean
  environment: string
  sandboxShortcut: boolean
  webhooks: boolean
}

export interface PlaidAccountProposal {
  plaidAccountId: string
  name: string
  mask: string | null
  type: string
  subtype: string | null
  accountType: Account['type'] | null
  suggestedAccountId: string | null
}

export interface LinkResult {
  itemId: string
  institutionName: string | null
  accounts: PlaidAccountProposal[]
  existingAccounts: Account[]
  /** This bank is already connected through that connection: add accounts there instead (DECISIONS.md 157). */
  duplicateOfItemId?: string | null
}

export interface AccountMapping {
  plaidAccountId: string
  action: 'new' | 'existing' | 'skip'
  accountId: string | null
}

export interface SyncResult {
  itemId: string
  institutionName: string | null
  ok: boolean
  added: number
  updated: number
  removed: number
  pendingPosted: number
  matchedToCsv: number
  skippedAsCsvHistory: number
  error: string | null
}

export interface PlaidItem {
  id: string
  institutionName: string | null
  status: 'PendingSetup' | 'Active' | 'LoginRequired' | 'Error'
  statusMessage: string | null
  lastSyncedUtc: string | null
  accounts: string[]
  /** Bank checks from Home's "Update bank data" (DECISIONS.md 242): nextUtc null means it can be checked now. */
  bankCheck: { lastUtc: string | null; thisMonth: number; perMonth: number; nextUtc: string | null; costCents: number | null }
}

/** What "Update bank data" did (DECISIONS.md 242). */
export interface BankUpdate {
  checked: string[]
  /** The soonest a skipped bank can be checked again. */
  nextCheckUtc: string | null
  problems: string[]
  results: SyncResult[]
}

export const plaidApi = {
  config: () => request<PlaidConfig>('GET', '/plaid/config'),
  /** With an item: sign in again, or (addAccounts) choose more of that bank's accounts to share. */
  linkToken: (itemId?: string, addAccounts = false) =>
    request<{ linkToken: string }>('POST', '/plaid/link-token', { itemId: itemId ?? null, addAccounts }),
  itemAccounts: (id: string) => request<LinkResult>('GET', `/plaid/items/${id}/accounts`),
  exchange: (publicToken: string) => request<LinkResult>('POST', '/plaid/exchange', { publicToken }),
  sandboxLink: (dynamic: boolean) => request<LinkResult>('POST', '/plaid/sandbox/link', { dynamic }),
  sandboxRefresh: (itemId: string) => request<void>('POST', `/plaid/sandbox/items/${itemId}/refresh`),
  confirmAccounts: (itemId: string, mappings: AccountMapping[]) =>
    request<SyncResult>('POST', `/plaid/items/${itemId}/accounts`, { mappings }),
  items: () => request<PlaidItem[]>('GET', '/plaid/items'),
  removeItem: (id: string) => request<void>('DELETE', `/plaid/items/${id}`),
  reconnected: (id: string) => request<SyncResult>('POST', `/plaid/items/${id}/reconnected`),
  syncAll: () => request<SyncResult[]>('POST', '/sync'),
  /** Home's "Update bank data" (DECISIONS.md 242): Plaid checks each bank now (paid, per bank), then everything syncs. */
  checkBanks: () => request<BankUpdate>('POST', '/plaid/check-banks'),
  /** The Plaid session to pick up again after a bank's own sign-in sends you back (DECISIONS.md 185); 404 when none. */
  oauthPending: () => request<{ linkToken: string; itemId: string | null; addAccounts: boolean }>('GET', '/plaid/oauth-pending'),
  clearOAuthPending: () => request<void>('DELETE', '/plaid/oauth-pending'),
}

export interface PendingSweep {
  id: string
  kind: 'Debt' | 'Savings'
  amountCents: number
  destination: string
  fromMonth: string
}

export interface SweepItem {
  id: string
  kind: 'Debt' | 'Savings'
  amountCents: number
  destination: string
  done: boolean
}

export interface LastMonth {
  month: { start: string; end: string; label: string }
  closed: boolean
  closesOn: string
  totalUnspentCents: number
  totalOverspentCents: number
  carriedInDeficitCents: number
  closingNetCents: number
  surplusCents: number
  deficitCents: number
  sweeps: SweepItem[]
  partnerShortfallCents: number
  unfiledSpendingCents: number
  overplannedCents: number
}

export interface ProposedLimit {
  categoryId: string
  name: string
  type: Category['type']
  currentLimitCents: number
  medianCents: number
  proposedCents: number
  explanation: string
  monthlyCents: number[]
}

export interface GeneratorProposal {
  monthsUsed: number
  months: { start: string; end: string }[]
  incomeCents: number
  incomeExplanation: string
  partnerCents: number
  partnerName: string | null
  categorization: AutoCategorizeResult
  categories: ProposedLimit[]
  allocatedCents: number
  unallocatedCents: number
  spendsMoreThanIncome: boolean
  uncategorizedCount: number
  hasCardDebt: boolean
  /** No categories yet: build them from your history first. */
  needsCategorySetup: boolean
  /** Partner money is her fixed contributions, not a median of what she sent. */
  partnerFromArrangements: boolean
  /** The automatic budget: this is what it applies, with nothing to confirm. */
  automatic: boolean
  /** What the month still needs after every squeeze. */
  shortCents: number
}

export type SweepTarget = 'Debt' | 'Savings' | 'Split'

export const budgetEditApi = {
  createCategory: (name: string, type: Category['type'], limitCents: number, priority?: Priority) =>
    request<{ id: string }>('POST', '/budget/categories', { name, type, limitCents, priority: priority ?? null }),
  updateCategory: (
    id: string,
    body: {
      name: string
      type: Category['type']
      limitCents: number
      priority: Priority
      isHidden: boolean
      applyTo: 'month' | 'permanent'
      /** Hand the limit back to the automatic budget. */
      automatic?: boolean
    },
  ) => request<void>('PUT', `/budget/categories/${id}`, body),
  reorder: (ids: string[]) => request<void>('POST', '/budget/categories/order', { ids }),
  /**
   * Delete a category (DECISIONS.md 169). Its transactions move to `moveToId`, or without one they go to Review for a
   * new category. It can't be undone.
   */
  deleteCategory: (id: string, moveToId?: string) =>
    request<{ name: string; movedTo: string | null; movedTransactions: number; toReview: number }>(
      'DELETE',
      `/budget/categories/${id}${moveToId ? `?moveToId=${moveToId}` : ''}`,
    ),
  /**
   * Clear every category, yours too, re-download what the bank says, and file everything again from the bank and the
   * app's guesses (DECISIONS.md 176). It can't be undone.
   */
  resetCategoriesFromBank: () =>
    request<{ cleared: number; filed: number; guesses: number; stillUncategorized: number; bankNotes: string[] }>('POST', '/budget/categories/reset-from-bank'),
  /** Archived categories (what "hidden" was), with when each was last used (DECISIONS.md 180). */
  archivedCategories: () => request<ArchivedCategory[]>('GET', '/budget/categories/hidden'),
  /** Spending and bill lines nothing has been filed under in about 3 months: suggested for archiving. */
  unusedCategories: () => request<ArchivedCategory[]>('GET', '/budget/categories/unused'),
  archiveCategory: (id: string) => request<{ name: string; releasedToReview: number }>('POST', `/budget/categories/${id}/archive`),
  restoreCategory: (id: string) => request<{ name: string }>('POST', `/budget/categories/${id}/restore`),
  /** "Keep it" on the archive suggestion: not suggested again for a while. */
  mergeCategories: (fromIds: string[], intoId: string) =>
    request<{ id: string; name: string; movedTransactions: number }>('POST', '/budget/categories/merge', { fromIds, intoId }),
  keepCategory: (id: string) => request<void>('POST', `/budget/categories/${id}/keep`),
  setIncome: (incomeCents: number, partnerCents?: number, automatic = false) =>
    request<void>('PUT', '/budget/months/current/income', { incomeCents, partnerCents: partnerCents ?? null, automatic }),
  closeMonth: (start: string) => request<void>('POST', `/budget/months/${start}/close`),
  sweepDone: (id: string) => request<void>('POST', `/budget/sweeps/${id}/done`),
  sweepUndo: (id: string) => request<void>('DELETE', `/budget/sweeps/${id}/done`),
  generator: () => request<GeneratorProposal>('GET', '/budget/generator'),
  discoverCategories: () => request<CategoryDiscovery>('GET', '/budget/categories/discover'),
  applyDiscovery: (
    create: { name: string; type: Category['type']; isDiscretionary: boolean; merchantPattern: string | null; replaces: string | null }[],
    hideIds: string[],
  ) =>
    request<AutoCategorizeResult>('POST', '/budget/categories/discover/apply', { create, hideIds }),
  applyGenerator: (incomeCents: number, partnerCents: number, limits: { categoryId: string; limitCents: number }[]) =>
    request<void>('POST', '/budget/generator/apply', { incomeCents, partnerCents, limits }),
  settings: () => request<{ sweepTarget: SweepTarget; sweepSavingsPercent: number; autoBudget: boolean }>('GET', '/settings/budget'),
  saveSettings: (sweepTarget: SweepTarget, sweepSavingsPercent: number, autoBudget?: boolean) =>
    request<void>('PUT', '/settings/budget', { sweepTarget, sweepSavingsPercent, autoBudget: autoBudget ?? null }),
}

export interface AutoCategorizeResult {
  filed: number
  guesses: number
  stillUncategorized: number
  partnerContributions: number
}

export interface SuggestedCategory {
  name: string
  type: Category['type']
  isDiscretionary: boolean
  transactionCount: number
}

export const partnerApi = {
  get: () => request<{ partnerName: string | null }>('GET', '/settings/partner'),
  /** The name on one friend's transfers (DECISIONS.md 207, 211); any number of friends can have one. */
  save: (partnerName: string | null, friendId?: string) => request<AutoCategorizeResult>('PUT', '/settings/partner', { partnerName, friendId }),
  suggestions: () => request<{ name: string; count: number; totalCents: number }[]>('GET', '/settings/partner/suggestions'),
}

/** Money from your partner that's her contribution or just money from her. Paying you back is a category instead. */
export type PartnerPurpose = 'contribution'

/** month: this budget month. earlier: before it, inside the months your budget is built from. older: before those. */
export type ReviewScope = 'month' | 'earlier' | 'older'

export interface ReviewCounts {
  total: number
  likely: number
  unsure: number
}

export interface ReviewSummary {
  month: ReviewCounts
  earlier: ReviewCounts
  /** Before the months your budget is built from: doesn't affect your budget. */
  older: ReviewCounts
  monthStart: string
  windowStart: string
}

export interface TransactionContext {
  history: { categoryName: string; count: number }[]
}

export interface DiscoveredCategory {
  name: string
  type: Category['type']
  isDiscretionary: boolean
  /** A pattern found in your spending rather than one of the common categories. */
  custom: boolean
  transactionCount: number
  monthsSeen: number
  monthlyCents: number
  examples: string[]
  reason: string
  status: 'new' | 'exists' | 'hidden'
  /** One bill per line: the merchant it's for, and the grouped category it's split out of. */
  merchantPattern: string | null
  replaces: string | null
}

export interface CategoryDiscovery {
  monthsAnalyzed: number
  proposed: DiscoveredCategory[]
  unused: { id: string; name: string; reason: string }[]
  unmatched: number
}

export type DebtStrategy = 'Avalanche' | 'Snowball'

export interface Debt {
  id: string
  name: string
  mask: string | null
  isCard: boolean
  balanceCents: number
  /** APR in basis points: 2499 = 24.99%. */
  aprBps: number | null
  minimumCents: number | null
  dueDay: number | null
  nextDueDate: string | null
  creditLimitCents: number | null
  utilizationPercent: number | null
  startingBalanceCents: number
  startedOn: string
  fromBank: boolean
  isHidden: boolean
  /** APR or minimum missing, so it isn't in the plan yet. */
  needsDetails: boolean
  paidOffBy: string | null
  interestCents: number
  /** A bank-linked card: its balance comes from the bank. */
  balanceFromBank: boolean
  /** Interest charged on it this year, and the latest charge (DECISIONS.md 232). */
  interestChargedThisYearCents: number
  lastInterestCents: number | null
  lastInterestDate: string | null
}

export interface DebtPlan {
  strategy: DebtStrategy
  minimumsCents: number
  extraCents: number
  extraSource: 'budget' | 'you'
  monthlyPaymentCents: number
  months: number
  debtFreeBy: string | null
  neverPaysOff: boolean
  totalInterestCents: number
  firstTarget: string | null
  chart: { month: string; balanceCents: number }[]
}

export interface DebtSummary {
  owedCents: number
  startingCents: number
  paidDownCents: number
  minimumsCents: number
  needsDetailsCount: number
  debts: Debt[]
  /** The last 12 months, oldest first: owed at the end of each, and card payments made. */
  history: { month: string; owedCents: number; paidCents: number }[] | null
  plan: DebtPlan | null
  comparison: {
    strategy: DebtStrategy
    months: number
    debtFreeBy: string | null
    neverPaysOff: boolean
    totalInterestCents: number
    interestDifferenceCents: number
    monthsDifference: number
  } | null
}

export interface DebtFields {
  name: string
  balanceCents: number | null
  aprBps: number | null
  minimumCents: number | null
  dueDay: number | null
  creditLimitCents: number | null
  startingBalanceCents: number | null
  isHidden?: boolean
}

/** One past savings transaction (DECISIONS.md 239): cents positive into savings, negative out. */
export interface SavingsHistoryItem {
  id: string
  date: string
  cents: number
  account: string
  description: string
  goal: string | null
}

/** Interest charged on every card and loan (DECISIONS.md 237): 12 calendar months, and each charge newest first. */
export interface InterestHistory {
  months: { month: string; cents: number; current: boolean }[]
  charges: { id: string; date: string; merchant: string; amountCents: number; debtId: string | null; debtName: string | null }[]
  lastYearCents: number
}

/** A payment a card or loan received (DECISIONS.md 240). */
export type DebtPayment = InterestHistory['charges'][number]

export const debtApi = {
  interest: () => request<InterestHistory>('GET', '/debt/interest'),
  payments: () => request<DebtPayment[]>('GET', '/debt/payments'),
  summary: (opts: { extraCents?: number; strategy?: DebtStrategy } = {}) => {
    const q = new URLSearchParams()
    if (opts.extraCents !== undefined) q.set('extra', String(opts.extraCents))
    if (opts.strategy) q.set('strategy', opts.strategy)
    const s = q.toString()
    return request<DebtSummary>('GET', `/debt${s ? `?${s}` : ''}`)
  },
  add: (d: DebtFields) => request<{ id: string }>('POST', '/debt', d),
  update: (id: string, d: DebtFields) => request<void>('PUT', `/debt/${id}`, d),
  remove: (id: string) => request<void>('DELETE', `/debt/${id}`),
  saveSettings: (strategy: DebtStrategy, extraCents: number | null) => request<void>('PUT', '/debt/settings', { strategy, extraCents }),
  fromBank: () => request<{ updated: number; message: string | null }>('POST', '/debt/from-bank'),
}

export type RecurringStatus = 'active' | 'priceUp' | 'priceDown' | 'possiblyCanceled' | 'canceled'

export interface RecurringItem {
  key: string
  name: string
  detectedName: string
  kind: 'Bill' | 'Subscription'
  frequency: 'Weekly' | 'Biweekly' | 'Monthly' | 'Quarterly' | 'Yearly'
  status: RecurringStatus
  amountCents: number
  previousAmountCents: number | null
  priceChangedOn: string | null
  monthlyCents: number
  lastDate: string
  nextExpected: string
  dueDay: number | null
  occurrences: number
  accountName: string
  categoryId: string | null
  categoryName: string | null
  /** Filed in a bill category, so the budget plans for it as a bill. */
  inBillCategory: boolean
  confirmed: boolean
  possibleDuplicate: boolean
  /** One merchant bills only this, so a rule can send its future charges to a category. */
  merchantWide: boolean
  /** Every charge found for it, newest first. */
  charges: { date: string; amountCents: number }[]
  /** An older bill that may be this one before its price changed (DECISIONS.md 181). */
  sameAs: { key: string; name: string; amountCents: number; firstDate: string; lastDate: string; occurrences: number } | null
  /** While that's being asked: the newer bill this one may have become. */
  replacedBy: string | null
  /** How many bills you joined into this one (Split apart undoes it), and whether its price varies (DECISIONS.md 197). */
  mergedCount?: number
  varies?: boolean
  /** "Still going: keep looking" is on since this day, and how many newer charges it found (DECISIONS.md 200). */
  lookingSince?: string | null
  found?: number
}

export interface ArchivedCategory {
  id: string
  name: string
  type: Category['type']
  monthlyLimitCents: number
  lastUsed: string | null
}

export interface RecurringSummary {
  monthlyCents: number
  annualCents: number
  alertCount: number
  items: RecurringItem[]
  dismissed: RecurringItem[]
}

export interface RecurringUpdate {
  key: string
  name?: string
  kind?: 'Bill' | 'Subscription'
  dueDay?: number
  clearDueDay?: boolean
  confirmed?: boolean
  dismissed?: boolean
  canceled?: boolean
  acknowledgePrice?: boolean
  /** This bill is the same as that one (its price changed): its charges join it (DECISIONS.md 181). */
  mergeInto?: string
  /** This bill isn't the same as that one: stop asking. */
  notSameAs?: string
  /** Undo joining: the bills joined into this one are their own again. */
  split?: boolean
  /** It's still going: find its later charges, even at a new price. False stops looking. */
  keepLooking?: boolean
}

/** Where debt and savings money in review can go (DECISIONS.md 227): your debts, your savings accounts and hand-tracked goals. */
export interface TowardChoices {
  /** accountId: the card's or loan's account, when it's linked (an interest charge on it is for that debt). */
  debts: { id: string; name: string; accountId?: string | null }[]
  savings: { id: string; name: string }[]
}

/** Where a charge in review can go when it's a bill (DECISIONS.md 225). */
export interface ChargeBillChoices {
  bills: { key: string; name: string; categoryName: string | null; amountCents: number }[]
  lines: { id: string; name: string }[]
  /** The bill it already belongs to. */
  current: string | null
  /** A repeating charge it's part of that isn't a bill yet. */
  detected: string | null
  suggested: string | null
}

export const recurringApi = {
  summary: () => request<RecurringSummary>('GET', '/recurring'),
  towardChoices: () => request<TowardChoices>('GET', '/transactions/toward'),
  fileToward: (transactionId: string, kind: 'debt' | 'interest' | 'savings', id: string) =>
    request<{ towardName: string; categoryName: string }>('POST', `/transactions/${transactionId}/toward`, { kind, id }),
  billChoices: (transactionId: string) => request<ChargeBillChoices>('GET', `/recurring/for/${transactionId}`),
  fileAsBill: (body: { transactionId: string; billKey?: string; categoryId?: string; name?: string }) =>
    request<{ categoryId: string; categoryName: string; moved: number }>('POST', '/recurring/file-charge', body),
  update: (u: RecurringUpdate) => request<void>('PUT', '/recurring', u),
  trackAsBill: (key: string) => request<{ categoryId: string; name: string }>('POST', '/recurring/track-as-bill', { key }),
  /** File a bill's charges (past and future) under another category. */
  moveToCategory: (key: string, categoryId: string) => request<{ categoryId: string; name: string }>('POST', '/recurring/category', { key, categoryId }),
  /** "Not a bill": its charges become spending in this category, and nothing expects it any more (DECISIONS.md 197). */
  notABill: (key: string, categoryId: string) => request<{ name: string }>('POST', '/recurring/not-a-bill', { key, categoryId }),
}

export interface ContributionStatus {
  arrangementId: string
  categoryId: string
  categoryName: string
  state: 'expected' | 'received' | 'differentAmount' | 'late'
  expectedCents: number
  receivedCents: number
  windowStart: string
  windowEnd: string
  shortfallCents: number
  /** Who sends it (DECISIONS.md 211). */
  friendId: string | null
  friendName: string | null
}

export interface PartnerOverview {
  partnerName: string | null
  contributions: {
    id: string
    categoryId: string
    categoryName: string
    amountCents: number
    windowFromDay: number
    windowToDay: number
    thisMonth: ContributionStatus | null
  }[]
  /** Transfers between you: waiting for a label, her money in, paying you back (in a category), or your share (in a category). */
  recent: {
    id: string
    date: string
    amountCents: number
    purpose: 'waiting' | 'contribution' | 'payback' | 'yourShare'
    categoryName: string | null
  }[]
  /** Her transfers from just before this month that may be this month's contribution, sent early. */
  earlyCandidates: EarlyDeposit[]
  /** Transfers you counted toward this month though they came before it started. */
  countedEarly: EarlyDeposit[]
}

/** Someone you share costs with (DECISIONS.md 205): just a name, or linked with their own account. */
export interface Friend {
  id: string
  name: string
  linked: boolean
  linkedSince: string | null
  /** An invite you sent that hasn't been used yet. */
  inviteExpiresUtc: string | null
  /** Their name on their bank transfers, for the one friend whose transfers are tracked (DECISIONS.md 207). */
  transferName: string | null
}

/** Your friends, and your name as they see it. Each linked account keeps its own budget. */
export interface SharedOverview {
  yourName: string | null
  friends: Friend[]
}

export interface InvitePeek {
  valid: boolean
  fromName: string | null
  problem: string | null
}

export const sharedApi = {
  overview: () => request<SharedOverview>('GET', '/shared'),
  setName: (name: string) => request<void>('PUT', '/shared/name', { name }),
  addFriend: (name: string) => request<Friend>('POST', '/shared/friends', { name }),
  renameFriend: (id: string, name: string) => request<void>('PUT', `/shared/friends/${id}`, { name }),
  removeFriend: (id: string) => request<void>('DELETE', `/shared/friends/${id}`),
  /** A new one-time invite for this friend; the token is only ever shown now, in the link. */
  invite: (id: string) => request<{ token: string; expiresUtc: string }>('POST', `/shared/friends/${id}/invite`),
  cancelInvite: (id: string) => request<void>('DELETE', `/shared/friends/${id}/invite`),
  unlink: (id: string) => request<void>('DELETE', `/shared/friends/${id}/link`),
  peek: (token: string) => request<InvitePeek>('GET', `/shared/invites/${encodeURIComponent(token)}`),
  join: (token: string) => request<void>('POST', '/shared/join', { token }),
}

/** The link to send: opens the app on the join screen. */
export const inviteLink = (token: string) => `${window.location.origin}/#/join/${token}`

/** The invite token in the address (#/join/<token>), if the app was opened from one. */
export function joinToken(): string | null {
  const m = window.location.hash.match(/^#\/join\/([A-Za-z0-9_-]+)$/)
  return m ? m[1] : null
}

export const partnerPlanApi = {
  /** One friend's money: their fixed contributions and recent transfers (DECISIONS.md 211). */
  overview: (friendId: string) => request<PartnerOverview>('GET', `/partner?friendId=${encodeURIComponent(friendId)}`),
  addContribution: (friendId: string, categoryId: string, amountCents: number, windowFromDay?: number, windowToDay?: number) =>
    request<{ id: string }>('POST', '/partner/contributions', { friendId, categoryId, amountCents, windowFromDay, windowToDay }),
  updateContribution: (id: string, categoryId: string, amountCents: number, windowFromDay?: number, windowToDay?: number) =>
    request<void>('PUT', `/partner/contributions/${id}`, { categoryId, amountCents, windowFromDay, windowToDay }),
  removeContribution: (id: string) => request<void>('DELETE', `/partner/contributions/${id}`),
  /** Her transfer from just before this budget month counts toward this month's contribution (DECISIONS.md 166). */
  countEarly: (transactionId: string) => request<void>('POST', `/partner/early/${transactionId}`),
  undoEarly: (transactionId: string) => request<void>('DELETE', `/partner/early/${transactionId}`),
}

export interface EarlyDeposit {
  id: string
  date: string
  amountCents: number
}

export type PriorityStep = 'StarterFund' | 'Debt' | 'Savings'

export interface SavingsGoal {
  id: string
  name: string
  targetCents: number
  accountId: string | null
  accountName: string | null
  manualSavedCents: number | null
  /** The monthly amount you set (null: your pace into its account). */
  monthlyCentsSet: number | null
  isStarter: boolean
  savedCents: number
  remainingCents: number
  percent: number
  reached: boolean
  monthlyCents: number
  monthlySource: 'you' | 'pace' | 'none'
  monthsToGo: number | null
  projectedDate: string | null
}

export interface SavingsSummary {
  totalSavedCents: number
  accounts: { id: string; name: string; mask: string | null; balanceCents: number; balanceFromBank: boolean; paceCents: number }[]
  goals: SavingsGoal[]
  priority: PriorityStep[]
  history: { date: string; balanceCents: number }[]
  /** Money moved into savings, newest first, tracked from your accounts. */
  transfers: { date: string; cents: number; account: string; description: string }[] | null
  thisMonthCents: number
  plannedCents: number
}

export interface GoalFields {
  name: string
  targetCents: number
  accountId: string | null
  manualSavedCents: number | null
  monthlyCents: number | null
}

export const savingsApi = {
  transactions: () => request<SavingsHistoryItem[]>('GET', '/savings/transactions'),
  summary: () => request<SavingsSummary>('GET', '/savings'),
  addGoal: (g: GoalFields) => request<{ id: string }>('POST', '/savings/goals', g),
  updateGoal: (id: string, g: GoalFields) => request<void>('PUT', `/savings/goals/${id}`, g),
  removeGoal: (id: string) => request<void>('DELETE', `/savings/goals/${id}`),
  setPriority: (order: PriorityStep[]) => request<void>('PUT', '/savings/priority', { order }),
  setBalance: (accountId: string, balanceCents: number) => request<void>('PUT', `/savings/accounts/${accountId}/balance`, { balanceCents }),
}

// Payday (milestone 9). The pay engine runs on the server; these only carry its results.
export interface PayLine {
  name: string
  cents: number
  note: string
}
export interface PayAmount {
  name: string
  cents: number
}
export interface PlanItem {
  key: string
  kind: 'keep' | 'debt' | 'cardspend' | 'sweep' | 'savings' | 'send'
  label: string
  cents: number
  destination: string
  why: string
  /** Checked off by you, or done according to your accounts (auto). */
  done: boolean
  auto: boolean
  /** For the check you're on now: what the app sees. */
  status: string | null
  /** For "keep": the bills it covers. */
  bills: { name: string; cents: number; due: string }[] | null
  /** "Pay off what you charged" on the check you're on can be skipped; the charges then stay on the card (DECISIONS.md 195). */
  skippable?: boolean
  skipped?: boolean
  /** For "send": the tasks to the same account merged into it, each checked off on its own (DECISIONS.md 220). */
  parts?: PlanItem[] | null
}
export interface PayCheck {
  payDate: string
  periodStart: string
  periodEnd: string
  hasSchedule: boolean
  grossCents: number
  taxableCents: number
  netCents: number
  lowNetCents: number
  highNetCents: number
  lowConfidence: boolean
  reasons: string[]
  notes: string[]
  lines: PayLine[]
  taxes: PayAmount[]
  deductions: PayAmount[]
  plan: { items: PlanItem[]; shortCents: number } | null
  /** The check you're living on now (paid most recently). */
  isCurrent: boolean
  /** What actually landed for it. */
  landedNetCents: number | null
}
export interface PayHistory {
  payDate: string
  predictedNetCents: number
  lowNetCents: number
  highNetCents: number
  actualNetCents: number | null
  varianceCents: number | null
  source: 'deposit' | 'stub' | null
}
export interface PayProfile {
  yearsOfService: number
  rateCents: number
  fullTime: boolean
  holidayPay: boolean
  flatNetPercent: number
  /** InFlight date of hire: years of service and the rate follow from it. */
  hireDate: string | null
  nextRaiseOn: string | null
  nextRaiseRateCents: number | null
}
export interface PaydaySummary {
  profile: PayProfile
  stubCount: number
  modelLearned: boolean
  modelEdited: boolean
  checks: PayCheck[]
  history: PayHistory[]
  scheduleMonths: string[]
  /** Schedules and pay stubs to upload now (DECISIONS.md 238). */
  uploadsDue?: { kind: 'schedule' | 'stub'; label: string; date: string }[]
}
export interface Trip {
  key: string
  code: string
  start: string
  end: string
  creditMinutes: number
  tafbMinutes: number
  revenueLegs: number
  overtime: boolean
  /** Dropped after importing: left out of pay. */
  dropped: boolean
  payCents: number
  creditPayCents: number
  boardingCents: number
  perDiemCents: number
  days: { date: string; report: string; release: string; creditMinutes: number; dutyMinutes: number; legs: number; route: string }[]
}
export interface ScheduleEntry {
  key: string
  code: string
  start: string
  end: string
  creditMinutes: number
  paid: boolean
}
export interface ScheduleMonth {
  year: number
  month: number
  source: 'text' | 'csv'
  reserve: boolean
  importedUtc: string | null
  creditMinutes: number
  shortfallMinutes: number
  overrideMinutes: number
  trips: Trip[]
  entries: ScheduleEntry[]
  checks: string[]
  warnings: string[]
}
export interface StubData {
  payDate: string
  periodStart: string
  periodEnd: string
  rateCents: number
  grossCents: number
  fedTaxableCents: number
  totalTaxesCents: number
  totalDeductionsCents: number
  netCents: number
  earnings: { name: string; rateCents: number | null; hours: number | null; currentCents: number }[]
  taxes: { name: string; currentCents: number }[]
  preTax: { name: string; currentCents: number }[]
  afterTax: { name: string; currentCents: number }[]
  userHoursEarned: number | null
  vacationHoursEarned: number | null
  ytdGrossCents: number | null
  ytdNetCents: number | null
}
export interface StubReview {
  stub: StubData
  checks: string[]
  problems: string[]
  replaces: boolean
}
export interface StubListItem {
  id: string
  payDate: string
  periodStart: string
  periodEnd: string
  grossCents: number
  netCents: number
}
export type CheckSchedule = 'Every' | 'SecondHalf' | 'FirstHalf'
export interface TaxRule {
  name: string
  kind: 'Rate' | 'Flat'
  schedule: CheckSchedule
  rate: number
  interceptCents: number
  base: 'FederalTaxable' | 'WagesBeforePreTax'
  roundToDollar: boolean
  flatCents: number
}
export interface DeductionRule {
  name: string
  cents: number
  schedule: CheckSchedule
  percentOfGross: number | null
}
export interface WithholdingModel {
  nonTaxable: string[]
  preTax: DeductionRule[]
  afterTax: DeductionRule[]
  taxes: TaxRule[]
  recurringEarnings: { name: string; cents: number; schedule: CheckSchedule }[]
  leftOut: string[]
  ptoHoursPerCheck: number
  minTaxableCents: number
  maxTaxableCents: number
  stubCount: number
  notes: string[]
}
export interface ModelView {
  model: WithholdingModel | null
  edited: boolean
  backTests: { payDate: string; actualNetCents: number; predictedNetCents: number; taxErrorCents: number; errorCents: number }[]
  stubCount: number
}
export interface PayRules {
  rates: { years: number; centsPerHour: number }[]
  perDiemCentsPerHour: number
  minDayCreditMinutes: number
  dutyHoursPerCreditHour: number
  tafbHoursPerCreditHour: number
  lineGuaranteeHours: number
  reserveGuaranteeHours: number
  partTimeGuaranteeHours: number
  overrideAboveHours: number
  overrideCentsPerHour: number
  overtimeMultiplier: number
  holidayCreditMinutes: number
  holidays: string[]
  boardingHoursPerLeg: number
  boardingRateFactor: number
  reportMinutesBefore: number
  reportMinutesBeforeInternational: number
  releaseMinutesAfter: number
  pairingBreakHours: number
  base: string
  airports: { code: string; timeZoneId: string; international: boolean }[]
  firstHalfPayDay: number
  secondHalfPayDay: number
  lines: { regular: string; boarding: string; perDiem: string; override: string; holiday: string; overtime: string; pto: string[] }
}

function fileForm(file: File, extra?: Record<string, string>) {
  const form = new FormData()
  form.append('file', file)
  for (const [k, v] of Object.entries(extra ?? {})) form.append(k, v)
  return form
}

/** Payday plan steps still to do on the check you're on now: the Plan tab's badge (DECISIONS.md 188). */
export const openPlanItems = (s: PaydaySummary) =>
  s.checks.filter((c) => c.isCurrent && c.plan).reduce((n, c) => n + c.plan!.items.filter((i) => !i.done && !i.skipped).length, 0)

/** What the pay side adds to the Tasks badge: open steps on the check you're on, plus uploads due (DECISIONS.md 238). */
export const payBadge = (s: PaydaySummary) => openPlanItems(s) + (s.uploadsDue?.length ?? 0)

export const paydayApi = {
  summary: () => request<PaydaySummary>('GET', '/payday'),
  setDone: (payDate: string, key: string, done: boolean) =>
    request<void>('PUT', `/payday/plan/${payDate}/${encodeURIComponent(key)}`, { done }),
  setSkipped: (payDate: string, key: string, skipped: boolean) =>
    request<void>('PUT', `/payday/plan/${payDate}/${encodeURIComponent(key)}`, { done: false, skipped }),
  setProfile: (p: Omit<PayProfile, 'rateCents' | 'nextRaiseOn' | 'nextRaiseRateCents'>) => request<void>('PUT', '/payday/profile', p),
  rules: () => request<{ rules: PayRules; edited: boolean }>('GET', '/payday/rules'),
  setRules: (r: PayRules) => request<void>('PUT', '/payday/rules', r),
  resetRules: () => request<void>('DELETE', '/payday/rules'),
  previewSchedule: (file: File) =>
    upload<{ month: ScheduleMonth; replaces: boolean }>('/payday/schedule', fileForm(file, { confirm: 'false' })),
  importSchedule: (file: File) => upload<{ month: ScheduleMonth }>('/payday/schedule', fileForm(file, { confirm: 'true' })),
  schedule: () => request<ScheduleMonth[]>('GET', '/payday/schedule'),
  setChoices: (year: number, month: number, reserve: boolean, overtime: string[], paidEntries: string[], dropped: string[]) =>
    request<void>('PUT', `/payday/schedule/${year}/${month}`, { reserve, overtime, paidEntries, dropped }),
  removeMonth: (year: number, month: number) => request<void>('DELETE', `/payday/schedule/${year}/${month}`),
  reviewStub: (file: File) => upload<StubReview>('/payday/paystubs', fileForm(file)),
  confirmStub: (stub: StubData) =>
    request<{ id: string; predictedNetCents: number | null; actualNetCents: number; varianceCents: number | null }>(
      'POST',
      '/payday/paystubs/confirm',
      stub,
    ),
  stubs: () => request<StubListItem[]>('GET', '/payday/paystubs'),
  removeStub: (id: string) => request<void>('DELETE', `/payday/paystubs/${id}`),
  model: () => request<ModelView>('GET', '/payday/model'),
  setModel: (m: WithholdingModel) => request<void>('PUT', '/payday/model', m),
  resetModel: () => request<void>('DELETE', '/payday/model'),
}

// Phone notifications (DECISIONS.md 151).
export const pushApi = {
  status: () => request<{ enabled: boolean; publicKey: string | null; problem: string | null }>('GET', '/push'),
  subscribe: (s: PushSubscriptionJSON) =>
    request<void>('POST', '/push/subscribe', { endpoint: s.endpoint, keys: { p256dh: s.keys?.p256dh, auth: s.keys?.auth } }),
  unsubscribe: (endpoint: string) => request<void>('POST', '/push/unsubscribe', { endpoint }),
  test: () => request<void>('POST', '/push/test'),
  /** Each kind of notification and whether it's on, for the whole household (DECISIONS.md 163). */
  types: () => request<NotificationType[]>('GET', '/push/types'),
  setType: (id: string, on: boolean) => request<void>('PUT', `/push/types/${encodeURIComponent(id)}`, { on }),
}

/** Transaction search filters. Amounts are dollars as typed ("12.50"), dates are yyyy-mm-dd; empty means not set. */
export interface SearchFilters {
  q: string
  categoryId: string
  min: string
  max: string
  from: string
  to: string
}

export interface NotificationType {
  id: string
  title: string
  detail: string
  on: boolean
}

/** A recurring charge the app found under spending (or no category) that you haven't said anything about yet. */
export const isNewFind = (i: RecurringItem) =>
  !i.inBillCategory && !i.confirmed && i.status !== 'possiblyCanceled' && i.status !== 'canceled'

/** What Review → Bills asks about (DECISIONS.md 152): new finds, price changes, charges that stopped, possible duplicates. */
export const needsLook = (i: RecurringItem) =>
  isNewFind(i) ||
  i.status === 'priceUp' ||
  i.status === 'priceDown' ||
  (i.status === 'possiblyCanceled' && !i.replacedBy) ||
  i.possibleDuplicate ||
  i.sameAs != null

/**
 * How much a spending category matters when money is short (DECISIONS.md 182): the automatic budget squeezes Low
 * (nice to have) first, then Medium (important), then High (must have).
 */
export type Priority = 'Low' | 'Medium' | 'High'

export const priorityLabels: Record<Priority, string> = { High: 'Must have', Medium: 'Important', Low: 'Nice to have' }

/** The bank side of the month (DECISIONS.md 183): what the accounts can cover for spending the rest of it. */
export interface CashCheck {
  checkingCents: number
  incomeStillComingCents: number
  partnerStillComingCents: number
  billsStillDueCents: number
  cardsToPayCents: number
  savingsStillToMoveCents: number
  leftoverToMoveCents: number
  availableCents: number
}

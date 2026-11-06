// Thin client for the backend. The frontend never computes money; it only displays what the API returns.

export type Status = 'healthy' | 'low' | 'overspent'

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
}

export interface Me {
  email: string
  householdId: string
}

export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api${path}`, {
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
  register: (email: string, password: string) => request<Me>('POST', '/auth/register', { email, password }),
  logout: () => request<void>('POST', '/auth/logout'),
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
  isDiscretionary: boolean
  sortOrder: number
}

export interface Transaction {
  id: string
  date: string
  amountCents: number
  merchant: string
  description: string
  kind: 'Purchase' | 'Refund' | 'Income' | 'Fee' | 'CardPayment' | 'Transfer' | 'Other'
  categoryId: string | null
  categoryName: string | null
  categorySource: 'None' | 'Bank' | 'Rule' | 'Transfer' | 'User'
  isTransfer: boolean
  pending: boolean
  accountId: string
  accountName: string
  bankCategory: string | null
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
  const res = await fetch(`/api${path}`, { method: 'POST', credentials: 'same-origin', body: form })
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
  monthTransactions: () => request<{ from: string; to: string; items: Transaction[] }>('GET', '/transactions'),
  inbox: () => request<Transaction[]>('GET', '/transactions/inbox'),
  inboxCount: () => request<{ count: number }>('GET', '/transactions/inbox/count'),
  updateTransaction: (id: string, categoryId: string | null, isTransfer: boolean, rulePattern: string | null) =>
    request<{ transaction: Transaction; ruleAppliedTo: number }>('PATCH', `/transactions/${id}`, {
      categoryId,
      isTransfer,
      rulePattern,
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
  isDiscretionary: boolean
  limitCents: number
  spentCents: number
  remainingCents: number
  health: Health
  transactionCount: number
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
  groups: BudgetGroup[]
}

export const budgetSummaryApi = {
  get: () => request<BudgetSummary>('GET', '/budget/summary'),
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
}

export const plaidApi = {
  config: () => request<PlaidConfig>('GET', '/plaid/config'),
  linkToken: (itemId?: string) => request<{ linkToken: string }>('POST', '/plaid/link-token', { itemId: itemId ?? null }),
  exchange: (publicToken: string) => request<LinkResult>('POST', '/plaid/exchange', { publicToken }),
  sandboxLink: (dynamic: boolean) => request<LinkResult>('POST', '/plaid/sandbox/link', { dynamic }),
  sandboxRefresh: (itemId: string) => request<void>('POST', `/plaid/sandbox/items/${itemId}/refresh`),
  confirmAccounts: (itemId: string, mappings: AccountMapping[]) =>
    request<SyncResult>('POST', `/plaid/items/${itemId}/accounts`, { mappings }),
  items: () => request<PlaidItem[]>('GET', '/plaid/items'),
  removeItem: (id: string) => request<void>('DELETE', `/plaid/items/${id}`),
  reconnected: (id: string) => request<SyncResult>('POST', `/plaid/items/${id}/reconnected`),
  syncAll: () => request<SyncResult[]>('POST', '/sync'),
}

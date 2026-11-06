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

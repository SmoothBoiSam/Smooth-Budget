// Display formatting only. Amounts arrive from the API as integer cents.

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })

export function formatCents(cents: number): string {
  return usd.format(cents / 100)
}

/** "2026-11-06" -> "Nov 6" without time-zone drift. */
export function formatDay(isoDate: string): string {
  const [y, m, d] = isoDate.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
}

export function formatTime(isoUtc: string): string {
  return new Date(isoUtc).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
}

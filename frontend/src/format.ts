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

/** Money out shows as a plain amount; money in gets a "+". */
export function formatSigned(cents: number): string {
  return cents < 0 ? `+${formatCents(-cents)}` : formatCents(cents)
}

/** "KING SOOPERS DENVER CO" -> "King Soopers Denver Co" for display. */
export function titleCase(s: string): string {
  return s.toLowerCase().replace(/\b([a-z])/g, (c) => c.toUpperCase())
}

/** Parses what someone typed as dollars ("450", "$1,234.5", "12.34") into integer cents, without floating point. */
export function parseDollars(input: string): number | null {
  const s = input.trim().replace(/[$,\s]/g, '')
  const m = /^(\d+)(?:\.(\d{0,2}))?$/.exec(s)
  if (!m) return null
  return Number(m[1]) * 100 + Number((m[2] ?? '').padEnd(2, '0'))
}

/** Cents as a plain editable dollar string: 45000 -> "450", 4550 -> "45.50". */
export function centsToInput(cents: number): string {
  const d = Math.floor(cents / 100)
  const c = cents % 100
  return c === 0 ? String(d) : `${d}.${String(c).padStart(2, '0')}`
}

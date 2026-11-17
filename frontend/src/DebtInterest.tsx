import { useEffect, useState } from 'react'
import { debtApi, type InterestHistory } from './api'
import { CardLink } from './ui'
import { formatCents } from './format'

const shortMonth = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { month: 'short' })
const longMonth = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
const whole = (cents: number) => '$' + Math.round(cents / 100).toLocaleString('en-US')

/**
 * On the Debt tab (DECISIONS.md 237): interest charged on every card and loan, one bar a calendar month for the last
 * 12, this month's lighter since it's still going. Tap a bar for its amount; the link opens every charge.
 */
export function InterestCard() {
  const [h, setH] = useState<InterestHistory | null>(null)
  const [picked, setPicked] = useState<number | null>(null)
  useEffect(() => {
    let live = true
    debtApi
      .interest()
      .then((x) => live && setH(x))
      .catch(() => {}) // extra: the Debt tab works without it
    return () => {
      live = false
    }
  }, [])
  if (!h || h.charges.length === 0) return null

  const months = h.months
  const top = Math.max(...months.map((m) => m.cents), 1) * 1.12
  const pct = (cents: number) => `${(Math.max(0, cents) / top) * 100}%`
  const shown = picked ?? months.length - 1
  const m = months[shown]
  const peak = months.reduce((a, b) => (b.cents > a.cents ? b : a))

  return (
    <section aria-labelledby="interest-h" className="mt-4 rounded-3xl neu p-5">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="interest-h" className="text-sm font-semibold">
          Interest charged
        </h2>
        <p aria-live="polite" className="text-right text-xs text-ink-soft">
          {longMonth(m.month)}
          {m.current ? ' so far' : ''}: <span className="font-semibold text-ink tabular-nums">{formatCents(m.cents)}</span>
        </p>
      </div>
      <p className="mt-0.5 text-xs text-ink-soft tabular-nums">{formatCents(h.lastYearCents)} in the last 12 months</p>

      <div aria-hidden className="relative mt-4 h-32">
        {peak.cents > 0 && (
          <div className="absolute inset-x-0 border-t border-dashed border-ink/15" style={{ bottom: pct(peak.cents) }}>
            <span className="absolute right-0 -top-5 rounded bg-surface px-1 text-[11px] text-ink-faint">{whole(peak.cents)}</span>
          </div>
        )}
        <div className="absolute inset-0 flex items-end gap-1.5 border-b border-ink/15">
          {months.map((x, i) => (
            <button key={x.month} type="button" tabIndex={-1} onClick={() => setPicked(i)} className="flex h-full flex-1 items-end justify-center">
              <span
                className={`block w-full max-w-7 rounded-t bg-danger/80 ${x.current ? 'opacity-45' : ''} ${i === shown ? 'ring-2 ring-danger ring-offset-2 ring-offset-surface' : ''}`}
                style={{ height: pct(x.cents), minHeight: x.cents > 0 ? 3 : 0 }}
              />
            </button>
          ))}
        </div>
      </div>
      <div aria-hidden className="mt-1.5 flex gap-1.5">
        {months.map((x, i) => (
          <span key={x.month} className={`flex-1 text-center text-[10px] ${i === shown ? 'font-semibold text-ink' : 'text-ink-soft'}`}>
            {shortMonth(x.month).slice(0, 1)}
          </span>
        ))}
      </div>

      {/* The same numbers for screen readers. */}
      <ul className="sr-only">
        {months.map((x) => (
          <li key={x.month}>
            {longMonth(x.month)}
            {x.current ? ' so far' : ''}: {formatCents(x.cents)} interest
          </li>
        ))}
      </ul>

      <CardLink href="#/transactions?type=interest&back=debt">Past interest charges</CardLink>
    </section>
  )
}


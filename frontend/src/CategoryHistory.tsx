import { useEffect, useState } from 'react'
import { budgetSummaryApi, type CategoryMonth } from './api'
import { formatCents } from './format'

const monthName = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { month: 'short' })
const longMonth = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { month: 'long' })
const whole = (cents: number) => '$' + Math.round(cents / 100).toLocaleString('en-US')

/**
 * A category's spending over the past months (DECISIONS.md 222): one bar a month, this month's lighter since it's still
 * going, and a dashed line across at this month's budget. Tap a bar for its amount.
 */
export default function CategoryHistory({ categoryId, limitCents, spending }: { categoryId: string; limitCents: number; spending: boolean }) {
  const [months, setMonths] = useState<CategoryMonth[] | null>(null)
  const [picked, setPicked] = useState<number | null>(null)

  useEffect(() => {
    let live = true
    budgetSummaryApi
      .history(categoryId)
      .then((h) => live && setMonths(h.months))
      .catch(() => live && setMonths([]))
    return () => {
      live = false
    }
  }, [categoryId])

  // Nothing to compare until there's an earlier month.
  if (!months || months.length < 2) return null

  const top = Math.max(limitCents, ...months.map((m) => m.spentCents), 1) * 1.12
  const pct = (cents: number) => `${(Math.max(0, cents) / top) * 100}%`
  const shown = picked ?? months.length - 1
  const m = months[shown]
  const verb = spending ? 'spent' : 'paid'

  return (
    <section aria-labelledby="history-h" className="mt-4 rounded-3xl neu p-5">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="history-h" className="text-sm font-semibold">
          Past months
        </h2>
        <p aria-live="polite" className="text-right text-xs text-ink-soft">
          {longMonth(m.month)}
          {m.current ? ' so far' : ''}: <span className="font-semibold text-ink tabular-nums">{formatCents(m.spentCents)}</span>
        </p>
      </div>

      <div aria-hidden className="relative mt-4 h-36">
        {/* This month's budget, across every month. */}
        {limitCents > 0 && (
          <div className="absolute inset-x-0 z-10 border-t-2 border-dashed border-ink-soft/70" style={{ bottom: pct(limitCents) }}>
            <span className="absolute right-0 -top-5 rounded bg-surface px-1 text-[11px] font-medium text-ink-soft">{whole(limitCents)} budget</span>
          </div>
        )}
        <div className="absolute inset-0 flex items-end gap-2 border-b border-ink/15">
          {months.map((x, i) => (
            <button
              key={x.month}
              type="button"
              tabIndex={-1}
              onClick={() => setPicked(i)}
              className="flex h-full flex-1 items-end justify-center"
            >
              <span
                className={`block w-full max-w-9 rounded-t bg-accent transition-opacity ${x.current ? 'opacity-45' : ''} ${i === shown ? 'ring-2 ring-accent ring-offset-2 ring-offset-surface' : ''}`}
                style={{ height: pct(x.spentCents), minHeight: x.spentCents > 0 ? 3 : 0 }}
              />
            </button>
          ))}
        </div>
      </div>
      <div aria-hidden className="mt-1.5 flex gap-2">
        {months.map((x, i) => (
          <span key={x.month} className={`flex-1 text-center text-[11px] ${i === shown ? 'font-semibold text-ink' : 'text-ink-soft'}`}>
            {x.current ? 'Now' : monthName(x.month)}
          </span>
        ))}
      </div>

      {/* The same numbers for screen readers. */}
      <ul className="sr-only">
        {months.map((x) => (
          <li key={x.month}>
            {longMonth(x.month)}
            {x.current ? ' so far' : ''}: {formatCents(x.spentCents)} {verb}
          </li>
        ))}
        {limitCents > 0 && <li>This month's budget: {formatCents(limitCents)}</li>}
      </ul>
    </section>
  )
}

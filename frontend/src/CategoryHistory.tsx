import { useEffect, useState } from 'react'
import { budgetSummaryApi, type CategoryMonth } from './api'
import { formatCents } from './format'

const monthName = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { month: 'short' })
const longMonth = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { month: 'long' })
const whole = (cents: number) => '$' + Math.round(cents / 100).toLocaleString('en-US')

/**
 * A category's spending over the past months (DECISIONS.md 222): one bar a month, this month's lighter since it's still
 * going, and a line across at this month's budget. When the automatic budget squeezed it to fit the month's money, a
 * fainter dashed line shows where it would be otherwise (DECISIONS.md 223). Tap a bar for its amount; the transactions
 * below follow the month you tap (DECISIONS.md 234).
 */
export default function CategoryHistory({
  categoryId,
  limitCents,
  spending,
  onPick,
}: {
  categoryId: string
  limitCents: number
  spending: boolean
  onPick?: (m: CategoryMonth) => void
}) {
  const [months, setMonths] = useState<CategoryMonth[] | null>(null)
  const [unsqueezed, setUnsqueezed] = useState<number | null>(null)
  const [picked, setPicked] = useState<number | null>(null)

  useEffect(() => {
    let live = true
    budgetSummaryApi
      .history(categoryId)
      .then((h) => {
        if (!live) return
        setMonths(h.months)
        setPicked(null)
        if (h.months.length > 0) onPick?.(h.months[h.months.length - 1])
        setUnsqueezed(h.unsqueezedCents && h.unsqueezedCents > limitCents ? h.unsqueezedCents : null)
      })
      .catch(() => live && setMonths([]))
    return () => {
      live = false
    }
  }, [categoryId, limitCents, onPick])

  // Nothing to compare until there's an earlier month.
  if (!months || months.length < 2) return null

  const top = Math.max(limitCents, unsqueezed ?? 0, ...months.map((m) => m.spentCents), 1) * 1.12
  const pct = (cents: number) => `${(Math.max(0, cents) / top) * 100}%`
  const shown = picked ?? months.length - 1
  const m = months[shown]
  const verb = spending ? 'spent' : 'paid'

  return (
    <section aria-labelledby="history-h" className="mt-4 rounded-3xl neu p-5">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="history-h" className="text-sm font-semibold">
          Category history
        </h2>
        <p aria-live="polite" className="text-right text-xs text-ink-soft">
          {longMonth(m.month)}
          {m.current ? ' so far' : ''}: <span className="font-semibold text-ink tabular-nums">{formatCents(m.spentCents)}</span>
        </p>
      </div>

      <div aria-hidden className="relative mt-4 h-36">
        {/* Where it would be without the squeeze: fainter, labelled on the left so the two labels never collide. */}
        {unsqueezed !== null && (
          <div className="absolute inset-x-0 z-10 border-t-2 border-dashed border-ink-faint/80" style={{ bottom: pct(unsqueezed) }}>
            <span className="absolute left-0 -top-5 rounded bg-surface px-1 text-[11px] text-ink-faint">{whole(unsqueezed)} usual</span>
          </div>
        )}
        {/* This month's budget, across every month: solid when it's the squeezed one. */}
        {limitCents > 0 && (
          <div
            className={`absolute inset-x-0 z-10 border-t-2 ${unsqueezed !== null ? 'border-solid border-ink-soft' : 'border-dashed border-ink-soft/70'}`}
            style={{ bottom: pct(limitCents) }}
          >
            <span className="absolute right-0 -top-5 rounded bg-surface px-1 text-[11px] font-medium text-ink-soft">
              {whole(limitCents)} {unsqueezed !== null ? 'now' : 'budget'}
            </span>
          </div>
        )}
        <div className="absolute inset-0 flex items-end gap-2 border-b border-ink/15">
          {months.map((x, i) => (
            <button
              key={x.month}
              type="button"
              tabIndex={-1}
              onClick={() => {
                setPicked(i)
                onPick?.(x)
              }}
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
            {monthName(x.month)}
          </span>
        ))}
      </div>

      {unsqueezed !== null && (
        <p className="mt-3 text-xs text-ink-soft">
          Squeezed from {whole(unsqueezed)} to {whole(limitCents)} so the plan fits the month's money and what your accounts can cover.
        </p>
      )}

      {/* The same numbers for screen readers. */}
      <ul className="sr-only">
        {months.map((x) => (
          <li key={x.month}>
            {longMonth(x.month)}
            {x.current ? ' so far' : ''}: {formatCents(x.spentCents)} {verb}
          </li>
        ))}
        {limitCents > 0 && <li>This month's budget: {formatCents(limitCents)}</li>}
        {unsqueezed !== null && <li>Before squeezing to fit this month's money: {formatCents(unsqueezed)}</li>}
      </ul>
    </section>
  )
}

import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, debtApi, type InterestHistory } from './api'
import { ErrorNotice, Loading, Page, PageHeader } from './ui'
import { formatCents, formatDay, titleCase } from './format'

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

      <a href="#/debt/interest" className="mt-4 flex items-center justify-between rounded-2xl neu-sm px-4 py-2.5 text-sm font-semibold">
        Past interest charges
        <span className="text-ink-soft" aria-hidden>
          ›
        </span>
      </a>
    </section>
  )
}

/** Debt → Past interest charges (DECISIONS.md 237): every interest charge, newest first, by month, for one card or all. */
export default function InterestScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [h, setH] = useState<InterestHistory | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [debt, setDebt] = useState<string>('all')

  const load = useCallback(
    () =>
      debtApi
        .interest()
        .then((x) => {
          setH(x)
          setError(null)
        })
        .catch((e) => {
          if (e instanceof ApiError && e.status === 401) return onSignedOut()
          setError(e instanceof Error ? e.message : 'Something went wrong.')
        }),
    [onSignedOut],
  )
  useEffect(() => {
    void load()
  }, [load])

  // The cards and loans that have charges, for the filter.
  const debts = useMemo(() => {
    const seen = new Map<string, string>()
    for (const c of h?.charges ?? []) if (c.debtId && c.debtName) seen.set(c.debtId, c.debtName)
    return [...seen].map(([id, name]) => ({ id, name }))
  }, [h])
  const charges = (h?.charges ?? []).filter((c) => debt === 'all' || c.debtId === debt)
  const byMonth = useMemo(() => {
    const groups: { month: string; total: number; items: typeof charges }[] = []
    for (const c of charges) {
      const month = c.date.slice(0, 7)
      const g = groups.at(-1)
      if (g && g.month === month) {
        g.items.push(c)
        g.total += c.amountCents
      } else groups.push({ month, total: c.amountCents, items: [c] })
    }
    return groups
  }, [charges])

  return (
    <Page>
      <PageHeader title="Interest charges" back={{ href: '#/debt', label: 'Debt' }} />
      {!h ? (
        error ? (
          <ErrorNotice message={error} onRetry={() => void load()} />
        ) : (
          <Loading />
        )
      ) : (
        <>
          {debts.length > 1 && (
            <div className="mt-4 flex flex-wrap gap-2" role="tablist" aria-label="Which card or loan">
              {[{ id: 'all', name: 'All' }, ...debts].map((d) => (
                <button
                  key={d.id}
                  role="tab"
                  aria-selected={debt === d.id}
                  type="button"
                  onClick={() => setDebt(d.id)}
                  className={`rounded-full px-3 py-1.5 text-xs font-semibold ${debt === d.id ? 'bg-accent text-accent-ink' : 'bg-ink/[0.06] text-ink-soft'}`}
                >
                  {d.name}
                </button>
              ))}
            </div>
          )}
          {byMonth.length === 0 ? (
            <p className="mt-4 rounded-3xl neu px-5 py-4 text-sm text-ink-soft">No interest charges yet.</p>
          ) : (
            byMonth.map((g) => (
              <section key={g.month} className="mt-5">
                <h2 className="flex items-baseline justify-between px-1 text-sm font-semibold text-ink-soft">
                  <span>{longMonth(g.month + '-01')}</span>
                  <span className="tabular-nums">{formatCents(g.total)}</span>
                </h2>
                <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-3xl neu">
                  {g.items.map((c) => (
                    <li key={c.id} className="flex items-center gap-3 px-5 py-3">
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-sm font-medium">{titleCase(c.merchant)}</p>
                        <p className="truncate text-xs text-ink-faint">
                          {formatDay(c.date)}
                          {c.debtName ? ` · ${c.debtName}` : ''}
                        </p>
                      </div>
                      <span className="shrink-0 text-sm font-semibold tabular-nums">{formatCents(c.amountCents)}</span>
                    </li>
                  ))}
                </ul>
              </section>
            ))
          )}
        </>
      )}
    </Page>
  )
}

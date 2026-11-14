import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, paydayApi, type IncomeItem, type IncomeKind, type IncomeMonth, type IncomeSummary } from './api'
import { CardLink, ErrorNotice, Loading, Page, PageHeader } from './ui'
import { formatCents, formatDay, titleCase } from './format'

const shortMonth = (iso: string) => new Date(iso.slice(0, 7) + '-01T12:00:00').toLocaleDateString('en-US', { month: 'short' })
const longMonth = (ym: string) => new Date(ym + '-01T12:00:00').toLocaleDateString('en-US', { month: 'long', year: 'numeric' })

/** The three kinds of income, in a fixed order and color (checked for colorblind separation and contrast, DECISIONS.md 254). */
const series: { kind: IncomeKind; key: 'paycheckCents' | 'friendCents' | 'otherCents'; label: string; color: string }[] = [
  { kind: 'paycheck', key: 'paycheckCents', label: 'Paychecks', color: 'var(--color-primary)' },
  { kind: 'friend', key: 'friendCents', label: 'Friends', color: 'var(--color-accent)' },
  { kind: 'other', key: 'otherCents', label: 'Other', color: 'var(--color-chart-other)' },
]
const total = (m: IncomeMonth) => m.paycheckCents + m.friendCents + m.otherCents

export const incomeKindLabel = (i: { kind: IncomeKind; friendName: string | null }) =>
  i.kind === 'paycheck' ? 'Paycheck' : i.kind === 'friend' ? (i.friendName ? `From ${i.friendName}` : 'Friend’s contribution') : 'Other income'

/**
 * The Income card at the top of the Income page (DECISIONS.md 254), like Debt's and Savings' cards: what's come in this
 * month, a year of it by kind, and a button to every income transaction.
 */
export function IncomeCard({ s }: { s: IncomeSummary }) {
  const chart = s.months.some((m) => total(m) > 0)
  return (
    <section aria-labelledby="income-h" className="mt-4 rounded-3xl neu p-5">
      <h2 id="income-h" className="text-sm font-semibold">
        Income this month
      </h2>
      <p className="text-4xl font-bold tabular-nums tracking-tight">{formatCents(s.thisMonthCents)}</p>
      {s.toReview > 0 && (
        <a href="#/tasks/transactions" className="mt-2 block rounded-xl neu-inset px-3 py-2 text-xs text-gold-ink">
          {s.toReview} {s.toReview === 1 ? 'deposit is' : 'deposits are'} waiting in Tasks for you to say what {s.toReview === 1 ? 'it is' : 'they are'}.
        </a>
      )}
      {chart ? <IncomeChart months={s.months} /> : <p className="mt-2 text-xs text-ink-soft">No income seen yet. It shows here once your checking account syncs.</p>}
      <CardLink href="#/income/history">Past income</CardLink>
    </section>
  )
}

/** Income by month for the last year, stacked by kind; tap or hover a month for its numbers. */
function IncomeChart({ months }: { months: IncomeMonth[] }) {
  const [hover, setHover] = useState<number | null>(null)
  const W = 340
  const H = 150
  const pad = { l: 44, r: 4, t: 10, b: 22 }
  const top = niceCeil(Math.max(...months.map(total), 1))
  const slot = (W - pad.l - pad.r) / months.length
  const bar = Math.min(16, slot * 0.6)
  const y = (c: number) => pad.t + (1 - c / top) * (H - pad.t - pad.b)
  const h = hover === null ? null : months[hover]

  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    const box = e.currentTarget.getBoundingClientRect()
    const px = ((e.clientX - box.left) / box.width) * W
    setHover(Math.max(0, Math.min(months.length - 1, Math.floor((px - pad.l) / slot))))
  }

  return (
    <figure className="mt-3">
      <figcaption className="flex min-h-4 items-baseline justify-between gap-3 text-xs text-ink-soft">
        <span>The last year</span>
        <span className="tabular-nums" aria-live="polite">
          {h ? `${longMonth(h.month.slice(0, 7))}: ${formatCents(total(h))}` : ''}
        </span>
      </figcaption>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        className="mt-2 w-full touch-none select-none"
        role="img"
        aria-label={`Income by month over the last year, from ${formatCents(total(months[0]))} to ${formatCents(total(months[months.length - 1]))}.`}
        onPointerMove={onMove}
        onPointerDown={onMove}
        onPointerLeave={() => setHover(null)}
      >
        {[0, 0.5, 1].map((f) => (
          <g key={f}>
            <line x1={pad.l} x2={W - pad.r} y1={y(top * f)} y2={y(top * f)} className="stroke-slate-200" strokeWidth={1} />
            <text x={pad.l - 6} y={y(top * f) + 3} textAnchor="end" className="fill-slate-500 text-[10px] tabular-nums">
              {shortMoney(top * f)}
            </text>
          </g>
        ))}
        {months.map((m, i) => {
          const x = pad.l + i * slot + (slot - bar) / 2
          let base = 0
          const parts = series.filter((s) => m[s.key] > 0)
          return (
            <g key={m.month} opacity={hover === null || hover === i ? 1 : 0.45}>
              {parts.map((s, k) => {
                const y0 = y(base)
                base += m[s.key]
                const y1 = y(base)
                // A 2px gap between kinds (off the top of each lower one); the top of the bar is rounded.
                const height = Math.max(0, y0 - y1)
                const last = k === parts.length - 1
                return last ? (
                  <path key={s.kind} d={roundedTop(x, y1, bar, height, Math.min(4, height, bar / 2))} style={{ fill: s.color }} />
                ) : (
                  <rect key={s.kind} x={x} y={y1 + 2} width={bar} height={Math.max(0, y0 - y1 - 2)} style={{ fill: s.color }} />
                )
              })}
            </g>
          )
        })}
        {months.map((m, i) =>
          i % 3 === 2 || i === months.length - 1 ? (
            <text key={m.month} x={pad.l + i * slot + slot / 2} y={H - 6} textAnchor="middle" className="fill-slate-500 text-[10px]">
              {shortMonth(m.month)}
            </text>
          ) : null,
        )}
      </svg>
      <ul className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-ink-soft">
        {series.map((s) => (
          <li key={s.kind} className="flex items-center gap-1.5">
            <span aria-hidden className="inline-block size-2.5 rounded-sm" style={{ background: s.color }} />
            {s.label}
            {h && <span className="tabular-nums text-ink">{formatCents(h[s.key])}</span>}
          </li>
        ))}
      </ul>
      {/* The same numbers as a table, for screen readers. */}
      <table className="sr-only">
        <caption>Income by month</caption>
        <thead>
          <tr>
            <th>Month</th>
            {series.map((s) => (
              <th key={s.kind}>{s.label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {months.map((m) => (
            <tr key={m.month}>
              <td>{longMonth(m.month.slice(0, 7))}</td>
              {series.map((s) => (
                <td key={s.kind}>{formatCents(m[s.key])}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  )
}

function roundedTop(x: number, y: number, w: number, h: number, r: number) {
  if (h <= 0) return ''
  return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`
}

function niceCeil(cents: number) {
  const dollars = cents / 100
  const pow = 10 ** Math.floor(Math.log10(dollars))
  const step = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].map((m) => m * pow).find((v) => v >= dollars) ?? 10 * pow
  return step * 100
}

function shortMoney(cents: number) {
  const d = cents / 100
  return d >= 1000 ? `$${(d / 1000).toFixed(d % 1000 === 0 ? 0 : 1)}k` : `$${Math.round(d)}`
}

/**
 * Income → Past income (DECISIONS.md 254): every income deposit, newest first, by month with each month's total, and what
 * each one counted as. To change one, open it from Home or the Budget tab's transactions.
 */
export default function IncomeHistoryScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [items, setItems] = useState<IncomeItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      paydayApi
        .incomeTransactions()
        .then((x) => {
          setItems(x)
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

  const byMonth = useMemo(() => {
    const groups: { month: string; total: number; items: IncomeItem[] }[] = []
    for (const t of items ?? []) {
      const month = t.date.slice(0, 7)
      const g = groups.at(-1)
      if (g && g.month === month) {
        g.items.push(t)
        g.total += t.cents
      } else groups.push({ month, total: t.cents, items: [t] })
    }
    return groups
  }, [items])

  return (
    <Page>
      <PageHeader title="Past income" back={{ href: '#/income', label: 'Income' }} />
      {!items ? (
        error ? (
          <ErrorNotice message={error} onRetry={() => void load()} />
        ) : (
          <Loading shape="list" />
        )
      ) : byMonth.length === 0 ? (
        <p className="mt-4 rounded-3xl neu px-5 py-4 text-sm text-ink-soft">No income yet.</p>
      ) : (
        <>
          <p className="mt-4 px-1 text-xs text-ink-faint">To change what one counts as, open it from Home or the Budget tab’s transactions.</p>
          {byMonth.map((g) => (
            <section key={g.month} className="mt-5">
              <h2 className="flex items-baseline justify-between px-1 text-sm font-semibold text-ink-soft">
                <span>{longMonth(g.month)}</span>
                <span className="tabular-nums text-good">{formatCents(g.total)}</span>
              </h2>
              <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-3xl neu">
                {g.items.map((t) => (
                  <li key={t.id} className="flex items-center gap-3 px-5 py-3">
                    <span aria-hidden className="size-2.5 shrink-0 rounded-sm" style={{ background: series.find((s) => s.kind === t.kind)!.color }} />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-sm font-medium">{titleCase(t.description)}</p>
                      <p className="truncate text-xs text-ink-faint">
                        {formatDay(t.date)} · {incomeKindLabel(t)}
                        {t.needsReview ? ' · waiting in Tasks' : ''}
                      </p>
                    </div>
                    <span className="shrink-0 text-sm font-semibold tabular-nums text-good">+{formatCents(t.cents)}</span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </>
      )}
    </Page>
  )
}

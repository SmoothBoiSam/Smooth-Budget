import { useCallback, useEffect, useState } from 'react'
import { ApiError, paydayApi, type ExpectedIncome, type IncomeKind, type IncomeMonth, type IncomeSummary } from './api'
import { ask } from './confirm'
import { CardLink, DateField } from './ui'
import { formatCents, formatDay, parseDollars } from './format'

const shortMonth = (iso: string) => new Date(iso.slice(0, 7) + '-01T12:00:00').toLocaleDateString('en-US', { month: 'short' })
const longMonth = (ym: string) => new Date(ym + '-01T12:00:00').toLocaleDateString('en-US', { month: 'long', year: 'numeric' })

/** The three kinds of income, in a fixed order and color (checked for colorblind separation and contrast, DECISIONS.md 254). */
const series: { kind: IncomeKind; key: 'paycheckCents' | 'friendCents' | 'otherCents'; label: string; color: string }[] = [
  { kind: 'paycheck', key: 'paycheckCents', label: 'Paychecks', color: 'var(--color-primary)' },
  { kind: 'friend', key: 'friendCents', label: 'Friends', color: 'var(--color-accent)' },
  { kind: 'other', key: 'otherCents', label: 'Other', color: 'var(--color-chart-other)' },
]
const total = (m: IncomeMonth) => m.paycheckCents + m.friendCents + m.otherCents

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
      <CardLink href="#/transactions?type=income&back=income">Past income</CardLink>
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
          const parts = series.filter((s) => m[s.key] > 0)
          // Where each kind starts and ends, stacked from the bottom.
          const tops = parts.map((_, k) => parts.slice(0, k + 1).reduce((sum, s) => sum + m[s.key], 0))
          return (
            <g key={m.month} opacity={hover === null || hover === i ? 1 : 0.45}>
              {parts.map((s, k) => {
                const y0 = y(k === 0 ? 0 : tops[k - 1])
                const y1 = y(tops[k])
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
      {/* The same numbers as a table, for screen readers. In a div: a table ignores sr-only's 1px width (DECISIONS.md 293). */}
      <div className="sr-only">
      <table>
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
      </div>
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
 * each one counted as. Tap one to change it (DECISIONS.md 258): a paycheck, other income, a friend's money, or not income at
 * all (a refund, a transfer), in the same pop-up as anywhere else.
 */
const fieldCls = 'mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base'

/**
 * Income → Bonus income (DECISIONS.md 256): a bonus or other pay you expect apart from your checks. Until it lands, the
 * budget month it's due in counts it with your paychecks; once a deposit matches it, the deposit counts instead.
 */
export function BonusIncome({ onSignedOut }: { onSignedOut: () => void }) {
  const [items, setItems] = useState<ExpectedIncome[] | null>(null)
  const [adding, setAdding] = useState(false)
  const [name, setName] = useState('')
  const [amount, setAmount] = useState('')
  const [date, setDate] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ good: boolean; text: string } | null>(null)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setMessage({ good: false, text: e instanceof Error ? e.message : 'Something went wrong.' })
    },
    [onSignedOut],
  )
  useEffect(() => {
    paydayApi.expected().then(setItems).catch(fail)
  }, [fail])

  async function save(e: React.FormEvent) {
    e.preventDefault()
    const cents = parseDollars(amount)
    if (!cents) return setMessage({ good: false, text: 'Enter what you expect to take home, after taxes.' })
    if (!date) return setMessage({ good: false, text: 'Pick the day you expect it.' })
    setBusy(true)
    try {
      setItems(await paydayApi.addExpected(name.trim(), cents, date))
      setMessage({ good: true, text: `Added. Your budget now counts ${formatCents(cents)} coming ${formatDay(date)}.` })
      setAdding(false)
      setName('')
      setAmount('')
      setDate('')
    } catch (err) {
      fail(err)
    } finally {
      setBusy(false)
    }
  }

  async function remove(x: ExpectedIncome) {
    const landed = x.landedDate !== null
    if (!(await ask(landed ? `Remove ${x.name}? The deposit stays as other income.` : `Remove ${x.name}? Your budget stops counting it.`, { yes: 'Remove', danger: true }))) return
    try {
      setItems(await paydayApi.removeExpected(x.id))
      setMessage({ good: true, text: `${x.name} removed.` })
    } catch (err) {
      fail(err)
    }
  }

  return (
    <>
      {items && items.length > 0 && (
        <ul className="mb-3 divide-y divide-ink/[0.07] overflow-hidden rounded-3xl neu">
          {items.map((x) => (
            <li key={x.id} className="flex items-center gap-3 px-5 py-3">
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-medium">{x.name}</p>
                <p className={`text-xs ${x.late ? 'text-gold-ink' : x.landedDate ? 'text-good' : 'text-ink-faint'}`}>
                  {x.landedDate
                    ? `Landed ${formatDay(x.landedDate)}: ${formatCents(x.landedCents ?? 0)}`
                    : x.late
                      ? `Expected ${formatDay(x.expectedDate)}, not seen yet. Your budget stopped counting it.`
                      : `Expected ${formatDay(x.expectedDate)} · counted in your budget`}
                </p>
              </div>
              <span className="shrink-0 text-sm font-semibold tabular-nums">{formatCents(x.amountCents)}</span>
              <button type="button" onClick={() => void remove(x)} aria-label={`Remove ${x.name}`} className="shrink-0 px-1 text-lg leading-none text-ink-faint">
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
      {message && (
        <p role="status" className={`mb-3 rounded-2xl neu-inset px-4 py-3 text-sm ${message.good ? 'text-good' : 'text-danger'}`}>
          {message.text}
        </p>
      )}
      {adding ? (
        <form onSubmit={(e) => void save(e)} className="rounded-3xl neu p-5">
          <label className="block">
            <span className="text-sm font-medium text-ink-soft">What you expect to take home</span>
            <input value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" placeholder="$0.00" className={fieldCls} autoFocus />
          </label>
          <p className="mt-1 text-xs text-ink-faint">After taxes: what you think will land in your account.</p>
          {/* A drawn box: an empty phone date input is invisible (DECISIONS.md 261). */}
          <div className="mt-4">
            <span aria-hidden className="text-sm font-medium text-ink-soft">
              When you expect it
            </span>
            <div className="mt-1 flex">
              <DateField label="When you expect it" placeholder="Pick a date" value={date} onChange={setDate} />
            </div>
          </div>
          <label className="mt-4 block">
            <span className="text-sm font-medium text-ink-soft">Name (optional)</span>
            <input value={name} onChange={(e) => setName(e.target.value)} maxLength={100} placeholder="Bonus" className={fieldCls} />
          </label>
          <div className="mt-5 flex gap-3">
            <button type="button" onClick={() => setAdding(false)} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-danger">
              Cancel
            </button>
            <button type="submit" disabled={busy} className="flex-1 rounded-2xl bg-primary py-3 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-50">
              {busy ? 'Adding…' : 'Add'}
            </button>
          </div>
        </form>
      ) : (
        <button
          type="button"
          onClick={() => {
            setAdding(true)
            setMessage(null)
          }}
          className="block w-full rounded-2xl neu py-3 text-center text-sm font-medium"
        >
          Add bonus income
        </button>
      )}
    </>
  )
}

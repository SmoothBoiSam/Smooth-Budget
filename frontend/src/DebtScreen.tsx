import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { ApiError, budgetEditApi, debtApi, savingsShare, type Debt, type DebtStrategy, type DebtSummary } from './api'
import { useResume } from './offline'
import { InterestCard } from './DebtInterest'
import { CardLink, ErrorNotice, Loading, Page, PageHeader, smallBtn } from './ui'
import { formatCents, formatDay } from './format'

const monthFmt = new Intl.DateTimeFormat('en-US', { month: 'short', year: 'numeric', timeZone: 'UTC' })
/** "2030-10-01" -> "Oct 2030". */
const formatMonth = (iso: string) => monthFmt.format(new Date(iso + 'T00:00:00Z'))
const formatApr = (bps: number) => `${(bps / 100).toFixed(2).replace(/\.?0+$/, '')}%`
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

/**
 * The Debt tab (SPEC 4.4): what you owe, the debt-free date under your plan, the payoff chart, Avalanche vs
 * Snowball, the extra each month (the debt side of the one split in Budget settings, DECISIONS.md 321), progress, and
 * each card or loan. The server does the math.
 */
export default function DebtScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [saved, setSaved] = useState<DebtSummary | null>(null)
  const [split, setSplit] = useState<number | null>(null) // savings side of the split
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )

  const load = useCallback(async () => {
    try {
      const s = await debtApi.summary()
      setSaved(s)
      setError(null)
      budgetEditApi.settings().then((b) => setSplit(savingsShare(b)), () => undefined)
    } catch (e) {
      fail(e)
    }
  }, [fail])

  useEffect(() => {
    void load()
  }, [load])
  useResume(load)

  if (!saved) {
    return (
      <Page>
        <PageHeader title="Debt" back={{ href: '#/payday', label: 'Money' }} />
        {error ? <ErrorNotice message={error} onRetry={() => void load()} /> : <Loading />}
      </Page>
    )
  }

  const s = saved
  const plan = s.plan
  const currentStrategy = plan?.strategy ?? 'Avalanche'
  const cmp = s.comparison
  const sameEitherWay = !!cmp && !cmp.neverPaysOff && cmp.interestDifferenceCents === 0 && cmp.monthsDifference === 0
  const visible = s.debts.filter((d) => !d.isHidden)
  const hidden = s.debts.filter((d) => d.isHidden)

  async function saveStrategy(next: DebtStrategy) {
    if (next === currentStrategy) return
    setBusy(true)
    try {
      await debtApi.saveSettings(next)
      setNotice(next === 'Avalanche' ? 'Extra goes to the highest APR first.' : 'Extra goes to the smallest balance first.')
      await load()
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  const owedLines = (
    <>
      {plan ? (
        plan.neverPaysOff ? (
          <p role="alert" className="mt-2 text-sm font-medium text-danger">
            At {formatCents(plan.monthlyPaymentCents)} a month the interest outruns the payments. Raise the extra below.
          </p>
        ) : (
          <p className="mt-2 text-sm text-ink-soft">
            Debt-free by <span className="font-semibold">{formatMonth(plan.debtFreeBy!)}</span>
            <span className="text-ink-soft">
              {' '}
              · {plural(plan.months, 'month')} · {formatCents(plan.totalInterestCents)} in interest
            </span>
          </p>
        )
      ) : (
        s.owedCents > 0 && <p className="mt-2 text-sm text-ink-soft">Add each card’s APR and minimum to see your debt-free date.</p>
      )}
      {s.startingCents > 0 && <Progress summary={s} />}
    </>
  )

  return (
    <Page>
      <PageHeader title="Debt" back={{ href: '#/payday', label: 'Money' }} />

      {notice && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <Alert text={error} />}

      {/* One Owed card (DECISIONS.md 289): the total, when you'll be debt-free, progress, then the chart. */}
      {plan && !plan.neverPaysOff && plan.chart.length > 1 ? (
        <PayoffChart
          past={(s.history ?? []).map((h) => ({ month: h.month, balanceCents: h.owedCents }))}
          points={plan.chart}
          owedCents={s.owedCents}
          limitCents={visible.reduce((sum, d) => sum + (d.isCard ? (d.creditLimitCents ?? 0) : 0), 0)}
        >
          {owedLines}
        </PayoffChart>
      ) : (
        <section aria-label="Owed" className="mt-4 rounded-3xl neu p-5">
          <p className="text-sm font-semibold">Owed</p>
          <p className="text-4xl font-bold tabular-nums tracking-tight">{formatCents(s.owedCents)}</p>
          {owedLines}
          <CardLink href="#/transactions?type=debt&back=debt">Past payments</CardLink>
        </section>
      )}

      {s.needsDetailsCount > 0 && (
        <NeedsDetails
          count={s.needsDetailsCount}
          busy={busy}
          onFromBank={async () => {
            setBusy(true)
            try {
              const r = await debtApi.fromBank()
              setNotice(r.message ?? `Filled in ${plural(r.updated, 'card')} from your bank. Check them below.`)
              await load()
            } catch (e) {
              fail(e)
            } finally {
              setBusy(false)
            }
          }}
        />
      )}

      <InterestCard />

      {plan && (
        <section aria-labelledby="plan-h" className="mt-4 rounded-3xl neu p-4">
          <h2 id="plan-h" className="text-sm font-semibold">
            Payment settings
          </h2>
          {/* No choice to make when both orders come out the same (DECISIONS.md 149). */}
          {!sameEitherWay && (
          <div role="radiogroup" aria-label="Strategy" className="mt-3 grid grid-cols-2 gap-2">
            {(['Avalanche', 'Snowball'] as const).map((st) => (
              <button
                key={st}
                type="button"
                role="radio"
                aria-checked={currentStrategy === st}
                disabled={busy}
                onClick={() => void saveStrategy(st)}
                className={`rounded-xl px-3 py-2 text-left ${currentStrategy === st ? 'bg-accent shadow-neu-sm text-accent-ink' : 'neu'}`}
              >
                <span className="block text-sm font-semibold">{st}</span>
                <span className={`block text-xs ${currentStrategy === st ? 'text-accent-ink/85' : 'text-ink-soft'}`}>
                  {st === 'Avalanche' ? 'Highest APR first · least interest' : 'Smallest balance first · quick wins'}
                </span>
              </button>
            ))}
          </div>
          )}
          {/* One card: no order to compare, so nothing to say (DECISIONS.md 315). */}
          {s.comparison && visible.filter((d) => d.balanceCents > 0).length > 1 && <Comparison plan={plan} cmp={s.comparison} />}

          {/* The extra is the debt side of the one split (DECISIONS.md 321), not a setting of its own. */}
          <p className="mt-5 flex items-baseline justify-between gap-3 text-sm">
            <span className="font-medium">Extra each month</span>
            <span className="font-semibold tabular-nums">{formatCents(plan.extraCents)}</span>
          </p>
          <p className="mt-1 text-xs text-ink-soft">
            {split !== null && <>Your split sends debt {100 - split}% of what’s left after bills, spending and card minimums. </>}
            Minimums {formatCents(plan.minimumsCents)} + extra {formatCents(plan.extraCents)} = <span className="font-medium">{formatCents(plan.monthlyPaymentCents)} a month</span>.
            {plan.firstTarget && plan.extraCents > 0 && ` Extra goes to ${plan.firstTarget} first.`}{' '}
            <a href="#/budget/plan" className="font-medium text-accent">
              Change the split ›
            </a>
          </p>
        </section>
      )}

      <section aria-labelledby="debts-h" className="mt-4 rounded-3xl neu p-5">
        <h2 id="debts-h" className="text-sm font-semibold">
          Accounts
        </h2>
        {visible.length === 0 ? (
          <p className="mt-2 text-xs text-ink-soft">No debts yet. Credit cards show up here once they’re connected or imported.</p>
        ) : (
          <ul className="mt-2 divide-y divide-ink/[0.07]">
            {visible.map((d) => (
              <li key={d.id}>
                <DebtRow d={d} />
              </li>
            ))}
          </ul>
        )}
        {hidden.length > 0 && (
          <p className="mt-3 text-xs text-ink-soft">
            Hidden from the plan:{' '}
            {hidden.map((d, i) => (
              <span key={d.id}>
                {i > 0 && ', '}
                <a href={pageOf(d)} className={`${smallBtn} py-1! text-accent`}>
                  {d.name}
                </a>
              </span>
            ))}
          </p>
        )}
        {/* Cards and loans are added and edited in More → Accounts (DECISIONS.md 326). */}
        <CardLink href="#/more/accounts">Add or change cards and loans</CardLink>
      </section>
    </Page>
  )
}

function Alert({ text }: { text: string }) {
  return (
    <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
      {text}
    </p>
  )
}

function Progress({ summary: s }: { summary: DebtSummary }) {
  const started = s.debts.filter((d) => !d.isHidden).map((d) => d.startedOn).sort()[0]
  const done = s.startingCents > 0 ? Math.min(1, s.paidDownCents / s.startingCents) : 0
  return (
    <div className="mt-4">
      <div className="h-2 overflow-hidden rounded-full neu-inset" aria-hidden>
        <div className="h-full rounded-full bg-good" style={{ width: `${done * 100}%` }} />
      </div>
      <p className="mt-1.5 text-xs text-ink-soft">
        <span className="font-medium text-ink-soft">{formatCents(s.paidDownCents)} paid down</span> since {started ? formatDay(started) : 'you started'} (
        {formatCents(s.startingCents)} then)
      </p>
    </div>
  )
}

function NeedsDetails({ count, busy, onFromBank }: { count: number; busy: boolean; onFromBank: () => void }) {
  return (
    <section className="mt-4 rounded-2xl neu-inset p-4 text-gold-ink">
      <p className="text-sm font-semibold">
        {count === 1 ? '1 card needs' : `${count} cards need`} an APR and minimum payment
      </p>
      <p className="mt-0.5 text-xs">They’re on your statement. Until then they aren’t in the plan below.</p>
      <button type="button" disabled={busy} onClick={onFromBank} className="mt-3 rounded-full bg-surface px-3 py-1.5 text-xs font-semibold text-ink disabled:opacity-50">
        {busy ? 'Asking your bank…' : 'Fill in from my bank'}
      </button>
    </section>
  )
}

function Comparison({ plan, cmp }: { plan: NonNullable<DebtSummary['plan']>; cmp: NonNullable<DebtSummary['comparison']> }) {
  if (plan.neverPaysOff || cmp.neverPaysOff) return null
  const saves = cmp.interestDifferenceCents
  const months = cmp.monthsDifference
  if (saves === 0 && months === 0) return <p className="mt-2 text-xs text-ink-soft">Highest APR or smallest balance first: same result for your debts.</p>
  return (
    <p className="mt-2 text-xs text-ink-soft">
      {saves >= 0 ? (
        <>
          {plan.strategy} saves <span className="font-semibold text-ink-soft">{formatCents(saves)}</span> in interest
        </>
      ) : (
        <>
          {plan.strategy} costs <span className="font-semibold text-ink-soft">{formatCents(-saves)}</span> more interest
        </>
      )}
      {months > 0 ? ` and finishes ${plural(months, 'month')} sooner` : months < 0 ? `, but takes ${plural(-months, 'month')} longer,` : ''} compared with {cmp.strategy}.
    </p>
  )
}

/** A card's account page, or a loan's, in More → Accounts; back comes here (DECISIONS.md 326). */
const pageOf = (d: Debt) => (d.accountId ? `#/more/accounts/${d.accountId}?back=debt` : `#/more/accounts/loan/${d.id}?back=debt`)

function DebtRow({ d }: { d: Debt }) {
  return (
    <a href={pageOf(d)} className="block w-full py-2.5 text-left">
      <div className="flex items-baseline justify-between gap-3">
        <span className="truncate text-sm font-medium">
          {d.name}
          {d.mask && <span className="text-ink-faint"> ••{d.mask}</span>}
        </span>
        <span className="shrink-0 text-sm font-semibold tabular-nums">{formatCents(d.balanceCents)}</span>
      </div>
      {d.needsDetails ? (
        <p className="mt-0.5 text-xs font-medium text-gold-ink">Add the APR and minimum ›</p>
      ) : d.balanceCents === 0 ? (
        <p className="mt-0.5 text-xs text-good">Paid off</p>
      ) : (
        <p className="mt-0.5 text-xs text-ink-soft">
          {d.aprBps !== null && `${formatApr(d.aprBps)} APR`}
          {d.minimumCents !== null && ` · ${formatCents(d.minimumCents)} min`}
          {d.nextDueDate && ` · due ${formatDay(d.nextDueDate)}${d.dueDay === 31 ? " (last day)" : ""}`}
          {d.paidOffBy && <span className="text-ink-soft"> · paid off {formatMonth(d.paidOffBy)}</span>}
        </p>
      )}
      {d.utilizationPercent !== null && d.isCard && (
        <div className="mt-2 flex items-center gap-2">
          <div className="h-1.5 flex-1 overflow-hidden rounded-full neu-inset" aria-hidden>
            <div
              className={`h-full rounded-full ${d.utilizationPercent > 30 ? 'bg-gold' : 'bg-good'}`}
              style={{ width: `${Math.min(100, d.utilizationPercent)}%` }}
            />
          </div>
          <span className="shrink-0 text-[11px] tabular-nums text-ink-soft">{d.utilizationPercent}% of limit used</span>
        </div>
      )}
      {/* Interest charges you filed (or the app found) on this card or loan (DECISIONS.md 232). */}
      {d.lastInterestCents !== null && d.lastInterestDate && (
        <p className="mt-1.5 text-xs text-ink-soft tabular-nums">
          Interest: {formatCents(d.lastInterestCents)} on {formatDay(d.lastInterestDate)} · {formatCents(d.interestChargedThisYearCents)} this year
        </p>
      )}
    </a>
  )
}

/**
 * Total balance over time under the plan: one series (the title names it), a 2px line over a light fill,
 * recessive gridlines, and a crosshair tooltip on hover or touch.
 */
/** What you owed over the last year (solid) running into your plan from here (dashed), under your cards' total credit limit (DECISIONS.md 320). */
function PayoffChart({
  past,
  points: plan,
  owedCents,
  limitCents,
  children,
}: {
  past: { month: string; balanceCents: number }[]
  points: { month: string; balanceCents: number }[]
  owedCents: number
  /** The visible cards' limits added up; 0 when none is known. */
  limitCents: number
  children: ReactNode
}) {
  const [hover, setHover] = useState<number | null>(null)
  const points = [...past, ...plan]
  const now = past.length
  const W = 340
  const H = 150
  const pad = { l: 44, r: 8, t: 10, b: 22 }
  const max = Math.max(...points.map((p) => p.balanceCents), limitCents, 1)
  const top = niceCeil(max)
  const x = (i: number) => pad.l + (i / (points.length - 1)) * (W - pad.l - pad.r)
  const y = (c: number) => pad.t + (1 - c / top) * (H - pad.t - pad.b)
  const path = (from: number, to: number) =>
    points.slice(from, to + 1).map((p, k) => `${k === 0 ? 'M' : 'L'}${x(from + k).toFixed(1)},${y(p.balanceCents).toFixed(1)}`).join('')
  const line = path(now, points.length - 1)
  const pastLine = now > 0 ? path(0, now) : ''
  const area = `${line}L${x(points.length - 1).toFixed(1)},${y(0)}L${x(now)},${y(0)}Z`
  const yearTicks = points.map((p, i) => ({ p, i })).filter(({ p, i }) => i > 0 && p.month.endsWith('-01-01'))
  const ticks = yearTicks.length > 6 ? yearTicks.filter((_, k) => k % Math.ceil(yearTicks.length / 6) === 0) : yearTicks
  const h = hover === null ? null : points[hover]

  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    const box = e.currentTarget.getBoundingClientRect()
    const px = ((e.clientX - box.left) / box.width) * W
    const i = Math.round(((px - pad.l) / (W - pad.l - pad.r)) * (points.length - 1))
    setHover(Math.max(0, Math.min(points.length - 1, i)))
  }

  return (
    <figure className="mt-4 rounded-3xl neu p-4">
      <figcaption>
        <span className="flex items-baseline justify-between gap-3">
          <span className="text-sm font-semibold">Owed</span>
          <span className="text-xs tabular-nums text-ink-soft" aria-live="polite">
            {h ? `${formatMonth(h.month)}: ${formatCents(h.balanceCents)}` : ''}
          </span>
        </span>
        <span className="block text-4xl font-bold tabular-nums tracking-tight">{formatCents(owedCents)}</span>
        {children}
      </figcaption>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        className="mt-2 w-full touch-none select-none"
        role="img"
        aria-label={`Total owed falls from ${formatCents(points[0].balanceCents)} to $0 by ${formatMonth(points[points.length - 1].month)}.${limitCents > 0 ? ` Credit limit ${formatCents(limitCents)}.` : ''}`}
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
        {limitCents > 0 && (
          <g>
            <line x1={pad.l} x2={W - pad.r} y1={y(limitCents)} y2={y(limitCents)} className="stroke-danger" strokeWidth={1.5} strokeDasharray="6 4" />
            <text x={W - pad.r} y={y(limitCents) - 4} textAnchor="end" className="fill-slate-500 text-[10px] tabular-nums">
              Limit {shortMoney(limitCents)}
            </text>
          </g>
        )}
        <path d={area} className="fill-sky-700" fillOpacity={0.12} />
        {pastLine && <path d={pastLine} className="stroke-slate-500" strokeWidth={2} fill="none" strokeLinejoin="round" strokeLinecap="round" />}
        <path d={line} className="stroke-sky-700" strokeWidth={2} strokeDasharray={past.length > 0 ? '5 4' : undefined} fill="none" strokeLinejoin="round" strokeLinecap="round" />
        {past.length > 0 && (
          <g>
            <line x1={x(now)} x2={x(now)} y1={pad.t} y2={y(0)} className="stroke-slate-300" strokeWidth={1} />
            <text x={x(now) + 3} y={pad.t + 8} className="fill-slate-500 text-[10px]">Now</text>
          </g>
        )}
        {ticks.map(({ p, i }) => (
          <text key={p.month} x={x(i)} y={H - 6} textAnchor="middle" className="fill-slate-500 text-[10px]">
            {p.month.slice(0, 4)}
          </text>
        ))}
        {h && hover !== null && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={pad.t} y2={y(0)} className="stroke-slate-400" strokeWidth={1} strokeDasharray="3 3" />
            <circle cx={x(hover)} cy={y(h.balanceCents)} r={4} className="fill-sky-700 stroke-white" strokeWidth={2} />
          </g>
        )}
      </svg>
      {/* Past payments on their own page, like interest's (DECISIONS.md 240). */}
      <CardLink href="#/transactions?type=debt&back=debt">Past payments</CardLink>
    </figure>
  )
}

/** A round axis top at or above the max: 1, 1.5, 2, 2.5, 3, 4, 5, 6 or 8 × a power of ten. */
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

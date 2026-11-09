import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, debtApi, type Debt, type DebtFields, type DebtStrategy, type DebtSummary } from './api'
import { useRemembered } from './offline'
import { Caret, ErrorNotice, Loading, Page, PageHeader, smallBtn } from './ui'
import { centsToInput, formatCents, formatDay, ordinal, parseDollars } from './format'

const monthFmt = new Intl.DateTimeFormat('en-US', { month: 'short', year: 'numeric', timeZone: 'UTC' })
/** "2030-10-01" -> "Oct 2030". */
const formatMonth = (iso: string) => monthFmt.format(new Date(iso + 'T00:00:00Z'))
const formatApr = (bps: number) => `${(bps / 100).toFixed(2).replace(/\.?0+$/, '')}%`
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

/**
 * The Debt tab (SPEC 4.4): what you owe, the debt-free date under your plan, the payoff chart, Avalanche vs
 * Snowball, an "extra each month" what-if slider, progress, and each card or loan. The server does the math;
 * the slider asks it again (debounced) for every value.
 */
export default function DebtScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [saved, setSaved] = useState<DebtSummary | null>(null)
  const [preview, setPreview] = useState<DebtSummary | null>(null)
  const [extra, setExtra] = useState<number | null>(null) // the slider; null = untouched
  const [strategy, setStrategy] = useState<DebtStrategy | null>(null)
  const [editing, setEditing] = useState<Debt | null | undefined>(undefined) // null = adding
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const timer = useRef<number | undefined>(undefined)

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
      setPreview(null)
      setExtra(null)
      setStrategy(null)
      setError(null)
    } catch (e) {
      fail(e)
    }
  }, [fail])

  useEffect(() => {
    void load()
  }, [load])

  /** What-if: ask the server for the plan with this extra and strategy, a moment after the slider stops. */
  function whatIf(nextExtra: number, nextStrategy: DebtStrategy) {
    setExtra(nextExtra)
    setStrategy(nextStrategy)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => {
      debtApi.summary({ extraCents: nextExtra, strategy: nextStrategy }).then(setPreview).catch(fail)
    }, 200)
  }

  if (!saved) {
    return (
      <Page>
        <PageHeader title="Debt" back={{ href: '#/payday', label: 'Plan' }} />
        {error ? <ErrorNotice message={error} onRetry={() => void load()} /> : <Loading />}
      </Page>
    )
  }

  const s = preview ?? saved
  const plan = s.plan
  const savedPlan = saved.plan
  const currentExtra = extra ?? savedPlan?.extraCents ?? 0
  const currentStrategy = strategy ?? savedPlan?.strategy ?? 'Avalanche'
  const cmp = s.comparison
  const sameEitherWay = !!cmp && !cmp.neverPaysOff && cmp.interestDifferenceCents === 0 && cmp.monthsDifference === 0
  const changed = savedPlan !== null && (currentExtra !== savedPlan.extraCents || currentStrategy !== savedPlan.strategy)
  const sliderMax = Math.max(1_000_00, Math.ceil(((savedPlan?.extraCents ?? 0) * 2) / 100_00) * 100_00)
  const visible = s.debts.filter((d) => !d.isHidden)
  const hidden = s.debts.filter((d) => d.isHidden)

  async function savePlan(extraCents: number | null) {
    setBusy(true)
    try {
      await debtApi.saveSettings(currentStrategy, extraCents)
      setNotice(extraCents === null ? 'Extra is now whatever your budget has left over each month.' : 'Saved. Your budget’s card payment now follows this plan.')
      await load()
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Page>
      <PageHeader title="Debt" back={{ href: '#/payday', label: 'Plan' }} />

      {notice && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <Alert text={error} />}

      <section aria-label="Summary" className="mt-4 rounded-3xl neu p-5">
        <p className="text-sm text-ink-soft">You owe</p>
        <p className="text-4xl font-bold tabular-nums tracking-tight">{formatCents(s.owedCents)}</p>
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
      </section>

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

      {plan && !plan.neverPaysOff && plan.chart.length > 1 && (
        <PayoffChart past={(s.history ?? []).map((h) => ({ month: h.month, balanceCents: h.owedCents }))} points={plan.chart} />
      )}
      {s.history && s.history.length > 0 && <PastPayments history={s.history} />}

      {plan && (
        <section aria-labelledby="plan-h" className="mt-4 rounded-3xl neu p-4">
          <h2 id="plan-h" className="text-sm font-semibold">
            Your plan
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
                onClick={() => whatIf(currentExtra, st)}
                className={`rounded-xl px-3 py-2 text-left ${currentStrategy === st ? 'bg-primary shadow-neu-sm text-primary-ink' : 'neu'}`}
              >
                <span className="block text-sm font-semibold">{st}</span>
                <span className={`block text-xs ${currentStrategy === st ? 'text-primary-ink/85' : 'text-ink-soft'}`}>
                  {st === 'Avalanche' ? 'Highest APR first · least interest' : 'Smallest balance first · quick wins'}
                </span>
              </button>
            ))}
          </div>
          )}
          {s.comparison && <Comparison plan={plan} cmp={s.comparison} />}

          <label className="mt-5 block">
            <span className="flex items-baseline justify-between gap-3 text-sm">
              <span className="font-medium">Extra each month</span>
              <span className="font-semibold tabular-nums">{formatCents(currentExtra)}</span>
            </span>
            <input
              type="range"
              min={0}
              max={sliderMax}
              step={10_00}
              value={currentExtra}
              onChange={(e) => whatIf(Number(e.target.value), currentStrategy)}
              aria-valuetext={`${formatCents(currentExtra)} extra a month`}
              className="mt-2 w-full accent-primary"
            />
          </label>
          <p className="mt-1 text-xs text-ink-soft">
            Minimums {formatCents(plan.minimumsCents)} + extra {formatCents(currentExtra)} = <span className="font-medium">{formatCents(plan.monthlyPaymentCents)} a month</span>.
            {plan.firstTarget && ` Extra goes to ${plan.firstTarget} first.`}
            {!changed && savedPlan?.extraSource === 'budget' && ' Now: whatever your budget has left over.'}
            {!changed && savedPlan?.extraSource === 'you' && ' Your budget plans this each month.'}
          </p>
          {changed ? (
            <div className="mt-3 flex gap-2">
              <button type="button" onClick={() => void load()} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-ink-soft">
                Reset
              </button>
              <button type="button" disabled={busy} onClick={() => void savePlan(currentExtra)} className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 text-sm font-semibold text-primary-ink disabled:opacity-50">
                Save as my plan
              </button>
            </div>
          ) : (
            savedPlan?.extraSource === 'you' && (
              <button type="button" disabled={busy} onClick={() => void savePlan(null)} className={`${smallBtn} mt-2 text-ink`}>
                Use whatever’s left over each month instead
              </button>
            )
          )}
        </section>
      )}

      <section aria-labelledby="debts-h" className="mt-6">
        <h2 id="debts-h" className="px-1 text-sm font-semibold text-ink-soft">
          Cards and loans
        </h2>
        {visible.length === 0 ? (
          <p className="mt-2 rounded-2xl neu px-4 py-3 text-sm text-ink-soft">
            No debts yet. Credit cards show up here once they’re connected or imported.
          </p>
        ) : (
          <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
            {visible.map((d) => (
              <li key={d.id}>
                <DebtRow d={d} onOpen={() => setEditing(d)} />
              </li>
            ))}
          </ul>
        )}
        <button type="button" onClick={() => setEditing(null)} className="mt-3 w-full rounded-2xl neu py-3 text-sm font-medium">
          + Add a loan or other debt
        </button>
        {hidden.length > 0 && (
          <p className="mt-3 text-xs text-ink-soft">
            Hidden from the plan:{' '}
            {hidden.map((d, i) => (
              <span key={d.id}>
                {i > 0 && ', '}
                <button type="button" onClick={() => setEditing(d)} className={`${smallBtn} py-1! text-ink`}>
                  {d.name}
                </button>
              </span>
            ))}
          </p>
        )}
      </section>

      {editing !== undefined && (
        <DebtSheet
          debt={editing}
          onClose={() => setEditing(undefined)}
          onSaved={async (message) => {
            setEditing(undefined)
            setNotice(message)
            await load()
          }}
          onError={fail}
        />
      )}
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

function DebtRow({ d, onOpen }: { d: Debt; onOpen: () => void }) {
  return (
    <button type="button" onClick={onOpen} className="block w-full px-4 py-3 text-left">
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
    </button>
  )
}

/**
 * Total balance over time under the plan: one series (the title names it), a 2px line over a light fill,
 * recessive gridlines, and a crosshair tooltip on hover or touch.
 */
/** The last year of payments, newest first (DECISIONS.md 141). */
function PastPayments({ history }: { history: { month: string; owedCents: number; paidCents: number }[] }) {
  const [open, setOpen] = useRemembered('debt.past', false)
  const paid = history.reduce((sum, h) => sum + h.paidCents, 0)
  return (
    <section className="mt-4 rounded-3xl neu p-4">
      <h2>
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex w-full items-baseline justify-between gap-3 text-left">
          <span className="flex items-center gap-1 text-sm font-semibold">
            <Caret open={open} />
            Past payments
          </span>
          <span className="text-xs tabular-nums text-ink-soft">{formatCents(paid)} paid in {history.length} months</span>
        </button>
      </h2>
      {open && (
        <table className="mt-2 w-full text-sm tabular-nums">
          <thead>
            <tr className="text-xs text-ink-soft">
              <th className="py-1 text-left font-normal">Month</th>
              <th className="py-1 text-right font-normal">Paid</th>
              <th className="py-1 text-right font-normal">Owed at the end</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-ink/[0.07]">
            {[...history].reverse().map((h) => (
              <tr key={h.month}>
                <td className="py-1.5">{formatMonth(h.month)}</td>
                <td className="py-1.5 text-right">{formatCents(h.paidCents)}</td>
                <td className="py-1.5 text-right">{formatCents(h.owedCents)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}

/** What you owed over the last year (solid) running into your plan from here (dashed). */
function PayoffChart({ past, points: plan }: { past: { month: string; balanceCents: number }[]; points: { month: string; balanceCents: number }[] }) {
  const [hover, setHover] = useState<number | null>(null)
  const points = [...past, ...plan]
  const now = past.length
  const W = 340
  const H = 150
  const pad = { l: 44, r: 8, t: 10, b: 22 }
  const max = Math.max(...points.map((p) => p.balanceCents), 1)
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
      <figcaption className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-semibold">{past.length > 0 ? 'Owed: the last year, then your plan' : 'Total owed under your plan'}</span>
        <span className="text-xs tabular-nums text-ink-soft" aria-live="polite">
          {h ? `${formatMonth(h.month)}: ${formatCents(h.balanceCents)}` : ''}
        </span>
      </figcaption>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        className="mt-2 w-full touch-none select-none"
        role="img"
        aria-label={`Total owed falls from ${formatCents(points[0].balanceCents)} to $0 by ${formatMonth(points[points.length - 1].month)}.`}
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
      <details className="mt-1 text-xs text-ink-soft">
        <summary className="cursor-pointer">Month by month</summary>
        <table className="mt-2 w-full tabular-nums">
          <tbody>
            {points
              .filter((_, i) => i % 3 === 0 || i === points.length - 1)
              .map((p) => (
                <tr key={p.month}>
                  <td className="py-0.5">{formatMonth(p.month)}</td>
                  <td className="py-0.5 text-right">{formatCents(p.balanceCents)}</td>
                </tr>
              ))}
          </tbody>
        </table>
      </details>
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

/** Edit a card or loan: APR, minimum, due day, limit, and (for loans and CSV-only cards) the balance. */
function DebtSheet({
  debt,
  onClose,
  onSaved,
  onError,
}: {
  debt: Debt | null
  onClose: () => void
  onSaved: (message: string) => Promise<void>
  onError: (e: unknown) => void
}) {
  const isNew = debt === null
  const [name, setName] = useState(debt?.name ?? '')
  const [balance, setBalance] = useState(debt ? centsToInput(debt.balanceCents) : '')
  const [apr, setApr] = useState(debt?.aprBps != null ? String(debt.aprBps / 100) : '')
  const [minimum, setMinimum] = useState(debt?.minimumCents != null ? centsToInput(debt.minimumCents) : '')
  const [dueDay, setDueDay] = useState(debt?.dueDay != null ? String(debt.dueDay) : '')
  const [limit, setLimit] = useState(debt?.creditLimitCents != null ? centsToInput(debt.creditLimitCents) : '')
  const [starting, setStarting] = useState(debt && debt.startingBalanceCents > 0 ? centsToInput(debt.startingBalanceCents) : '')
  const [hidden, setHidden] = useState(debt?.isHidden ?? false)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const balanceEditable = !debt?.balanceFromBank

  function fields(): DebtFields | string {
    const aprNum = apr.trim() === '' ? null : Number(apr.replace('%', ''))
    if (aprNum !== null && (!Number.isFinite(aprNum) || aprNum < 0 || aprNum > 100)) return 'Enter the APR as a percentage, like 24.99.'
    const min = minimum.trim() === '' ? null : parseDollars(minimum)
    if (minimum.trim() !== '' && min === null) return 'Enter the minimum payment in dollars, like 35 or 35.00.'
    const bal = balanceEditable ? parseDollars(balance) : null
    if (balanceEditable && bal === null) return 'Enter what you owe in dollars.'
    const day = dueDay.trim() === '' ? null : Number(dueDay)
    if (day !== null && (!Number.isInteger(day) || day < 1 || day > 31)) return 'The due day is a day of the month, 1 to 31.'
    const lim = limit.trim() === '' ? null : parseDollars(limit)
    if (limit.trim() !== '' && lim === null) return 'Enter the limit in dollars.'
    const start = starting.trim() === '' ? null : parseDollars(starting)
    if (starting.trim() !== '' && start === null) return 'Enter the starting balance in dollars.'
    if (!name.trim()) return 'Give it a name.'
    return {
      name: name.trim(),
      balanceCents: bal,
      aprBps: aprNum === null ? null : Math.round(aprNum * 100),
      minimumCents: min,
      dueDay: day,
      creditLimitCents: lim,
      startingBalanceCents: start,
      isHidden: hidden,
    }
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    const f = fields()
    if (typeof f === 'string') return setErr(f)
    setBusy(true)
    try {
      if (isNew) await debtApi.add(f)
      else await debtApi.update(debt.id, f)
      await onSaved(isNew ? `Added ${f.name}.` : 'Saved.')
    } catch (ex) {
      onError(ex)
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="debt-sheet-h"
        onSubmit={save}
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="debt-sheet-h" className="text-lg font-semibold">
          {isNew ? 'Add a debt' : debt.name}
        </h2>
        {!isNew && debt.fromBank && <p className="text-xs text-ink-soft">APR and minimum came from your bank. Change them if they’re off.</p>}

        <Field label="Name">
          <input value={name} onChange={(e) => setName(e.target.value)} className={inputCls} placeholder="e.g. Car loan" />
        </Field>
        {balanceEditable ? (
          <Field label="What you owe now">
            <Money value={balance} onChange={setBalance} label="Balance" />
          </Field>
        ) : (
          <p className="mt-4 text-sm text-ink-soft">
            Owed now: <span className="font-semibold">{formatCents(debt!.balanceCents)}</span> <span className="text-xs text-ink-faint">(from your bank)</span>
          </p>
        )}
        <div className="grid grid-cols-2 gap-3">
          <Field label="APR">
            <div className={`${inputCls} flex items-center`}>
              <input value={apr} onChange={(e) => setApr(e.target.value)} inputMode="decimal" aria-label="APR percent" placeholder="24.99" className="w-full bg-transparent text-right outline-none" />
              <span className="pl-1 text-sm text-ink-soft">%</span>
            </div>
          </Field>
          <Field label="Minimum payment">
            <Money value={minimum} onChange={setMinimum} label="Minimum payment" />
          </Field>
          <Field label="Due day of the month">
            <select aria-label="Due day of the month" value={dueDay} onChange={(e) => setDueDay(e.target.value)} className={inputCls}>
              <option value="">Not set</option>
              {Array.from({ length: 30 }, (_, i) => i + 1).map((d) => (
                <option key={d} value={String(d)}>
                  {ordinal(d)}
                </option>
              ))}
              {/* Stored as 31: the 31st where there is one, otherwise the 30th, 29th or 28th. */}
              <option value="31">Last day of the month</option>
            </select>
          </Field>
          <Field label={debt?.isCard ? 'Credit limit' : 'Original amount'}>
            <Money value={limit} onChange={setLimit} label="Limit" />
          </Field>
        </div>
        <Field label="What you owed when you started tracking">
          <Money value={starting} onChange={setStarting} label="Starting balance" />
        </Field>
        {!isNew && (
          <label className="mt-4 flex items-center gap-3 rounded-xl neu-inset px-4 py-3">
            <input type="checkbox" checked={hidden} onChange={(e) => setHidden(e.target.checked)} className="size-5" />
            <span className="text-sm">
              Leave out of the plan
              <span className="block text-xs text-ink-soft">For a card you pay in full every month.</span>
            </span>
          </label>
        )}

        {err && (
          <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {err}
          </p>
        )}
        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-2xl neu py-3 font-semibold text-danger text-sm">
            Cancel
          </button>
          <button type="submit" disabled={busy} className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-50 text-sm">
            {busy ? 'Saving…' : 'Save'}
          </button>
        </div>
        {!isNew && !debt.isCard && (
          <button
            type="button"
            onClick={async () => {
              if (!window.confirm(`Remove ${debt.name}? Its details are deleted.`)) return
              try {
                await debtApi.remove(debt.id)
                await onSaved(`Removed ${debt.name}.`)
              } catch (ex) {
                onError(ex)
              }
            }}
            className="mt-3 w-full py-2 text-sm font-medium text-danger"
          >
            Remove this debt
          </button>
        )}
      </form>
    </div>
  )
}

const inputCls = 'mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base'

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="mt-4 block">
      <span className="text-sm font-medium text-ink-soft">{label}</span>
      {children}
    </label>
  )
}

function Money({ value, onChange, label }: { value: string; onChange: (v: string) => void; label: string }) {
  return (
    <div className={`${inputCls} flex items-center`}>
      <span className="text-sm text-ink-soft">$</span>
      <input aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} inputMode="decimal" className="w-full bg-transparent px-1 text-right tabular-nums outline-none" />
    </div>
  )
}

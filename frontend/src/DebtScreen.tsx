import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, debtApi, type Debt, type DebtFields, type DebtStrategy, type DebtSummary } from './api'
import { ErrorNotice, Loading } from './ui'
import { centsToInput, formatCents, formatDay, parseDollars } from './format'

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
        <h1 className="text-2xl font-bold tracking-tight">Debt</h1>
        {error ? <ErrorNotice message={error} onRetry={() => void load()} /> : <Loading />}
      </Page>
    )
  }

  const s = preview ?? saved
  const plan = s.plan
  const savedPlan = saved.plan
  const currentExtra = extra ?? savedPlan?.extraCents ?? 0
  const currentStrategy = strategy ?? savedPlan?.strategy ?? 'Avalanche'
  const changed = savedPlan !== null && (currentExtra !== savedPlan.extraCents || currentStrategy !== savedPlan.strategy)
  const sliderMax = Math.max(1_000_00, Math.ceil(((savedPlan?.extraCents ?? 0) * 2) / 100_00) * 100_00)
  const visible = s.debts.filter((d) => !d.isHidden)
  const hidden = s.debts.filter((d) => d.isHidden)

  async function savePlan(extraCents: number | null) {
    setBusy(true)
    try {
      await debtApi.saveSettings(currentStrategy, extraCents)
      setNotice(extraCents === null ? 'Extra now follows your budget’s card payments.' : 'Saved as your plan.')
      await load()
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Page>
      <h1 className="text-2xl font-bold tracking-tight">Debt</h1>

      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && <Alert text={error} />}

      <section aria-label="Summary" className="mt-4 rounded-2xl bg-white p-5 ring-1 ring-slate-200">
        <p className="text-sm text-slate-500">You owe</p>
        <p className="text-4xl font-bold tabular-nums tracking-tight">{formatCents(s.owedCents)}</p>
        {plan ? (
          plan.neverPaysOff ? (
            <p role="alert" className="mt-2 text-sm font-medium text-red-600">
              At {formatCents(plan.monthlyPaymentCents)} a month the interest outruns the payments. Raise the extra below.
            </p>
          ) : (
            <p className="mt-2 text-sm text-slate-700">
              Debt-free by <span className="font-semibold">{formatMonth(plan.debtFreeBy!)}</span>
              <span className="text-slate-500">
                {' '}
                · {plural(plan.months, 'month')} · {formatCents(plan.totalInterestCents)} in interest
              </span>
            </p>
          )
        ) : (
          s.owedCents > 0 && <p className="mt-2 text-sm text-slate-500">Add each card’s APR and minimum to see your debt-free date.</p>
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

      {plan && !plan.neverPaysOff && plan.chart.length > 1 && <PayoffChart points={plan.chart} />}

      {plan && (
        <section aria-labelledby="plan-h" className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
          <h2 id="plan-h" className="text-sm font-semibold">
            Your plan
          </h2>
          <div role="radiogroup" aria-label="Strategy" className="mt-3 grid grid-cols-2 gap-2">
            {(['Avalanche', 'Snowball'] as const).map((st) => (
              <button
                key={st}
                type="button"
                role="radio"
                aria-checked={currentStrategy === st}
                onClick={() => whatIf(currentExtra, st)}
                className={`rounded-xl px-3 py-2 text-left ring-1 ${currentStrategy === st ? 'bg-slate-900 text-white ring-slate-900' : 'bg-white ring-slate-200'}`}
              >
                <span className="block text-sm font-semibold">{st}</span>
                <span className={`block text-xs ${currentStrategy === st ? 'text-slate-300' : 'text-slate-500'}`}>
                  {st === 'Avalanche' ? 'Highest APR first · least interest' : 'Smallest balance first · quick wins'}
                </span>
              </button>
            ))}
          </div>
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
              className="mt-2 w-full accent-slate-900"
            />
          </label>
          <p className="mt-1 text-xs text-slate-500">
            Minimums {formatCents(plan.minimumsCents)} + extra {formatCents(currentExtra)} = <span className="font-medium">{formatCents(plan.monthlyPaymentCents)} a month</span>.
            {plan.firstTarget && ` Extra goes to ${plan.firstTarget} first.`}
            {!changed && savedPlan?.extraSource === 'budget' && ' The extra comes from your budget’s card payments minus the minimums.'}
          </p>
          {changed ? (
            <div className="mt-3 flex gap-2">
              <button type="button" onClick={() => void load()} className="flex-1 rounded-xl bg-slate-100 py-2.5 text-sm font-semibold text-slate-700">
                Reset
              </button>
              <button type="button" disabled={busy} onClick={() => void savePlan(currentExtra)} className="flex-1 rounded-xl bg-slate-900 py-2.5 text-sm font-semibold text-white disabled:opacity-50">
                Save as my plan
              </button>
            </div>
          ) : (
            savedPlan?.extraSource === 'you' && (
              <button type="button" disabled={busy} onClick={() => void savePlan(null)} className="mt-2 text-xs font-medium text-slate-600 underline">
                Follow my budget’s card payments instead
              </button>
            )
          )}
        </section>
      )}

      <section aria-labelledby="debts-h" className="mt-6">
        <h2 id="debts-h" className="text-sm font-semibold text-slate-700">
          Cards and loans
        </h2>
        {visible.length === 0 ? (
          <p className="mt-2 rounded-xl bg-white px-4 py-3 text-sm text-slate-500 ring-1 ring-slate-200">
            No debts yet. Credit cards show up here once they’re connected or imported.
          </p>
        ) : (
          <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
            {visible.map((d) => (
              <li key={d.id}>
                <DebtRow d={d} onOpen={() => setEditing(d)} />
              </li>
            ))}
          </ul>
        )}
        <button type="button" onClick={() => setEditing(null)} className="mt-3 w-full rounded-xl bg-white py-3 text-sm font-medium ring-1 ring-slate-200">
          + Add a loan or other debt
        </button>
        {hidden.length > 0 && (
          <p className="mt-3 text-xs text-slate-500">
            Hidden from the plan:{' '}
            {hidden.map((d, i) => (
              <span key={d.id}>
                {i > 0 && ', '}
                <button type="button" onClick={() => setEditing(d)} className="underline">
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

function Page({ children }: { children: React.ReactNode }) {
  return <div className="mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">{children}</div>
}

function Alert({ text }: { text: string }) {
  return (
    <p role="alert" className="mt-4 rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">
      {text}
    </p>
  )
}

function Progress({ summary: s }: { summary: DebtSummary }) {
  const started = s.debts.filter((d) => !d.isHidden).map((d) => d.startedOn).sort()[0]
  const done = s.startingCents > 0 ? Math.min(1, s.paidDownCents / s.startingCents) : 0
  return (
    <div className="mt-4">
      <div className="h-2 overflow-hidden rounded-full bg-slate-100" aria-hidden>
        <div className="h-full rounded-full bg-emerald-600" style={{ width: `${done * 100}%` }} />
      </div>
      <p className="mt-1.5 text-xs text-slate-500">
        <span className="font-medium text-slate-700">{formatCents(s.paidDownCents)} paid down</span> since {started ? formatDay(started) : 'you started'} (
        {formatCents(s.startingCents)} then)
      </p>
    </div>
  )
}

function NeedsDetails({ count, busy, onFromBank }: { count: number; busy: boolean; onFromBank: () => void }) {
  return (
    <section className="mt-4 rounded-2xl bg-amber-50 p-4 text-amber-900 ring-1 ring-amber-200">
      <p className="text-sm font-semibold">
        {count === 1 ? '1 card needs' : `${count} cards need`} an APR and minimum payment
      </p>
      <p className="mt-0.5 text-xs">They’re on your statement. Until then they aren’t in the plan below.</p>
      <button type="button" disabled={busy} onClick={onFromBank} className="mt-3 rounded-full bg-white px-3 py-1.5 text-xs font-semibold text-slate-900 ring-1 ring-amber-200 disabled:opacity-50">
        {busy ? 'Asking your bank…' : 'Fill in from my bank'}
      </button>
    </section>
  )
}

function Comparison({ plan, cmp }: { plan: NonNullable<DebtSummary['plan']>; cmp: NonNullable<DebtSummary['comparison']> }) {
  if (plan.neverPaysOff || cmp.neverPaysOff) return null
  const saves = cmp.interestDifferenceCents
  const months = cmp.monthsDifference
  if (saves === 0 && months === 0) return <p className="mt-2 text-xs text-slate-500">Both strategies come out the same for your debts.</p>
  return (
    <p className="mt-2 text-xs text-slate-500">
      {saves >= 0 ? (
        <>
          {plan.strategy} saves <span className="font-semibold text-slate-700">{formatCents(saves)}</span> in interest
        </>
      ) : (
        <>
          {plan.strategy} costs <span className="font-semibold text-slate-700">{formatCents(-saves)}</span> more interest
        </>
      )}
      {months > 0 ? ` and finishes ${plural(months, 'month')} sooner` : months < 0 ? `, but takes ${plural(-months, 'month')} longer,` : ''} compared with {cmp.strategy}.
    </p>
  )
}

function DebtRow({ d, onOpen }: { d: Debt; onOpen: () => void }) {
  return (
    <button type="button" onClick={onOpen} className="block w-full px-4 py-3 text-left hover:bg-slate-50">
      <div className="flex items-baseline justify-between gap-3">
        <span className="truncate text-sm font-medium">
          {d.name}
          {d.mask && <span className="text-slate-400"> ••{d.mask}</span>}
        </span>
        <span className="shrink-0 text-sm font-semibold tabular-nums">{formatCents(d.balanceCents)}</span>
      </div>
      {d.needsDetails ? (
        <p className="mt-0.5 text-xs font-medium text-amber-600">Add the APR and minimum ›</p>
      ) : d.balanceCents === 0 ? (
        <p className="mt-0.5 text-xs text-emerald-700">Paid off</p>
      ) : (
        <p className="mt-0.5 text-xs text-slate-500">
          {d.aprBps !== null && `${formatApr(d.aprBps)} APR`}
          {d.minimumCents !== null && ` · ${formatCents(d.minimumCents)} min`}
          {d.nextDueDate && ` · due ${formatDay(d.nextDueDate)}`}
          {d.paidOffBy && <span className="text-slate-700"> · paid off {formatMonth(d.paidOffBy)}</span>}
        </p>
      )}
      {d.utilizationPercent !== null && d.isCard && (
        <div className="mt-2 flex items-center gap-2">
          <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-slate-100" aria-hidden>
            <div
              className={`h-full rounded-full ${d.utilizationPercent > 30 ? 'bg-amber-500' : 'bg-emerald-600'}`}
              style={{ width: `${Math.min(100, d.utilizationPercent)}%` }}
            />
          </div>
          <span className="shrink-0 text-[11px] tabular-nums text-slate-500">{d.utilizationPercent}% of limit used</span>
        </div>
      )}
    </button>
  )
}

/**
 * Total balance over time under the plan: one series (the title names it), a 2px line over a light fill,
 * recessive gridlines, and a crosshair tooltip on hover or touch.
 */
function PayoffChart({ points }: { points: { month: string; balanceCents: number }[] }) {
  const [hover, setHover] = useState<number | null>(null)
  const W = 340
  const H = 150
  const pad = { l: 44, r: 8, t: 10, b: 22 }
  const max = Math.max(...points.map((p) => p.balanceCents), 1)
  const top = niceCeil(max)
  const x = (i: number) => pad.l + (i / (points.length - 1)) * (W - pad.l - pad.r)
  const y = (c: number) => pad.t + (1 - c / top) * (H - pad.t - pad.b)
  const line = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(p.balanceCents).toFixed(1)}`).join('')
  const area = `${line}L${x(points.length - 1).toFixed(1)},${y(0)}L${x(0)},${y(0)}Z`
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
    <figure className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <figcaption className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-semibold">Total owed under your plan</span>
        <span className="text-xs tabular-nums text-slate-500" aria-live="polite">
          {h ? `${formatMonth(h.month)}: ${formatCents(h.balanceCents)}` : 'Hover or touch for a month'}
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
        <path d={line} className="stroke-sky-700" strokeWidth={2} fill="none" strokeLinejoin="round" strokeLinecap="round" />
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
      <details className="mt-1 text-xs text-slate-500">
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
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-white p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="debt-sheet-h" className="text-lg font-semibold">
          {isNew ? 'Add a debt' : debt.name}
        </h2>
        {!isNew && debt.fromBank && <p className="text-xs text-slate-500">APR and minimum came from your bank. Change them if they’re off.</p>}

        <Field label="Name">
          <input value={name} onChange={(e) => setName(e.target.value)} className={inputCls} placeholder="e.g. Car loan" />
        </Field>
        {balanceEditable ? (
          <Field label="What you owe now">
            <Money value={balance} onChange={setBalance} label="Balance" />
          </Field>
        ) : (
          <p className="mt-4 text-sm text-slate-600">
            Owed now: <span className="font-semibold">{formatCents(debt!.balanceCents)}</span> <span className="text-xs text-slate-400">(from your bank)</span>
          </p>
        )}
        <div className="grid grid-cols-2 gap-3">
          <Field label="APR">
            <div className={`${inputCls} flex items-center`}>
              <input value={apr} onChange={(e) => setApr(e.target.value)} inputMode="decimal" aria-label="APR percent" placeholder="24.99" className="w-full bg-transparent text-right outline-none" />
              <span className="pl-1 text-sm text-slate-500">%</span>
            </div>
          </Field>
          <Field label="Minimum payment">
            <Money value={minimum} onChange={setMinimum} label="Minimum payment" />
          </Field>
          <Field label="Due day of the month">
            <input value={dueDay} onChange={(e) => setDueDay(e.target.value)} inputMode="numeric" placeholder="25" className={inputCls} />
          </Field>
          <Field label={debt?.isCard ? 'Credit limit' : 'Original amount'}>
            <Money value={limit} onChange={setLimit} label="Limit" />
          </Field>
        </div>
        <Field label="What you owed when you started tracking">
          <Money value={starting} onChange={setStarting} label="Starting balance" />
        </Field>
        {!isNew && (
          <label className="mt-4 flex items-center gap-3 rounded-xl bg-slate-50 px-4 py-3">
            <input type="checkbox" checked={hidden} onChange={(e) => setHidden(e.target.checked)} className="size-5" />
            <span className="text-sm">
              Leave out of the plan
              <span className="block text-xs text-slate-500">For a card you pay in full every month.</span>
            </span>
          </label>
        )}

        {err && (
          <p role="alert" className="mt-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
            {err}
          </p>
        )}
        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-xl bg-slate-100 py-3 font-semibold text-slate-700">
            Cancel
          </button>
          <button type="submit" disabled={busy} className="flex-1 rounded-xl bg-slate-900 py-3 font-semibold text-white disabled:opacity-50">
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
            className="mt-3 w-full py-2 text-sm font-medium text-red-600"
          >
            Remove this debt
          </button>
        )}
      </form>
    </div>
  )
}

const inputCls = 'mt-1 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-base'

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="mt-4 block">
      <span className="text-sm font-medium text-slate-700">{label}</span>
      {children}
    </label>
  )
}

function Money({ value, onChange, label }: { value: string; onChange: (v: string) => void; label: string }) {
  return (
    <div className={`${inputCls} flex items-center`}>
      <span className="text-sm text-slate-500">$</span>
      <input aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} inputMode="decimal" className="w-full bg-transparent px-1 text-right tabular-nums outline-none" />
    </div>
  )
}

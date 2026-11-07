import { useCallback, useEffect, useState } from 'react'
import { ApiError, paydayApi, type PayCheck, type PaydaySummary, type PlanItem } from './api'
import { ErrorNotice, Loading } from './ui'
import { formatCents, formatDay } from './format'

const weekday = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })
}
const monthName = (iso: string) => {
  const [y, m] = iso.split('-').map(Number)
  return new Date(y, m - 1, 1).toLocaleDateString('en-US', { month: 'long' })
}

/**
 * The Payday tab (SPEC 4.6): the next checks with a low/likely/high range, the payday plan for each, and predicted vs
 * actual once a deposit lands. Setup (schedule, pay stubs, profile, rules) is on its own screen.
 */
export default function PaydayScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [s, setS] = useState<PaydaySummary | null>(null)
  const [error, setError] = useState<string | null>(null)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )
  const load = useCallback(async () => {
    try {
      setS(await paydayApi.summary())
      setError(null)
    } catch (e) {
      fail(e)
    }
  }, [fail])
  useEffect(() => {
    void load()
  }, [load])

  async function toggle(check: PayCheck, item: PlanItem) {
    try {
      await paydayApi.setDone(check.payDate, item.key, !item.done)
      await load()
    } catch (e) {
      fail(e)
    }
  }

  const empty = s && s.scheduleMonths.length === 0 && s.stubCount === 0

  return (
    <div className="mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">
      <div className="flex items-baseline justify-between">
        <h1 className="text-2xl font-bold tracking-tight">Payday</h1>
        <a href="#/payday/setup" className="text-sm font-medium text-sky-700">
          Schedule & stubs
        </a>
      </div>
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!s && !error && <Loading />}

      {empty && (
        <section className="mt-4 rounded-2xl bg-white p-5 ring-1 ring-slate-200">
          <h2 className="font-semibold">Forecast your paychecks</h2>
          <p className="mt-1 text-sm text-slate-600">
            Import this month’s SkedPlus+ schedule to see what each check should be, then add 2 or more recent pay stubs so the app learns your taxes and deductions.
          </p>
          <a href="#/payday/setup" className="mt-4 block rounded-xl bg-slate-900 py-3 text-center text-sm font-semibold text-white">
            Import a schedule or pay stub
          </a>
        </section>
      )}

      {s && !empty && (
        <>
          <ul className="mt-4 space-y-3">
            {s.checks.map((c, i) => (
              <li key={c.payDate}>
                <CheckCard c={c} first={i === 0} onToggle={(item) => void toggle(c, item)} />
              </li>
            ))}
          </ul>

          {s.history.length > 0 && (
            <section aria-labelledby="hist-h" className="mt-6">
              <h2 id="hist-h" className="text-sm font-semibold text-slate-700">
                Predicted vs actual
              </h2>
              <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
                {s.history.map((h) => (
                  <li key={h.payDate} className="flex items-center justify-between gap-3 px-4 py-2.5 text-sm">
                    <span>
                      <span className="font-medium">{formatDay(h.payDate)}</span>
                      <span className="block text-xs text-slate-500">
                        Predicted {formatCents(h.predictedNetCents)}
                        {h.source === 'stub' ? ' · from your stub' : h.source === 'deposit' ? ' · deposit' : ''}
                      </span>
                    </span>
                    {h.actualNetCents === null ? (
                      <span className="text-xs text-slate-500">Not seen yet</span>
                    ) : (
                      <span className="text-right tabular-nums">
                        <span className="font-semibold">{formatCents(h.actualNetCents)}</span>
                        <span className={`block text-xs ${Math.abs(h.varianceCents ?? 0) < 1_000 ? 'text-slate-500' : (h.varianceCents ?? 0) > 0 ? 'text-emerald-700' : 'text-amber-600'}`}>
                          {(h.varianceCents ?? 0) >= 0 ? '+' : '−'}
                          {formatCents(Math.abs(h.varianceCents ?? 0))}
                        </span>
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )}

          <p className="mt-6 text-xs text-slate-500">
            Forecasts use your imported schedule, the pay rules table and the withholding model learned from {s.stubCount} pay stub{s.stubCount === 1 ? '' : 's'}
            {s.modelEdited ? ' (with your edits)' : ''}. They’re estimates, not guarantees. Profit sharing and other irregular payouts are left out.
          </p>
        </>
      )}
    </div>
  )
}

function CheckCard({ c, first, onToggle }: { c: PayCheck; first: boolean; onToggle: (item: PlanItem) => void }) {
  const [open, setOpen] = useState(false)
  const [planOpen, setPlanOpen] = useState(first)
  return (
    <article className="rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="font-semibold">{weekday(c.payDate)}</h2>
        <span className="text-xs text-slate-500">
          Covers {formatDay(c.periodStart)}–{formatDay(c.periodEnd)}
        </span>
      </div>
      {!c.hasSchedule ? (
        <p className="mt-2 text-sm text-slate-600">
          Import your {monthName(c.periodStart)} schedule to forecast this check.{' '}
          <a href="#/payday/setup" className="font-medium text-sky-700">
            Import
          </a>
        </p>
      ) : (
        <>
          <p className="mt-1 text-3xl font-bold tabular-nums tracking-tight">{formatCents(c.netCents)}</p>
          <p className="text-sm text-slate-500">
            Likely take-home · range {formatCents(c.lowNetCents)}–{formatCents(c.highNetCents)} · gross {formatCents(c.grossCents)}
          </p>
          {c.lowConfidence && (
            <p className="mt-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800">
              <span className="font-semibold">Low confidence.</span> {c.reasons.join(' ')}
            </p>
          )}
          {c.notes.map((n) => (
            <p key={n} className="mt-2 text-xs text-slate-500">
              {n}
            </p>
          ))}
          <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="mt-2 text-xs font-medium text-sky-700">
            {open ? 'Hide' : 'How this is figured'}
          </button>
          {open && (
            <dl className="mt-2 space-y-1 text-sm">
              {c.lines.map((l) => (
                <Row key={l.name + l.note} label={l.name} cents={l.cents} note={l.note} />
              ))}
              <Row label="Gross" cents={c.grossCents} strong />
              {c.taxes.map((t) => (
                <Row key={t.name} label={t.name} cents={-t.cents} />
              ))}
              {c.deductions.map((d) => (
                <Row key={d.name} label={d.name} cents={-d.cents} />
              ))}
              <Row label="Take-home" cents={c.netCents} strong />
            </dl>
          )}

          {c.plan && (
            <section className="mt-3 border-t border-slate-100 pt-3">
              <button type="button" onClick={() => setPlanOpen(!planOpen)} aria-expanded={planOpen} className="flex w-full items-center justify-between text-sm font-semibold">
                Payday plan
                <span className="text-xs font-normal text-slate-500">
                  {c.plan.items.filter((i) => i.done).length} of {c.plan.items.length} done
                </span>
              </button>
              {planOpen && (
                <>
                  {c.plan.shortCents > 0 && (
                    <p className="mt-2 rounded-lg bg-red-50 px-3 py-2 text-xs text-red-800">
                      This check is {formatCents(c.plan.shortCents)} short of everything planned for it.
                    </p>
                  )}
                  <ul className="mt-2 space-y-1">
                    {c.plan.items.map((item) => (
                      <li key={item.key}>
                        <label className="flex cursor-pointer items-start gap-3 rounded-lg px-1 py-1.5">
                          <input type="checkbox" checked={item.done} onChange={() => onToggle(item)} className="mt-1 size-4 accent-slate-900" />
                          <span className="min-w-0 flex-1">
                            <span className={`block text-sm ${item.done ? 'text-slate-400 line-through' : ''}`}>{planText(item)}</span>
                            <span className="block text-xs text-slate-500">{item.why}</span>
                          </span>
                        </label>
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </section>
          )}
        </>
      )}
    </article>
  )
}

function planText(i: PlanItem) {
  const amount = formatCents(i.cents)
  switch (i.kind) {
    case 'bill':
      return `${i.destination.replace(/^Keep in /, 'Keep ' + amount + ' in ')} for ${i.label}`
    case 'spending':
      return `Keep ${amount} in ${i.destination} for spending`
    default:
      return `Send ${amount} to ${i.destination} (${i.label.toLowerCase()})`
  }
}

function Row({ label, cents, note, strong }: { label: string; cents: number; note?: string; strong?: boolean }) {
  return (
    <div className={strong ? 'border-t border-slate-100 pt-1 font-semibold' : ''}>
      <div className="flex justify-between gap-3">
        <dt>{label}</dt>
        <dd className="tabular-nums">{cents < 0 ? `−${formatCents(-cents)}` : formatCents(cents)}</dd>
      </div>
      {note && <p className="text-xs text-slate-500">{note}</p>}
    </div>
  )
}

import { useCallback, useEffect, useState, type ChangeEvent, type ReactNode } from 'react'
import {
  ApiError,
  paydayApi,
  type ModelView,
  type PayProfile,
  type PayRules,
  type ScheduleMonth,
  type StubListItem,
  type StubReview,
  type WithholdingModel,
} from './api'
import { formatCents, formatDay } from './format'

const hm = (minutes: number) => `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}`
const monthLabel = (y: number, m: number) => new Date(y, m - 1, 1).toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
const schedules: Record<string, string> = { Every: 'every check', SecondHalf: 'the check paid on the 7th', FirstHalf: 'the check paid on the 22nd' }

/**
 * Payday setup (SPEC 4.6, 4.9): your pay profile, the monthly schedule import, pay stub upload with a review before
 * saving, the learned withholding model and the pay rules table, all viewable and editable.
 */
export default function PaySetupScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [profile, setProfile] = useState<PayProfile | null>(null)
  const [months, setMonths] = useState<ScheduleMonth[]>([])
  const [stubs, setStubs] = useState<StubListItem[]>([])
  const [model, setModel] = useState<ModelView | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [preview, setPreview] = useState<{ file: File; month: ScheduleMonth; replaces: boolean } | null>(null)
  const [review, setReview] = useState<StubReview | null>(null)
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
      const [summary, m, st, mv] = await Promise.all([paydayApi.summary(), paydayApi.schedule(), paydayApi.stubs(), paydayApi.model()])
      setProfile(summary.profile)
      setMonths(m)
      setStubs(st)
      setModel(mv)
    } catch (e) {
      fail(e)
    }
  }, [fail])
  useEffect(() => {
    void load()
  }, [load])

  async function act(action: () => Promise<unknown>, message: string) {
    setBusy(true)
    setError(null)
    try {
      await action()
      setNotice(message)
      await load()
    } catch (e) {
      fail(e)
    } finally {
      setBusy(false)
    }
  }

  async function pickSchedule(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setError(null)
    try {
      const r = await paydayApi.previewSchedule(file)
      setPreview({ file, month: r.month, replaces: r.replaces })
    } catch (err) {
      fail(err)
    }
  }

  async function pickStub(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setError(null)
    try {
      setReview(await paydayApi.reviewStub(file))
    } catch (err) {
      fail(err)
    }
  }

  return (
    <div className="mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">
      <a href="#/payday" className="text-sm font-medium text-sky-700">
        ‹ Payday
      </a>
      <h1 className="mt-1 text-2xl font-bold tracking-tight">Schedule & pay stubs</h1>
      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && (
        <p role="alert" className="mt-4 rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">
          {error}
        </p>
      )}

      <Section title="Monthly schedule" hint="The SkedPlus+ detailed text export (.txt) is exact; the older CSV works too, with estimated times. Re-importing a month replaces it.">
        <FileButton label="Import a schedule" accept=".txt,.csv,text/plain,text/csv" onChange={pickSchedule} />
        {preview && (
          <div className="mt-3 rounded-xl bg-white p-4 ring-2 ring-sky-600">
            <p className="text-sm font-semibold">
              {monthLabel(preview.month.year, preview.month.month)}: {preview.month.trips.length} trips, {hm(preview.month.creditMinutes)} credit
              {preview.replaces ? ' (replaces the month you imported before)' : ''}
            </p>
            <p className="text-xs text-slate-500">Check the trips, then confirm.</p>
            <MonthBody m={preview.month} />
            <div className="mt-3 flex gap-2">
              <button type="button" onClick={() => setPreview(null)} className="flex-1 rounded-xl bg-slate-100 py-2.5 text-sm font-semibold text-slate-700">
                Cancel
              </button>
              <button
                type="button"
                disabled={busy}
                onClick={() =>
                  void act(async () => {
                    await paydayApi.importSchedule(preview.file)
                    setPreview(null)
                  }, `Imported ${monthLabel(preview.month.year, preview.month.month)}. The forecast is updated.`)
                }
                className="flex-1 rounded-xl bg-slate-900 py-2.5 text-sm font-semibold text-white disabled:opacity-50"
              >
                Confirm import
              </button>
            </div>
          </div>
        )}
        <ul className="mt-3 space-y-3">
          {months.map((m) => (
            <li key={`${m.year}-${m.month}`}>
              <MonthCard m={m} busy={busy} act={act} />
            </li>
          ))}
        </ul>
      </Section>

      <Section title="Pay stubs" hint="Upload past pay advice PDFs: 2 is the minimum, 6 recent ones is better. You review the numbers before anything is saved, and the file itself is never kept.">
        <FileButton label="Upload a pay stub" accept=".pdf,application/pdf" onChange={pickStub} />
        {review && <StubReviewCard r={review} busy={busy} onCancel={() => setReview(null)} onSave={() => void act(async () => {
          const saved = await paydayApi.confirmStub(review.stub)
          setReview(null)
          if (saved.varianceCents !== null) setNotice(`Saved. The app had predicted ${formatCents(saved.predictedNetCents ?? 0)}; it was ${formatCents(saved.actualNetCents)}.`)
        }, 'Saved. Your withholding model is refitted.')} />}
        {stubs.length > 0 && (
          <ul className="mt-3 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
            {stubs.map((s) => (
              <li key={s.id} className="flex items-center justify-between gap-3 px-4 py-2.5 text-sm">
                <span>
                  <span className="font-medium">Paid {formatDay(s.payDate)}</span>
                  <span className="block text-xs text-slate-500">
                    {formatDay(s.periodStart)}–{formatDay(s.periodEnd)} · gross {formatCents(s.grossCents)}
                  </span>
                </span>
                <span className="flex items-center gap-3">
                  <span className="font-semibold tabular-nums">{formatCents(s.netCents)}</span>
                  <button type="button" aria-label={`Remove the stub paid ${formatDay(s.payDate)}`} onClick={() => void act(() => paydayApi.removeStub(s.id), 'Removed. The model is refitted.')} className="text-xs text-red-600">
                    Remove
                  </button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Section>

      {model && <ModelSection view={model} busy={busy} act={act} />}
      {profile && <ProfileSection p={profile} busy={busy} act={act} />}
      <RulesSection busy={busy} act={act} fail={fail} />
    </div>
  )
}

type Act = (action: () => Promise<unknown>, message: string) => Promise<void>

function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <section className="mt-6">
      <h2 className="text-sm font-semibold text-slate-700">{title}</h2>
      {hint && <p className="text-xs text-slate-500">{hint}</p>}
      <div className="mt-2">{children}</div>
    </section>
  )
}

function FileButton({ label, accept, onChange }: { label: string; accept: string; onChange: (e: ChangeEvent<HTMLInputElement>) => void }) {
  return (
    <label className="block cursor-pointer rounded-xl bg-white py-3 text-center text-sm font-medium ring-1 ring-slate-200 focus-within:ring-2 focus-within:ring-sky-600">
      {label}
      <input type="file" accept={accept} onChange={onChange} className="sr-only" />
    </label>
  )
}

function MonthBody({ m, onOvertime, onPaid }: { m: ScheduleMonth; onOvertime?: (key: string) => void; onPaid?: (key: string) => void }) {
  return (
    <>
      {m.shortfallMinutes > 0 && (
        <p className="mt-2 text-xs text-slate-600">
          {hm(m.shortfallMinutes)} below the line guarantee: no top-up is assumed, so the checks are lighter (information only).
        </p>
      )}
      {m.overrideMinutes > 0 && <p className="mt-2 text-xs text-emerald-700">{hm(m.overrideMinutes)} above 87 hours: the override is forecast (an estimate).</p>}
      <ul className="mt-2 space-y-2">
        {m.trips.map((t) => (
          <li key={t.key} className="rounded-lg bg-slate-50 px-3 py-2">
            <div className="flex items-baseline justify-between gap-2 text-sm">
              <span className="font-medium">
                {t.code} · {formatDay(t.start)}–{formatDay(t.end)}
              </span>
              <span className="font-semibold tabular-nums">{formatCents(t.payCents)}</span>
            </div>
            <p className="text-xs text-slate-500">
              {hm(t.creditMinutes)} credit · {hm(t.tafbMinutes)} away · {t.revenueLegs} boarded legs · credit {formatCents(t.creditPayCents)}, boarding {formatCents(t.boardingCents)}, per diem{' '}
              {formatCents(t.perDiemCents)}
            </p>
            <p className="text-xs text-slate-400">{t.days.map((d) => `${formatDay(d.date)} ${d.route} (${hm(d.creditMinutes)})`).join(' · ')}</p>
            {onOvertime && (
              <label className="mt-1 flex items-center gap-2 text-xs">
                <input type="checkbox" checked={t.overtime} onChange={() => onOvertime(t.key)} className="accent-slate-900" />
                Flown as overtime (1.5×)
              </label>
            )}
          </li>
        ))}
        {m.entries.map((e) => (
          <li key={e.key} className="rounded-lg bg-slate-50 px-3 py-2 text-sm">
            <span className="font-medium">{e.code}</span> · {formatDay(e.start.slice(0, 10))}–{formatDay(e.end.slice(0, 10))} · {hm(e.creditMinutes)} credit
            {onPaid ? (
              <label className="mt-1 flex items-center gap-2 text-xs">
                <input type="checkbox" checked={e.paid} onChange={() => onPaid(e.key)} className="accent-slate-900" />
                This is paid (treated as unpaid unless you switch it on)
              </label>
            ) : (
              <span className="block text-xs text-slate-500">Treated as unpaid.</span>
            )}
          </li>
        ))}
      </ul>
      {m.warnings.length > 0 && (
        <ul className="mt-2 space-y-1">
          {m.warnings.map((w) => (
            <li key={w} className="text-xs text-amber-700">
              {w}
            </li>
          ))}
        </ul>
      )}
      <details className="mt-2 text-xs text-slate-500">
        <summary className="cursor-pointer">Credit checks ({m.checks.length})</summary>
        <ul className="mt-1 space-y-0.5">
          {m.checks.map((c) => (
            <li key={c}>{c}</li>
          ))}
        </ul>
      </details>
    </>
  )
}

function MonthCard({ m, busy, act }: { m: ScheduleMonth; busy: boolean; act: Act }) {
  const overtime = m.trips.filter((t) => t.overtime).map((t) => t.key)
  const paid = m.entries.filter((e) => e.paid).map((e) => e.key)
  const save = (reserve: boolean, ot: string[], pe: string[]) => void act(() => paydayApi.setChoices(m.year, m.month, reserve, ot, pe), 'Saved. The forecast is updated.')
  const flip = (list: string[], key: string) => (list.includes(key) ? list.filter((k) => k !== key) : [...list, key])
  return (
    <div className="rounded-xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-2">
        <h3 className="font-semibold">{monthLabel(m.year, m.month)}</h3>
        <span className="text-xs text-slate-500">
          {hm(m.creditMinutes)} credit · {m.source === 'text' ? 'text export' : 'CSV (estimated)'}
        </span>
      </div>
      <label className="mt-2 flex items-center gap-2 text-sm">
        <input type="checkbox" checked={m.reserve} disabled={busy} onChange={() => save(!m.reserve, overtime, paid)} className="accent-slate-900" />
        Reserve this month (the 76-hour guarantee, split across the two checks)
      </label>
      <MonthBody m={m} onOvertime={(k) => save(m.reserve, flip(overtime, k), paid)} onPaid={(k) => save(m.reserve, overtime, flip(paid, k))} />
      <button type="button" disabled={busy} onClick={() => void act(() => paydayApi.removeMonth(m.year, m.month), `Removed ${monthLabel(m.year, m.month)}.`)} className="mt-2 text-xs font-medium text-red-600">
        Remove this month
      </button>
    </div>
  )
}

function StubReviewCard({ r, busy, onCancel, onSave }: { r: StubReview; busy: boolean; onCancel: () => void; onSave: () => void }) {
  const s = r.stub
  return (
    <div className="mt-3 rounded-xl bg-white p-4 ring-2 ring-sky-600">
      <p className="text-sm font-semibold">
        Paid {formatDay(s.payDate)} for {formatDay(s.periodStart)}–{formatDay(s.periodEnd)}
        {r.replaces ? ' (replaces the stub you saved for this date)' : ''}
      </p>
      <p className="text-xs text-slate-500">Check these numbers against your stub before saving. Only the numbers are kept.</p>
      <dl className="mt-2 space-y-0.5 text-sm">
        {s.earnings.map((e) => (
          <Line key={'e' + e.name} label={e.name + (e.hours !== null ? ` (${e.hours} h)` : '')} cents={e.currentCents} />
        ))}
        <Line label="Gross" cents={s.grossCents} strong />
        <Line label="Federal taxable" cents={s.fedTaxableCents} muted />
        {s.taxes.map((t) => (
          <Line key={'t' + t.name} label={t.name} cents={-t.currentCents} />
        ))}
        {[...s.preTax, ...s.afterTax].map((d) => (
          <Line key={'d' + d.name} label={d.name} cents={-d.currentCents} />
        ))}
        <Line label="Net pay" cents={s.netCents} strong />
      </dl>
      {(s.userHoursEarned !== null || s.vacationHoursEarned !== null) && (
        <p className="mt-1 text-xs text-slate-500">
          Earned this period: {s.userHoursEarned ?? 0} user and {s.vacationHoursEarned ?? 0} vacation hours.
        </p>
      )}
      <ul className="mt-2 space-y-0.5 text-xs">
        {r.checks.map((c) => (
          <li key={c} className="text-emerald-700">
            ✓ {c}
          </li>
        ))}
        {r.problems.map((p) => (
          <li key={p} className="text-red-700">
            ✗ {p}
          </li>
        ))}
      </ul>
      <div className="mt-3 flex gap-2">
        <button type="button" onClick={onCancel} className="flex-1 rounded-xl bg-slate-100 py-2.5 text-sm font-semibold text-slate-700">
          Cancel
        </button>
        <button type="button" disabled={busy || r.problems.length > 0} onClick={onSave} className="flex-1 rounded-xl bg-slate-900 py-2.5 text-sm font-semibold text-white disabled:opacity-40">
          Save stub
        </button>
      </div>
    </div>
  )
}

function Line({ label, cents, strong, muted }: { label: string; cents: number; strong?: boolean; muted?: boolean }) {
  return (
    <div className={`flex justify-between gap-3 ${strong ? 'border-t border-slate-100 pt-0.5 font-semibold' : ''} ${muted ? 'text-slate-500' : ''}`}>
      <dt>{label}</dt>
      <dd className="tabular-nums">{cents < 0 ? `−${formatCents(-cents)}` : formatCents(cents)}</dd>
    </div>
  )
}

function ModelSection({ view, busy, act }: { view: ModelView; busy: boolean; act: Act }) {
  const [draft, setDraft] = useState<WithholdingModel | null>(null)
  const m = draft ?? view.model
  if (!m)
    return (
      <Section title="Withholding model">
        <p className="rounded-xl bg-white p-4 text-sm text-slate-600 ring-1 ring-slate-200">
          Add at least 2 pay stubs and the app learns your taxes and deductions from them. Until then, take-home is a flat percent of gross (below).
        </p>
      </Section>
    )
  const editTax = (i: number, field: 'rate' | 'interceptCents' | 'flatCents', value: number) =>
    setDraft({ ...m, taxes: m.taxes.map((t, k) => (k === i ? { ...t, [field]: value } : t)) })
  const worst = Math.max(0, ...view.backTests.map((b) => Math.abs(b.errorCents)))
  return (
    <Section title="Withholding model" hint={`Learned from ${view.stubCount} stubs${view.edited ? ', with your edits' : ''}. A new stub refits it.`}>
      <div className="rounded-xl bg-white p-4 text-sm ring-1 ring-slate-200">
        {view.backTests.length > 0 && (
          <p className={worst <= 1 ? 'text-emerald-700' : 'text-amber-700'}>
            {worst <= 1 ? `Reproduces all ${view.backTests.length} stubs' net pay to within a cent.` : `Off by up to ${formatCents(worst)} on a stub.`}
          </p>
        )}
        <p className="mt-2 text-xs text-slate-500">Untaxed: {m.nonTaxable.join(', ') || 'none'}. Fitted on taxable pay from {formatCents(m.minTaxableCents)} to {formatCents(m.maxTaxableCents)}; outside that, forecasts are low confidence.</p>
        <ul className="mt-3 space-y-2">
          {m.taxes.map((t, i) => (
            <li key={t.name}>
              <p className="font-medium">{t.name}</p>
              {t.kind === 'Flat' ? (
                <p className="flex items-center gap-2 text-xs text-slate-600">
                  <NumberInput label={`${t.name} amount`} value={t.flatCents / 100} step={0.01} onChange={(v) => editTax(i, 'flatCents', Math.round(v * 100))} /> on {schedules[t.schedule]}
                </p>
              ) : (
                <p className="flex flex-wrap items-center gap-1 text-xs text-slate-600">
                  <NumberInput label={`${t.name} rate`} value={Number((t.rate * 100).toFixed(4))} step={0.01} onChange={(v) => editTax(i, 'rate', v / 100)} />% of{' '}
                  {t.base === 'FederalTaxable' ? 'federal taxable' : 'wages before pre-tax benefits'}
                  {' + '}
                  <NumberInput label={`${t.name} adjustment`} value={t.interceptCents / 100} step={0.01} onChange={(v) => editTax(i, 'interceptCents', Math.round(v * 100))} />
                  {t.roundToDollar ? ', whole dollars' : ''}, never below $0{t.schedule !== 'Every' ? `, on ${schedules[t.schedule]}` : ''}
                </p>
              )}
            </li>
          ))}
        </ul>
        <p className="mt-3 text-xs text-slate-600">
          Deductions: {[...m.preTax.map((d) => `${d.name} ${formatCents(d.cents)} (pre-tax)`), ...m.afterTax.map((d) => `${d.name} ${formatCents(d.cents)}`)].join(', ') || 'none'}.
        </p>
        <p className="mt-1 text-xs text-slate-600">
          Fixed lines: {m.recurringEarnings.map((r) => `${r.name} ${formatCents(r.cents)} on ${schedules[r.schedule]}`).join(', ') || 'none'}.
          {m.leftOut.length > 0 && ` Left out as irregular: ${m.leftOut.join(', ')}.`}
        </p>
        <p className="mt-1 flex items-center gap-2 text-xs text-slate-600">
          PTO cashed out per check (hours, average):
          <NumberInput label="PTO hours per check" value={m.ptoHoursPerCheck} step={0.1} onChange={(v) => setDraft({ ...m, ptoHoursPerCheck: v })} />
        </p>
        {m.notes.map((n) => (
          <p key={n} className="mt-1 text-xs text-amber-700">
            {n}
          </p>
        ))}
        <div className="mt-3 flex gap-2">
          {draft && (
            <button type="button" disabled={busy} onClick={() => void act(async () => { await paydayApi.setModel(draft); setDraft(null) }, 'Model saved.')} className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white">
              Save edits
            </button>
          )}
          {(draft || view.edited) && (
            <button type="button" disabled={busy} onClick={() => void act(async () => { setDraft(null); await paydayApi.resetModel() }, 'Back to the model learned from your stubs.')} className="rounded-lg bg-slate-100 px-3 py-1.5 text-xs font-semibold text-slate-700">
              Use the learned model
            </button>
          )}
        </div>
      </div>
    </Section>
  )
}

function NumberInput({ label, value, step, onChange }: { label: string; value: number; step: number; onChange: (v: number) => void }) {
  return (
    <input
      aria-label={label}
      type="number"
      step={step}
      value={value}
      onChange={(e) => {
        const v = Number(e.target.value)
        if (e.target.value !== '' && Number.isFinite(v)) onChange(v)
      }}
      className="w-20 rounded-md border border-slate-300 bg-white px-1.5 py-0.5 text-right tabular-nums"
    />
  )
}

function ProfileSection({ p, busy, act }: { p: PayProfile; busy: boolean; act: Act }) {
  const [d, setD] = useState(p)
  const changed = d.yearsOfService !== p.yearsOfService || d.fullTime !== p.fullTime || d.holidayPay !== p.holidayPay || d.flatNetPercent !== p.flatNetPercent
  return (
    <Section title="Pay profile" hint={`Your hourly rate comes from years of service in the pay rules table: ${formatCents(p.rateCents)}/hour now.`}>
      <div className="space-y-2 rounded-xl bg-white p-4 text-sm ring-1 ring-slate-200">
        <label className="flex items-center justify-between gap-2">
          Completed years of service
          <NumberInput label="Years of service" value={d.yearsOfService} step={1} onChange={(v) => setD({ ...d, yearsOfService: Math.max(0, Math.round(v)) })} />
        </label>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={d.fullTime} onChange={() => setD({ ...d, fullTime: !d.fullTime })} className="accent-slate-900" />
          Full-time (80-hour line guarantee; part-time is 40)
        </label>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={d.holidayPay} onChange={() => setD({ ...d, holidayPay: !d.holidayPay })} className="accent-slate-900" />
          Eligible for holiday pay
        </label>
        <label className="flex items-center justify-between gap-2">
          Take-home with fewer than 2 stubs (% of gross)
          <NumberInput label="Flat take-home percent" value={d.flatNetPercent} step={1} onChange={(v) => setD({ ...d, flatNetPercent: Math.round(v) })} />
        </label>
        {changed && (
          <button type="button" disabled={busy} onClick={() => void act(() => paydayApi.setProfile(d), 'Profile saved.')} className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white">
            Save profile
          </button>
        )}
      </div>
    </Section>
  )
}

const ruleFields: [keyof PayRules, string, 'cents' | 'hm' | 'number' | 'text'][] = [
  ['perDiemCentsPerHour', 'Per diem per hour', 'cents'],
  ['minDayCreditMinutes', 'Minimum daily guarantee (H:MM)', 'hm'],
  ['dutyHoursPerCreditHour', 'Duty guarantee: 1 hour per this many duty hours', 'number'],
  ['tafbHoursPerCreditHour', 'Time-away guarantee: 1 hour per this many hours away', 'number'],
  ['lineGuaranteeHours', 'Line holder guarantee (hours)', 'number'],
  ['reserveGuaranteeHours', 'Reserve guarantee (hours)', 'number'],
  ['partTimeGuaranteeHours', 'Part-time guarantee (hours)', 'number'],
  ['overrideAboveHours', 'Productivity override above (hours)', 'number'],
  ['overrideCentsPerHour', 'Override per hour', 'cents'],
  ['overtimeMultiplier', 'Overtime multiplier', 'number'],
  ['holidayCreditMinutes', 'Holiday pay credit (H:MM)', 'hm'],
  ['boardingHoursPerLeg', 'Boarding pay hours per leg', 'number'],
  ['boardingRateFactor', 'Boarding pay share of the hourly rate', 'number'],
  ['reportMinutesBefore', 'CSV: report minutes before departure', 'number'],
  ['reportMinutesBeforeInternational', 'CSV: report minutes before an international departure', 'number'],
  ['releaseMinutesAfter', 'CSV: release minutes after arrival', 'number'],
  ['pairingBreakHours', 'CSV: a pairing ends after this many hours at base', 'number'],
  ['base', 'Base airport', 'text'],
  ['firstHalfPayDay', 'Pay day for the 1st–15th', 'number'],
  ['secondHalfPayDay', 'Pay day for the 16th–end (next month)', 'number'],
]

function RulesSection({ busy, act, fail }: { busy: boolean; act: Act; fail: (e: unknown) => void }) {
  const [open, setOpen] = useState(false)
  const [rules, setRules] = useState<PayRules | null>(null)
  const [edited, setEdited] = useState(false)
  const [text, setText] = useState({ rates: '', holidays: '', airports: '' })

  async function show() {
    setOpen(!open)
    if (rules || open) return
    try {
      const r = await paydayApi.rules()
      setRules(r.rules)
      setEdited(r.edited)
      setText({
        rates: r.rules.rates.map((x) => `${x.years} ${(x.centsPerHour / 100).toFixed(2)}`).join('\n'),
        holidays: r.rules.holidays.join('\n'),
        airports: r.rules.airports.map((a) => `${a.code} ${a.timeZoneId}${a.international ? ' international' : ''}`).join('\n'),
      })
    } catch (e) {
      fail(e)
    }
  }

  function value(key: keyof PayRules, kind: string) {
    const v = rules![key] as number | string
    return kind === 'cents' ? (Number(v) / 100).toFixed(2) : kind === 'hm' ? hm(Number(v)) : String(v)
  }
  function set(key: keyof PayRules, kind: string, raw: string) {
    let v: number | string = raw
    if (kind === 'cents') v = Math.round(Number(raw) * 100)
    else if (kind === 'hm') {
      const [h, m] = raw.split(':').map(Number)
      v = (h || 0) * 60 + (m || 0)
    } else if (kind === 'number') v = Number(raw)
    setRules({ ...rules!, [key]: v })
  }
  function parsed(): PayRules {
    return {
      ...rules!,
      rates: text.rates.split('\n').map((l) => l.trim().split(/\s+/)).filter((p) => p.length === 2).map(([y, r]) => ({ years: Number(y), centsPerHour: Math.round(Number(r) * 100) })),
      holidays: text.holidays.split('\n').map((l) => l.trim()).filter(Boolean),
      airports: text.airports
        .split('\n')
        .map((l) => l.trim().split(/\s+/))
        .filter((p) => p.length >= 2)
        .map(([code, zone, intl]) => ({ code: code.toUpperCase(), timeZoneId: zone, international: intl === 'international' })),
    }
  }

  return (
    <Section title="Pay rules table" hint="Seeded from the policy manual (revision 28). Every value is editable; check it against the current manual.">
      <button type="button" onClick={() => void show()} aria-expanded={open} className="w-full rounded-xl bg-white py-3 text-sm font-medium ring-1 ring-slate-200">
        {open ? 'Hide the rules' : `View and edit the rules${edited ? ' (edited)' : ''}`}
      </button>
      {open && rules && (
        <div className="mt-2 space-y-2 rounded-xl bg-white p-4 text-sm ring-1 ring-slate-200">
          {ruleFields.map(([key, label, kind]) => (
            <label key={key} className="flex items-center justify-between gap-2 text-xs">
              {label}
              <input aria-label={label} value={value(key, kind)} onChange={(e) => set(key, kind, e.target.value)} className="w-24 rounded-md border border-slate-300 bg-white px-1.5 py-0.5 text-right" />
            </label>
          ))}
          <RulesText label="Hourly rates (years of service, rate per hour)" value={text.rates} onChange={(v) => setText({ ...text, rates: v })} />
          <RulesText label="Holidays (YYYY-MM-DD)" value={text.holidays} onChange={(v) => setText({ ...text, holidays: v })} />
          <RulesText label="Airport time zones (code, IANA zone, 'international')" value={text.airports} onChange={(v) => setText({ ...text, airports: v })} />
          <div className="flex gap-2 pt-1">
            <button type="button" disabled={busy} onClick={() => void act(async () => { await paydayApi.setRules(parsed()); setEdited(true) }, 'Pay rules saved.')} className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white">
              Save rules
            </button>
            {edited && (
              <button
                type="button"
                disabled={busy}
                onClick={() => void act(async () => { await paydayApi.resetRules(); setRules(null); setOpen(false); setEdited(false) }, 'Back to the rules from the manual.')}
                className="rounded-lg bg-slate-100 px-3 py-1.5 text-xs font-semibold text-slate-700"
              >
                Reset to the manual
              </button>
            )}
          </div>
        </div>
      )}
    </Section>
  )
}

function RulesText({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <label className="block text-xs">
      {label}
      <textarea aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} rows={4} className="mt-1 block w-full rounded-md border border-slate-300 bg-white px-2 py-1 font-mono text-xs" />
    </label>
  )
}

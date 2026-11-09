import { useCallback, useEffect, useState, type ChangeEvent, type ReactNode } from 'react'
import {
  ApiError,
  paydayApi,
  type PayProfile,
  type ScheduleMonth,
  type StubListItem,
  type StubReview,
} from './api'
import { useRemembered } from './offline'
import { Caret, ErrorNotice } from './ui'
import { formatCents, formatDay } from './format'

const hm = (minutes: number) => `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}`
const monthLabel = (y: number, m: number) => new Date(y, m - 1, 1).toLocaleDateString('en-US', { month: 'long', year: 'numeric' })

/**
 * Payday setup (SPEC 4.6, 4.9): the monthly schedule import, pay stub upload with a review before saving, and your pay
 * profile. The schedules and stubs already in are each on their own page (DECISIONS.md 184). The withholding model and
 * pay rules work on their own, so they aren't shown.
 */
export default function PaySetupScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [profile, setProfile] = useState<PayProfile | null>(null)
  const [months, setMonths] = useState<ScheduleMonth[]>([])
  const [stubs, setStubs] = useState<StubListItem[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [preview, setPreview] = useState<{ file: File; month: ScheduleMonth; replaces: boolean } | null>(null)
  const [review, setReview] = useState<StubReview | null>(null)
  const [busy, setBusy] = useState(false)
  const [sub, setSub] = useState(subFromHash)
  useEffect(() => {
    const onHash = () => {
      setSub(subFromHash())
      window.scrollTo(0, 0)
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )
  const load = useCallback(async () => {
    try {
      const [summary, m, st] = await Promise.all([paydayApi.summary(), paydayApi.schedule(), paydayApi.stubs()])
      setProfile(summary.profile)
      setMonths(m)
      setStubs(st)
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
      setTimeout(() => document.getElementById('schedule-preview')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50)
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

  const header = (back: string, backLabel: string, title: string) => (
    <>
      <a href={back} className="text-sm font-medium text-sky-700">
        ‹ {backLabel}
      </a>
      <h1 className="mt-1 text-2xl font-bold tracking-tight">{title}</h1>
      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
    </>
  )
  const shell = 'mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28'
  const previewCard = preview && (
        <div id="schedule-preview" className="mt-3 scroll-mt-4 rounded-xl bg-white p-4 ring-2 ring-sky-600">
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
  )

  if (sub === 'schedules')
    return (
      <div className={shell}>
        {header('#/payday/setup', 'Schedule & pay stubs', 'Schedules')}
        <div className="mt-4">
          {previewCard}
          {months.length === 0 ? (
            <p className="text-sm text-slate-500">No schedules imported yet.</p>
          ) : (
            <ul className="mt-3 space-y-3">
              {months.map((m) => (
                <li key={`${m.year}-${m.month}`}>
                  <MonthCard m={m} busy={busy} act={act} onUpdate={pickSchedule} />
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    )

  if (sub === 'stubs')
    return (
      <div className={shell}>
        {header('#/payday/setup', 'Schedule & pay stubs', 'Pay stubs')}
        {stubs.length === 0 ? (
          <p className="mt-4 text-sm text-slate-500">No pay stubs uploaded yet.</p>
        ) : (
          <ul className="mt-4 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
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
                  <button type="button" aria-label={`Remove the stub paid ${formatDay(s.payDate)}`} onClick={() => void act(() => paydayApi.removeStub(s.id), 'Removed.')} className="text-xs text-red-600">
                    Remove
                  </button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>
    )

  return (
    <div className={shell}>
      {header('#/payday', 'Plan', 'Schedule & pay stubs')}

      <Section title="Monthly schedule" hint="The SkedPlus+ detailed text export (.txt) is exact; the older CSV works too, with estimated times. Re-importing a month replaces it.">
        <FileButton label="Import a schedule" accept=".txt,.csv,text/plain,text/csv" onChange={pickSchedule} />
        {previewCard}
        <SubLink href="#/payday/setup/schedules" title="View schedules" detail={months.length === 0 ? 'None imported yet' : `${months.length} ${months.length === 1 ? 'month' : 'months'}: ${months.map((m) => monthLabel(m.year, m.month).split(' ')[0]).join(', ')}`} />
      </Section>

      <Section title="Pay stubs" hint="Upload past pay advice PDFs: 2 is the minimum, 6 recent ones is better. You review the numbers before anything is saved, and the file itself is never kept.">
        <FileButton label="Upload a pay stub" accept=".pdf,application/pdf" onChange={pickStub} />
        {review && <StubReviewCard r={review} busy={busy} onCancel={() => setReview(null)} onSave={() => void act(async () => {
          const saved = await paydayApi.confirmStub(review.stub)
          setReview(null)
          if (saved.varianceCents !== null) setNotice(`Saved. The app had predicted ${formatCents(saved.predictedNetCents ?? 0)}; it was ${formatCents(saved.actualNetCents)}.`)
        }, 'Saved. Your forecast learned from it.')} />}
        <SubLink href="#/payday/setup/stubs" title="View pay stubs" detail={stubs.length === 0 ? 'None uploaded yet' : `${stubs.length} ${stubs.length === 1 ? 'stub' : 'stubs'}, latest paid ${formatDay(stubs.reduce((a, b) => (a.payDate > b.payDate ? a : b)).payDate)}`} />
      </Section>

      {profile && <ProfileSection p={profile} busy={busy} act={act} />}
    </div>
  )
}

/** "View schedules" and "View pay stubs" (DECISIONS.md 184): a row that opens the list on its own page. */
function SubLink({ href, title, detail }: { href: string; title: string; detail: string }) {
  return (
    <a href={href} className="mt-3 flex items-center justify-between gap-3 rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
      <span className="min-w-0">
        <span className="block text-sm font-semibold">{title}</span>
        <span className="block truncate text-xs text-slate-500">{detail}</span>
      </span>
      <span aria-hidden className="font-semibold text-slate-400">›</span>
    </a>
  )
}

const subFromHash = () => {
  const h = window.location.hash
  return h.startsWith('#/payday/setup/stubs') ? 'stubs' : h.startsWith('#/payday/setup/schedules') ? 'schedules' : null
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

function MonthBody({ m, onOvertime, onPaid, onDropped }: { m: ScheduleMonth; onOvertime?: (key: string) => void; onPaid?: (key: string) => void; onDropped?: (key: string) => void }) {
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
          <li key={t.key} className={`rounded-lg bg-slate-50 px-3 py-2 ${t.dropped ? 'opacity-60' : ''}`}>
            <div className={`flex items-baseline justify-between gap-2 text-sm ${t.dropped ? 'line-through' : ''}`}>
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
            {onDropped && (
              <label className="mt-1 flex items-center gap-2 text-xs">
                <input type="checkbox" checked={t.dropped} onChange={() => onDropped(t.key)} className="accent-slate-900" />
                I dropped this trip (left out of pay)
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

function MonthCard({ m, busy, act, onUpdate }: { m: ScheduleMonth; busy: boolean; act: Act; onUpdate: (e: ChangeEvent<HTMLInputElement>) => void }) {
  const overtime = m.trips.filter((t) => t.overtime).map((t) => t.key)
  const paid = m.entries.filter((e) => e.paid).map((e) => e.key)
  const dropped = m.trips.filter((t) => t.dropped).map((t) => t.key)
  const [open, setOpen] = useRemembered(`schedule.${m.year}-${m.month}`, true)
  const save = (reserve: boolean, ot: string[], pe: string[], dr: string[] = dropped) =>
    void act(() => paydayApi.setChoices(m.year, m.month, reserve, ot, pe, dr), 'Saved. The forecast is updated.')
  const flip = (list: string[], key: string) => (list.includes(key) ? list.filter((k) => k !== key) : [...list, key])
  return (
    <div className="rounded-xl bg-white p-4 ring-1 ring-slate-200">
      <h3>
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex w-full items-baseline justify-between gap-2 text-left">
          <span className="flex items-center gap-1 font-semibold">
            <Caret open={open} />
            {monthLabel(m.year, m.month)}
          </span>
          <span className="text-xs font-normal text-slate-500">
            {hm(m.creditMinutes)} credit · {m.trips.length - dropped.length} trips · {m.source === 'text' ? 'text export' : 'CSV (estimated)'}
          </span>
        </button>
      </h3>
      {open && (
        <>
      <label className="mt-2 flex items-center gap-2 text-sm">
        <input type="checkbox" checked={m.reserve} disabled={busy} onChange={() => save(!m.reserve, overtime, paid)} className="accent-slate-900" />
        Reserve this month (the 76-hour guarantee, split across the two checks)
      </label>
      <MonthBody
        m={m}
        onOvertime={(k) => save(m.reserve, flip(overtime, k), paid)}
        onPaid={(k) => save(m.reserve, overtime, flip(paid, k))}
        onDropped={(k) => save(m.reserve, overtime, paid, flip(dropped, k))}
      />
      <p className="mt-3 text-xs text-slate-500">Picked up or traded a trip? Upload the updated export for this month: it replaces the month and keeps your choices for trips that are still there.</p>
      <div className="mt-2 flex items-center justify-between gap-3">
        <label className="cursor-pointer rounded-lg bg-slate-100 px-3 py-1.5 text-xs font-semibold text-slate-700">
          Upload an updated schedule
          <input type="file" accept=".txt,.csv,text/plain,text/csv" onChange={onUpdate} className="sr-only" />
        </label>
        <button type="button" disabled={busy} onClick={() => void act(() => paydayApi.removeMonth(m.year, m.month), `Removed ${monthLabel(m.year, m.month)}.`)} className="text-xs font-medium text-red-600">
          Remove this month
        </button>
      </div>
        </>
      )}
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
  const changed =
    d.hireDate !== p.hireDate || d.yearsOfService !== p.yearsOfService || d.fullTime !== p.fullTime || d.holidayPay !== p.holidayPay || d.flatNetPercent !== p.flatNetPercent
  const save = () =>
    act(() => paydayApi.setProfile({ hireDate: d.hireDate, yearsOfService: d.yearsOfService, fullTime: d.fullTime, holidayPay: d.holidayPay, flatNetPercent: d.flatNetPercent }), 'Profile saved.')
  return (
    <Section title="Pay profile" hint="Your hourly rate goes by your years of service since your date of hire.">
      <div className="space-y-2 rounded-xl bg-white p-4 text-sm ring-1 ring-slate-200">
        <label className="flex items-center justify-between gap-2">
          InFlight date of hire
          <input
            type="date"
            aria-label="Date of hire"
            value={d.hireDate ?? ''}
            onChange={(e) => setD({ ...d, hireDate: e.target.value || null })}
            className="rounded-md border border-slate-300 bg-white px-1.5 py-0.5"
          />
        </label>
        <p className="text-xs text-slate-500">
          {p.hireDate
            ? `${p.yearsOfService} completed year${p.yearsOfService === 1 ? '' : 's'}: ${formatCents(p.rateCents)}/hour now.${p.nextRaiseOn && p.nextRaiseRateCents ? ` It goes up to ${formatCents(p.nextRaiseRateCents)} on ${formatDay(p.nextRaiseOn)}, ${p.nextRaiseOn.slice(0, 4)}.` : ''}`
            : `${formatCents(p.rateCents)}/hour now. Set your date of hire and the rate advances on its own each anniversary.`}
        </p>
        {!d.hireDate && (
          <label className="flex items-center justify-between gap-2">
            Or: completed years of service
            <NumberInput label="Years of service" value={d.yearsOfService} step={1} onChange={(v) => setD({ ...d, yearsOfService: Math.max(0, Math.round(v)) })} />
          </label>
        )}
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
          <button type="button" disabled={busy} onClick={() => void save()} className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white">
            Save profile
          </button>
        )}
      </div>
    </Section>
  )
}

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
import { Caret, ErrorNotice, Page, PageHeader, Row, Rows } from './ui'
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
      <PageHeader title={title} back={{ href: back, label: backLabel }} />
      {notice && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
    </>
  )
  const previewCard = preview && (
        <div id="schedule-preview" className="mt-3 scroll-mt-4 neu rounded-3xl p-5 ring-2 ring-accent">
          <p className="text-sm font-semibold">
            {monthLabel(preview.month.year, preview.month.month)}: {preview.month.trips.length} trips, {hm(preview.month.creditMinutes)} credit
            {preview.replaces ? ' (replaces the month you imported before)' : ''}
          </p>
          <p className="text-xs text-ink-soft">Check the trips, then confirm.</p>
          <MonthBody m={preview.month} />
          <div className="mt-3 flex gap-3">
            <button type="button" onClick={() => setPreview(null)} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-danger">
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
              className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 text-sm font-semibold text-primary-ink disabled:opacity-50"
            >
              Confirm import
            </button>
          </div>
        </div>
  )

  if (sub === 'schedules')
    return (
      <Page>
        {header('#/payday/setup', 'Schedule & pay stubs', 'Schedules')}
        <div className="mt-4">
          {previewCard}
          {months.length === 0 ? (
            <p className="text-sm text-ink-soft">No schedules imported yet.</p>
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
      </Page>
    )

  if (sub === 'stubs')
    return (
      <Page>
        {header('#/payday/setup', 'Schedule & pay stubs', 'Pay stubs')}
        {stubs.length === 0 ? (
          <p className="mt-4 text-sm text-ink-soft">No pay stubs uploaded yet.</p>
        ) : (
          <ul className="mt-4 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
            {stubs.map((s) => (
              <li key={s.id} className="flex items-center justify-between gap-3 px-4 py-2.5 text-sm">
                <span>
                  <span className="font-medium">Paid {formatDay(s.payDate)}</span>
                  <span className="block text-xs text-ink-soft">
                    {formatDay(s.periodStart)}–{formatDay(s.periodEnd)} · gross {formatCents(s.grossCents)}
                  </span>
                </span>
                <span className="flex items-center gap-3">
                  <span className="font-semibold tabular-nums">{formatCents(s.netCents)}</span>
                  <button type="button" aria-label={`Remove the stub paid ${formatDay(s.payDate)}`} onClick={() => void act(() => paydayApi.removeStub(s.id), 'Removed.')} className="text-xs text-danger">
                    Remove
                  </button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Page>
    )

  return (
    <Page>
      {header('#/payday', 'Plan', 'Schedule & pay stubs')}

      <Section title="Monthly schedule" hint="SkedPlus+ text export (.txt) or CSV. Re-importing a month replaces it.">
        <FileButton label="Import a schedule" accept=".txt,.csv,text/plain,text/csv" onChange={pickSchedule} />
        {previewCard}
        <SubLink href="#/payday/setup/schedules" title="View schedules" detail={months.length === 0 ? 'None imported yet' : `${months.length} ${months.length === 1 ? 'month' : 'months'}: ${months.map((m) => monthLabel(m.year, m.month).split(' ')[0]).join(', ')}`} />
      </Section>

      <Section title="Pay stubs" hint="Pay advice PDFs: 2 at least, 6 is better. You check the numbers first; the file isn’t kept.">
        <FileButton label="Upload a pay stub" accept=".pdf,application/pdf" onChange={pickStub} />
        {review && <StubReviewCard r={review} busy={busy} onCancel={() => setReview(null)} onSave={() => void act(async () => {
          const saved = await paydayApi.confirmStub(review.stub)
          setReview(null)
          if (saved.varianceCents !== null) setNotice(`Saved. The app had predicted ${formatCents(saved.predictedNetCents ?? 0)}; it was ${formatCents(saved.actualNetCents)}.`)
        }, 'Saved. Your forecast learned from it.')} />}
        <SubLink href="#/payday/setup/stubs" title="View pay stubs" detail={stubs.length === 0 ? 'None uploaded yet' : `${stubs.length} ${stubs.length === 1 ? 'stub' : 'stubs'}, latest paid ${formatDay(stubs.reduce((a, b) => (a.payDate > b.payDate ? a : b)).payDate)}`} />
      </Section>

      {profile && <ProfileSection p={profile} busy={busy} act={act} />}
    </Page>
  )
}

/** "View schedules" and "View pay stubs" (DECISIONS.md 184): a row that opens the list on its own page. */
function SubLink({ href, title, detail }: { href: string; title: string; detail: string }) {
  return (
    <div className="neu mt-3 rounded-3xl">
      <Rows>
        <Row label={<span className="font-semibold">{title}</span>} hint={detail} href={href} />
      </Rows>
    </div>
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
      <h2 className="px-1 text-sm font-semibold text-ink-soft">{title}</h2>
      {hint && <p className="px-1 text-xs text-ink-faint">{hint}</p>}
      <div className="mt-2">{children}</div>
    </section>
  )
}

function FileButton({ label, accept, onChange }: { label: string; accept: string; onChange: (e: ChangeEvent<HTMLInputElement>) => void }) {
  return (
    <label className="block cursor-pointer rounded-2xl neu py-3 text-center text-sm font-medium focus-within:ring-2 focus-within:ring-accent">
      {label}
      <input type="file" accept={accept} onChange={onChange} className="sr-only" />
    </label>
  )
}

function MonthBody({ m, onOvertime, onPaid, onDropped }: { m: ScheduleMonth; onOvertime?: (key: string) => void; onPaid?: (key: string) => void; onDropped?: (key: string) => void }) {
  return (
    <>
      {m.shortfallMinutes > 0 && (
        <p className="mt-2 text-xs text-ink-soft">
          {hm(m.shortfallMinutes)} below the line guarantee: no top-up is assumed, so the checks are lighter (information only).
        </p>
      )}
      {m.overrideMinutes > 0 && <p className="mt-2 text-xs text-good">{hm(m.overrideMinutes)} above 87 hours: the override is forecast (an estimate).</p>}
      <ul className="mt-2 space-y-2">
        {m.trips.map((t) => (
          <li key={t.key} className={`rounded-lg neu-inset px-3 py-2 ${t.dropped ? 'opacity-60' : ''}`}>
            <div className={`flex items-baseline justify-between gap-2 text-sm ${t.dropped ? 'line-through' : ''}`}>
              <span className="font-medium">
                {t.code} · {formatDay(t.start)}–{formatDay(t.end)}
              </span>
              <span className="font-semibold tabular-nums">{formatCents(t.payCents)}</span>
            </div>
            <p className="text-xs text-ink-soft">
              {hm(t.creditMinutes)} credit · {hm(t.tafbMinutes)} away · {t.revenueLegs} boarded legs · credit {formatCents(t.creditPayCents)}, boarding {formatCents(t.boardingCents)}, per diem{' '}
              {formatCents(t.perDiemCents)}
            </p>
            <p className="text-xs text-ink-faint">{t.days.map((d) => `${formatDay(d.date)} ${d.route} (${hm(d.creditMinutes)})`).join(' · ')}</p>
            {onOvertime && (
              <label className="mt-1 flex items-center gap-2 text-xs">
                <input type="checkbox" checked={t.overtime} onChange={() => onOvertime(t.key)} className="accent-accent" />
                Flown as overtime (1.5×)
              </label>
            )}
            {onDropped && (
              <label className="mt-1 flex items-center gap-2 text-xs">
                <input type="checkbox" checked={t.dropped} onChange={() => onDropped(t.key)} className="accent-accent" />
                I dropped this trip (left out of pay)
              </label>
            )}
          </li>
        ))}
        {m.entries.map((e) => (
          <li key={e.key} className="rounded-2xl neu-inset px-4 py-3 text-sm">
            <span className="font-medium">{e.code}</span> · {formatDay(e.start.slice(0, 10))}–{formatDay(e.end.slice(0, 10))} · {hm(e.creditMinutes)} credit
            {onPaid ? (
              <label className="mt-1 flex items-center gap-2 text-xs">
                <input type="checkbox" checked={e.paid} onChange={() => onPaid(e.key)} className="accent-accent" />
                This is paid (treated as unpaid unless you switch it on)
              </label>
            ) : (
              <span className="block text-xs text-ink-soft">Treated as unpaid.</span>
            )}
          </li>
        ))}
      </ul>
      {m.warnings.length > 0 && (
        <ul className="mt-2 space-y-1">
          {m.warnings.map((w) => (
            <li key={w} className="text-xs text-gold-ink">
              {w}
            </li>
          ))}
        </ul>
      )}
      <details className="mt-2 text-xs text-ink-soft">
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
    <div className="rounded-2xl neu p-4">
      <h3>
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex w-full items-baseline justify-between gap-2 text-left">
          <span className="flex items-center gap-1 font-semibold">
            <Caret open={open} />
            {monthLabel(m.year, m.month)}
          </span>
          <span className="text-xs font-normal text-ink-soft">
            {hm(m.creditMinutes)} credit · {m.trips.length - dropped.length} trips · {m.source === 'text' ? 'text export' : 'CSV (estimated)'}
          </span>
        </button>
      </h3>
      {open && (
        <>
      <label className="mt-2 flex items-center gap-2 text-sm">
        <input type="checkbox" checked={m.reserve} disabled={busy} onChange={() => save(!m.reserve, overtime, paid)} className="accent-accent" />
        Reserve this month (the 76-hour guarantee, split across the two checks)
      </label>
      <MonthBody
        m={m}
        onOvertime={(k) => save(m.reserve, flip(overtime, k), paid)}
        onPaid={(k) => save(m.reserve, overtime, flip(paid, k))}
        onDropped={(k) => save(m.reserve, overtime, paid, flip(dropped, k))}
      />
      <p className="mt-3 text-xs text-ink-soft">Picked up or traded a trip? Upload the updated export for this month: it replaces the month and keeps your choices for trips that are still there.</p>
      <div className="mt-2 flex items-center justify-between gap-3">
        <label className="cursor-pointer rounded-lg neu-sm px-3 py-1.5 text-xs font-semibold text-ink-soft">
          Upload an updated schedule
          <input type="file" accept=".txt,.csv,text/plain,text/csv" onChange={onUpdate} className="sr-only" />
        </label>
        <button type="button" disabled={busy} onClick={() => void act(() => paydayApi.removeMonth(m.year, m.month), `Removed ${monthLabel(m.year, m.month)}.`)} className="text-xs font-medium text-danger">
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
    <div className="mt-3 neu rounded-3xl p-5 ring-2 ring-accent">
      <p className="text-sm font-semibold">
        Paid {formatDay(s.payDate)} for {formatDay(s.periodStart)}–{formatDay(s.periodEnd)}
        {r.replaces ? ' (replaces the stub you saved for this date)' : ''}
      </p>
      <p className="text-xs text-ink-soft">Check these numbers against your stub before saving. Only the numbers are kept.</p>
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
        <p className="mt-1 text-xs text-ink-soft">
          Earned this period: {s.userHoursEarned ?? 0} user and {s.vacationHoursEarned ?? 0} vacation hours.
        </p>
      )}
      <ul className="mt-2 space-y-0.5 text-xs">
        {r.checks.map((c) => (
          <li key={c} className="text-good">
            ✓ {c}
          </li>
        ))}
        {r.problems.map((p) => (
          <li key={p} className="text-danger">
            ✗ {p}
          </li>
        ))}
      </ul>
      <div className="mt-3 flex gap-3">
        <button type="button" onClick={onCancel} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-danger">
          Cancel
        </button>
        <button type="button" disabled={busy || r.problems.length > 0} onClick={onSave} className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 text-sm font-semibold text-primary-ink disabled:opacity-40">
          Save stub
        </button>
      </div>
    </div>
  )
}

function Line({ label, cents, strong, muted }: { label: string; cents: number; strong?: boolean; muted?: boolean }) {
  return (
    <div className={`flex justify-between gap-3 ${strong ? 'border-t border-ink/10 pt-0.5 font-semibold' : ''} ${muted ? 'text-ink-soft' : ''}`}>
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
      className="w-20 rounded-md neu-inset px-1.5 py-0.5 text-right tabular-nums"
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
    <Section title="Pay profile" hint="Your rate follows your years of service.">
      <div className="space-y-2 rounded-2xl neu p-4 text-sm">
        <label className="flex items-center justify-between gap-2">
          InFlight date of hire
          <input
            type="date"
            aria-label="Date of hire"
            value={d.hireDate ?? ''}
            onChange={(e) => setD({ ...d, hireDate: e.target.value || null })}
            className="rounded-md neu-inset px-1.5 py-0.5"
          />
        </label>
        <p className="text-xs text-ink-soft">
          {p.hireDate
            ? `${p.yearsOfService} completed year${p.yearsOfService === 1 ? '' : 's'}: ${formatCents(p.rateCents)}/hour now.${p.nextRaiseOn && p.nextRaiseRateCents ? ` It goes up to ${formatCents(p.nextRaiseRateCents)} on ${formatDay(p.nextRaiseOn)}, ${p.nextRaiseOn.slice(0, 4)}.` : ''}`
            : `${formatCents(p.rateCents)}/hour now. Add your date of hire and it rises each anniversary.`}
        </p>
        {!d.hireDate && (
          <label className="flex items-center justify-between gap-2">
            Or: completed years of service
            <NumberInput label="Years of service" value={d.yearsOfService} step={1} onChange={(v) => setD({ ...d, yearsOfService: Math.max(0, Math.round(v)) })} />
          </label>
        )}
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={d.fullTime} onChange={() => setD({ ...d, fullTime: !d.fullTime })} className="accent-accent" />
          Full-time (80-hour line guarantee; part-time is 40)
        </label>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={d.holidayPay} onChange={() => setD({ ...d, holidayPay: !d.holidayPay })} className="accent-accent" />
          Eligible for holiday pay
        </label>
        <label className="flex items-center justify-between gap-2">
          Take-home with fewer than 2 stubs (% of gross)
          <NumberInput label="Flat take-home percent" value={d.flatNetPercent} step={1} onChange={(v) => setD({ ...d, flatNetPercent: Math.round(v) })} />
        </label>
        {changed && (
          <button type="button" disabled={busy} onClick={() => void save()} className="rounded-lg bg-primary shadow-neu-sm px-3 py-1.5 text-xs font-semibold text-primary-ink">
            Save profile
          </button>
        )}
      </div>
    </Section>
  )
}

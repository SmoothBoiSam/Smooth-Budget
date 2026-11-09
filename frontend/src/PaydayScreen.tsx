import { useCallback, useEffect, useState } from 'react'
import { ApiError, debtApi, openPlanItems, paydayApi, savingsApi, type DebtSummary, type PayCheck, type PaydaySummary, type PlanItem, type SavingsSummary } from './api'
import { icons } from './icons'
import { useRemembered } from './offline'
import { Caret, Card, ErrorNotice, Loading, Page, PageHeader, Row as MenuRow, Rows } from './ui'
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
 * The Plan tab (SPEC 4.6; DECISIONS.md 147), with links to Debt and Savings under the checks: the next checks with a low/likely/high range, the payday plan for each, and predicted vs
 * actual once a deposit lands. Setup (schedule, pay stubs, profile, rules) is on its own screen.
 */
export default function PaydayScreen({ onSignedOut, onOpenItems }: { onSignedOut: () => void; onOpenItems: (n: number) => void }) {
  const [s, setS] = useState<PaydaySummary | null>(null)
  const [debt, setDebt] = useState<DebtSummary | null>(null)
  const [savings, setSavings] = useState<SavingsSummary | null>(null)
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
      // Debt and Savings are extras here: the plan still shows without them.
      const [p, d, sv] = await Promise.all([paydayApi.summary(), debtApi.summary().catch(() => null), savingsApi.summary().catch(() => null)])
      setS(p)
      onOpenItems(openPlanItems(p))
      setDebt(d)
      setSavings(sv)
      setError(null)
    } catch (e) {
      fail(e)
    }
  }, [fail, onOpenItems])
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
  const [historyOpen, setHistoryOpen] = useRemembered('payday.history', true)
  const [doneOpen, setDoneOpen] = useState(false)

  return (
    <Page>
      <PageHeader title="Plan" />
      <DebtSavingsLinks debt={debt} savings={savings} />
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!s && !error && <Loading />}

      {empty && (
        <section className="mt-6 rounded-3xl neu p-5">
          <h2 className="font-semibold">Forecast your paychecks</h2>
          <p className="mt-1 text-sm text-ink-soft">Import your SkedPlus+ schedule and 2 or more pay stubs.</p>
          <a href="#/payday/setup" className="mt-4 block rounded-2xl bg-coral shadow-neu-sm py-3 text-center text-sm font-semibold text-coral-ink">
            Import a schedule or pay stub
          </a>
        </section>
      )}

      {s && !empty && (
        <>
          <ul className="mt-6 space-y-4">
            {s.checks
              .filter((c) => !finished(c))
              .map((c) => (
                <li key={c.payDate}>
                  <CheckCard c={c} onToggle={(item) => void toggle(c, item)} />
                </li>
              ))}
          </ul>


          {/* A check whose plan is all done is put away (DECISIONS.md 154), one tap from coming back. */}
          {s.checks.some(finished) && (
            <section aria-labelledby="done-h" className="mt-6">
              <h2 id="done-h">
                <button type="button" onClick={() => setDoneOpen(!doneOpen)} aria-expanded={doneOpen} className="flex items-center gap-1 px-1 text-sm font-semibold text-ink-soft">
                  <Caret open={doneOpen} />
                  Finished paychecks
                </button>
              </h2>
              {doneOpen && (
                <ul className="mt-2 space-y-3">
                  {s.checks.filter(finished).map((c) => (
                    <li key={c.payDate}>
                      <CheckCard c={c} onToggle={(item) => void toggle(c, item)} />
                    </li>
                  ))}
                </ul>
              )}
            </section>
          )}

          {s.history.length > 0 && (
            <section aria-labelledby="hist-h" className="mt-6">
              <h2 id="hist-h">
                <button type="button" onClick={() => setHistoryOpen(!historyOpen)} aria-expanded={historyOpen} className="flex items-center gap-1 px-1 text-sm font-semibold text-ink-soft">
                  <Caret open={historyOpen} />
                  Predicted vs actual
                </button>
              </h2>
              {historyOpen && (
              <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
                {s.history.map((h) => (
                  <li key={h.payDate} className="flex items-center justify-between gap-3 px-4 py-2.5 text-sm">
                    <span>
                      <span className="font-medium">{formatDay(h.payDate)}</span>
                      <span className="block text-xs text-ink-soft">
                        Predicted {formatCents(h.predictedNetCents)}
                        {h.source === 'stub' ? ' · from your stub' : h.source === 'deposit' ? ' · deposit' : ''}
                      </span>
                    </span>
                    {h.actualNetCents === null ? (
                      <span className="text-xs text-ink-soft">Not seen yet</span>
                    ) : (
                      <span className="text-right tabular-nums">
                        <span className="font-semibold">{formatCents(h.actualNetCents)}</span>
                        <span className={`block text-xs ${Math.abs(h.varianceCents ?? 0) < 1_000 ? 'text-ink-soft' : (h.varianceCents ?? 0) > 0 ? 'text-good' : 'text-gold-ink'}`}>
                          {(h.varianceCents ?? 0) >= 0 ? '+' : '−'}
                          {formatCents(Math.abs(h.varianceCents ?? 0))}
                        </span>
                      </span>
                    )}
                  </li>
                ))}
              </ul>
              )}
            </section>
          )}

          <p className="mt-6 px-1 text-xs text-ink-faint">
            Estimates from your schedule, pay rules and {s.stubCount} pay stub{s.stubCount === 1 ? '' : 's'}
            {s.modelEdited ? ' (with your edits)' : ''}. Irregular payouts are left out.
          </p>
        </>
      )}
    </Page>
  )
}

/** A paid check whose every plan item is done. */
const finished = (c: PayCheck) => c.isCurrent && !!c.plan && c.plan.items.length > 0 && c.plan.items.every((i) => i.done)

/** Debt, Savings, and Schedule & pay stubs, one tap away under the plan (they used to be tabs of their own). */
function DebtSavingsLinks({ debt, savings }: { debt: DebtSummary | null; savings: SavingsSummary | null }) {
  const debtLine = !debt
    ? 'Cards, loans and your payoff plan'
    : debt.debts.length === 0
      ? 'No cards or loans yet'
      : `${formatCents(debt.owedCents)} owed${debt.plan?.debtFreeBy ? ` · debt-free by ${monthYear(debt.plan.debtFreeBy)}` : ''}`
  const savingsLine = !savings
    ? 'Goals and what you’ve saved'
    : `${formatCents(savings.totalSavedCents)} saved${savings.thisMonthCents > 0 ? ` · ${formatCents(savings.thisMonthCents)} moved in this month` : ''}`
  // Everything else on Plan, in one menu: debt, savings, and where the forecast comes from (DECISIONS.md 187).
  return (
    <Card>
      <Rows>
        <MenuRow icon={icons.debt} label={<span className="font-semibold">Debt</span>} hint={debtLine} href="#/debt" />
        <MenuRow icon={icons.savings} label={<span className="font-semibold">Savings</span>} hint={savingsLine} href="#/savings" />
        <MenuRow icon={icons.payday} label={<span className="font-semibold">Schedule & pay stubs</span>} hint="What the forecast learns from" href="#/payday/setup" />
      </Rows>
    </Card>
  )
}

const monthYear = (iso: string) => {
  const [y, m] = iso.split('-').map(Number)
  return new Date(y, m - 1, 1).toLocaleDateString('en-US', { month: 'short', year: 'numeric' })
}

function CheckCard({ c, onToggle }: { c: PayCheck; onToggle: (item: PlanItem) => void }) {
  const [open, setOpen] = useState(false)
  const [planOpen, setPlanOpen] = useState(c.isCurrent)
  // Future checks start folded to one line (DECISIONS.md 154); the one you're on starts open.
  const [folded, setFolded] = useState(!c.isCurrent || finished(c))
  const known = c.hasSchedule || c.landedNetCents !== null
  if (folded)
    return (
      <button
        type="button"
        onClick={() => setFolded(false)}
        aria-expanded={false}
        className="flex w-full items-center gap-3 rounded-3xl neu px-4 py-3 text-left"
      >
        <span className="min-w-0 flex-1">
          <span className="block font-semibold">{c.isCurrent ? `Paid ${weekday(c.payDate)}` : weekday(c.payDate)}</span>
          <span className="block truncate text-xs text-ink-soft">
            {finished(c)
              ? `All ${c.plan!.items.length} done`
              : known
                ? `Covers ${formatDay(c.periodStart)}–${formatDay(c.periodEnd)}`
                : `Import your ${monthName(c.periodStart)} schedule to forecast it`}
          </span>
        </span>
        {known && <span className="shrink-0 font-semibold tabular-nums">{c.landedNetCents === null && 'about '}{formatCents(c.landedNetCents ?? c.netCents)}</span>}
        <Caret open={false} />
      </button>
    )
  return (
    <article className="animate-rise-in rounded-3xl neu p-4">
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="flex flex-wrap items-center gap-x-2 gap-y-1 font-semibold">
          {c.isCurrent ? `Paid ${weekday(c.payDate)}` : weekday(c.payDate)}
          {c.isCurrent && <span className="whitespace-nowrap rounded-full neu-inset px-2 py-0.5 text-xs font-medium text-good">You're on this check</span>}
        </h2>
        <span className="shrink-0 text-xs text-ink-soft">
          Covers {formatDay(c.periodStart)}–{formatDay(c.periodEnd)}
          {(!c.isCurrent || finished(c)) && (
            <button type="button" onClick={() => setFolded(true)} className="ml-2 font-medium text-coral">
              Fold
            </button>
          )}
        </span>
      </div>
      {!c.hasSchedule && c.landedNetCents === null ? (
        <p className="mt-2 text-sm text-ink-soft">
          Import your {monthName(c.periodStart)} schedule to forecast this check.{' '}
          <a href="#/payday/setup" className="font-medium text-coral">
            Import
          </a>
        </p>
      ) : (
        <>
          <p className="mt-1 text-3xl font-bold tabular-nums tracking-tight">{formatCents(c.landedNetCents ?? c.netCents)}</p>
          <p className="text-sm text-ink-soft">
            {c.landedNetCents !== null
              ? `Landed${c.hasSchedule ? ` · forecast was ${formatCents(c.netCents)}` : ''}`
              : `Likely take-home · range ${formatCents(c.lowNetCents)}–${formatCents(c.highNetCents)} · gross ${formatCents(c.grossCents)}`}
          </p>
          {c.lowConfidence && c.landedNetCents === null && (
            <p className="mt-2 rounded-lg neu-inset px-3 py-2 text-xs text-gold-ink">
              <span className="font-semibold">Low confidence.</span> {c.reasons.join(' ')}
            </p>
          )}
          {c.notes.map((n) => (
            <p key={n} className="mt-2 text-xs text-ink-soft">
              {n}
            </p>
          ))}
          {c.hasSchedule && (
          <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="mt-2 text-xs font-medium text-coral">
            {open ? 'Hide' : 'How this is figured'}
          </button>
          )}
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
            <section className="mt-3 border-t border-ink/10 pt-3">
              <button type="button" onClick={() => setPlanOpen(!planOpen)} aria-expanded={planOpen} className="flex w-full items-center justify-between text-sm font-semibold">
                Payday plan
                <span className="text-xs font-normal text-ink-soft">
                  {c.plan.items.filter((i) => i.done).length} of {c.plan.items.length} done
                </span>
              </button>
              {planOpen && (
                <>
                  {c.plan.shortCents > 0 && (
                    <p className="mt-2 rounded-lg neu-inset px-3 py-2 text-xs text-danger">
                      This check is {formatCents(c.plan.shortCents)} short of everything planned for it.
                    </p>
                  )}
                  <ul className="mt-2 space-y-1">
                    {c.plan.items.map((item) => (
                      <li key={item.key}>
                        <PlanRow item={item} onToggle={() => onToggle(item)} />
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

/** One part of the payday plan: check it off, or it's done on its own from your accounts. */
function PlanRow({ item, onToggle }: { item: PlanItem; onToggle: () => void }) {
  const [billsOpen, setBillsOpen] = useState(false)
  const body = (
    <span className="min-w-0 flex-1">
      <span className={`block text-sm ${item.done ? 'text-ink-faint line-through' : ''}`}>{planText(item)}</span>
      {item.status && <span className={`block text-xs ${item.done ? 'text-good' : 'text-gold-ink'}`}>{item.status}</span>}
      <span className="block text-xs text-ink-soft">{item.why}</span>
    </span>
  )
  return (
    <div className="px-1 py-1.5">
      <label className="flex cursor-pointer items-start gap-3">
        <input
          type="checkbox"
          checked={item.done}
          disabled={item.auto}
          onChange={onToggle}
          aria-label={item.auto ? `${planText(item)}: done, from your accounts` : planText(item)}
          className="mt-1 size-4 shrink-0 accent-coral"
        />
        {body}
      </label>
      {item.bills && item.bills.length > 0 && (
        <div className="ml-7">
          <button type="button" onClick={() => setBillsOpen(!billsOpen)} aria-expanded={billsOpen} className="flex items-center gap-1 text-xs font-medium text-coral">
            <Caret open={billsOpen} />
            {billsOpen ? 'Hide the bills' : 'Show the bills'}
          </button>
          {billsOpen && (
            <ul className="mt-1 divide-y divide-ink/[0.07] text-xs">
              {item.bills.map((b) => (
                <li key={b.name + b.due} className="flex justify-between gap-3 py-1">
                  <span>
                    {b.name} <span className="text-ink-faint">· {formatDay(b.due)}</span>
                  </span>
                  <span className="tabular-nums">{formatCents(b.cents)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}

function planText(i: PlanItem) {
  const amount = formatCents(i.cents)
  switch (i.kind) {
    case 'keep':
      return `Keep ${amount} in ${i.destination} for ${i.label}`
    case 'cardspend':
      // What you charged to a card: paid from the spending money left in checking, not from this check.
      return `Pay ${amount} to ${i.destination} for what you charged${i.label === 'Charged so far' ? ' so far' : ''}`
    default:
      return `Send ${amount} to ${i.destination} (${i.label.toLowerCase()})`
  }
}

function Row({ label, cents, note, strong }: { label: string; cents: number; note?: string; strong?: boolean }) {
  return (
    <div className={strong ? 'border-t border-ink/10 pt-1 font-semibold' : ''}>
      <div className="flex justify-between gap-3">
        <dt>{label}</dt>
        <dd className="tabular-nums">{cents < 0 ? `−${formatCents(-cents)}` : formatCents(cents)}</dd>
      </div>
      {note && <p className="text-xs text-ink-soft">{note}</p>}
    </div>
  )
}

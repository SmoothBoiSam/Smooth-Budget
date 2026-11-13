import { useCallback, useEffect, useState } from 'react'
import { ApiError, debtApi, payBadge, paydayApi, savingsApi, type DebtSummary, type PayCheck, type PaydaySummary, type SavingsSummary } from './api'
import { icons } from './icons'
import { useRemembered, useResume } from './offline'
import { Card, Caret, Chevron, ErrorNotice, Loading, Page, PageHeader, Row as MenuRow, Rows } from './ui'
import { formatCents, formatDay } from './format'
import { finished, weekday } from './paycheck'

const monthName = (iso: string) => {
  const [y, m] = iso.split('-').map(Number)
  return new Date(y, m - 1, 1).toLocaleDateString('en-US', { month: 'long' })
}

/**
 * The Plan tab (SPEC 4.6; DECISIONS.md 147, 249): one menu for Debt, Savings, and Schedule & pay stubs, which holds your
 * paychecks. The payday plan for each check is checked off under Tasks (DECISIONS.md 206).
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
      onOpenItems(payBadge(p))
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
  useResume(load)

  return (
    <Page>
      <PageHeader title="Plan" />
      <DebtSavingsLinks debt={debt} savings={savings} payday={s} />
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!s && !error && <Loading />}
    </Page>
  )
}

/**
 * Your paychecks (DECISIONS.md 249), on the Schedule & pay stubs page they're forecast from: the next checks with a
 * low/likely/high range, finished ones put away, and predicted vs actual once a deposit lands.
 */
export function Paychecks({ s }: { s: PaydaySummary }) {
  const empty = s.scheduleMonths.length === 0 && s.stubCount === 0
  const [historyOpen, setHistoryOpen] = useRemembered('payday.history', true)
  const [doneOpen, setDoneOpen] = useState(false)

  if (empty)
    return (
      <p className="rounded-2xl neu-inset px-4 py-3 text-sm text-ink-soft">
        Import your SkedPlus+ schedule and 2 or more pay stubs below, and your paychecks show up here.
      </p>
    )

  return (
        <>
          <ul className="space-y-4">
            {s.checks
              .filter((c) => !finished(c))
              .map((c) => (
                <li key={c.payDate}>
                  <CheckCard c={c} />
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
                      <CheckCard c={c} />
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
  )
}

/** Debt, Savings, and Schedule & pay stubs, one tap away under the plan (they used to be tabs of their own). */
function DebtSavingsLinks({ debt, savings, payday }: { debt: DebtSummary | null; savings: SavingsSummary | null; payday: PaydaySummary | null }) {
  // The check you're on, or the next one: what the paychecks row leads with (DECISIONS.md 249).
  const next = payday?.checks.find((c) => !finished(c) && (c.hasSchedule || c.landedNetCents !== null))
  const payLine = !payday
    ? 'Your paychecks, schedule and pay stubs'
    : payday.scheduleMonths.length === 0 && payday.stubCount === 0
      ? 'Import a schedule and pay stubs to forecast your pay'
      : next
        ? `${next.isCurrent ? 'Paid' : 'Next check'} ${weekday(next.payDate)} · ${next.landedNetCents === null ? 'about ' : ''}${formatCents(next.landedNetCents ?? next.netCents)}`
        : 'Your paychecks, schedule and pay stubs'
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
        <MenuRow icon={icons.payday} label={<span className="font-semibold">Schedule & pay stubs</span>} hint={payLine} href="#/payday/setup" />
      </Rows>
    </Card>
  )
}

const monthYear = (iso: string) => {
  const [y, m] = iso.split('-').map(Number)
  return new Date(y, m - 1, 1).toLocaleDateString('en-US', { month: 'short', year: 'numeric' })
}

function CheckCard({ c }: { c: PayCheck }) {
  const [open, setOpen] = useState(false)
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
          {c.isCurrent && <span className="whitespace-nowrap rounded-full bg-ink/[0.06] px-2 py-0.5 text-xs font-medium text-good">You're on this check</span>}
        </h2>
        <span className="shrink-0 text-xs text-ink-soft">
          Covers {formatDay(c.periodStart)}–{formatDay(c.periodEnd)}
          {(!c.isCurrent || finished(c)) && (
            <button type="button" onClick={() => setFolded(true)} className="ml-2 font-medium text-accent">
              Fold
            </button>
          )}
        </span>
      </div>
      {!c.hasSchedule && c.landedNetCents === null ? (
        <p className="mt-2 text-sm text-ink-soft">
          Import your {monthName(c.periodStart)} schedule to forecast this check.{' '}
          <a href="#/payday/setup" className="font-medium text-accent">
            Import
          </a>
        </p>
      ) : (
        <>
          <p className="mt-1 text-3xl font-bold tabular-nums tracking-tight">{formatCents(c.landedNetCents ?? c.netCents)}</p>
          <p className="text-sm text-ink-soft">
            {c.landedNetCents !== null
              ? `Landed${c.hasSchedule ? ` · forecast was ${formatCents(c.netCents)}` : ''}`
              : `Likely take-home${c.lowNetCents === c.highNetCents ? '' : ` · range ${formatCents(c.lowNetCents)}–${formatCents(c.highNetCents)}`} · gross ${formatCents(c.grossCents)}`}
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
          <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="mt-2 text-xs font-medium text-accent">
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

          {/* The steps for this check are checked off under Tasks (DECISIONS.md 206). */}
          {c.plan && c.plan.items.length > 0 && (
            <a href="#/tasks/payday" className="mt-3 flex items-center justify-between gap-3 border-t border-ink/10 pt-3 text-sm">
              <span className="font-semibold">Payday plan</span>
              <span className="flex items-center gap-1 text-xs text-ink-soft">
                {c.plan.items.filter((i) => i.done || i.skipped).length} of {c.plan.items.length} done in Tasks
                <Chevron />
              </span>
            </a>
          )}
        </>
      )}
    </article>
  )
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

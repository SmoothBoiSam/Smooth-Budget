import { useCallback, useEffect, useState } from 'react'
import { ApiError, openPlanItems, paydayApi, type PayCheck, type PaydaySummary, type PlanItem } from './api'
import { Caret, ErrorNotice, Loading, Page, PageHeader, smallBtn, smallBtnOn } from './ui'
import { formatCents, formatDay } from './format'
import { finished, weekday } from './paycheck'

const hasPlan = (c: PayCheck) => !!c.plan && c.plan.items.length > 0

/**
 * Pay period tasks, under Tasks (DECISIONS.md 206): the payday plan for each check as a checklist. The check you're on
 * is open; later ones start folded; a finished one is put away (DECISIONS.md 154). The forecast itself stays on Plan.
 */
export default function PayTasksScreen({ onSignedOut, onOpenItems }: { onSignedOut: () => void; onOpenItems: (n: number) => void }) {
  const [s, setS] = useState<PaydaySummary | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [doneOpen, setDoneOpen] = useState(false)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )
  const load = useCallback(async () => {
    try {
      const p = await paydayApi.summary()
      setS(p)
      onOpenItems(openPlanItems(p))
      setError(null)
    } catch (e) {
      fail(e)
    }
  }, [fail, onOpenItems])
  useEffect(() => {
    paydayApi
      .summary()
      .then((p) => {
        setS(p)
        onOpenItems(openPlanItems(p))
      })
      .catch(fail)
  }, [fail, onOpenItems])

  async function toggle(check: PayCheck, item: PlanItem, skip?: boolean) {
    try {
      if (skip !== undefined) await paydayApi.setSkipped(check.payDate, item.key, skip)
      else await paydayApi.setDone(check.payDate, item.key, !item.done)
      await load()
    } catch (e) {
      fail(e)
    }
  }

  const checks = s?.checks.filter(hasPlan) ?? []
  const open = checks.filter((c) => !finished(c))
  const done = checks.filter(finished)

  return (
    <Page>
      <PageHeader title="Pay period tasks" back={{ href: '#/tasks', label: 'Tasks' }} />
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!s && !error && <Loading />}

      {s && checks.length === 0 && (
        <p className="mt-6 rounded-2xl neu-inset px-4 py-3 text-sm text-ink-soft">
          Nothing to do yet. Once Plan forecasts your next check, the steps for it show up here.{' '}
          <a href="#/payday" className="font-medium text-accent">
            Open Plan
          </a>
        </p>
      )}

      {open.length > 0 && (
        <ul className="mt-6 space-y-4">
          {open.map((c) => (
            <li key={c.payDate}>
              <TaskCard c={c} onToggle={(item, skip) => void toggle(c, item, skip)} />
            </li>
          ))}
        </ul>
      )}

      {done.length > 0 && (
        <section aria-labelledby="done-h" className="mt-6">
          <h2 id="done-h">
            <button type="button" onClick={() => setDoneOpen(!doneOpen)} aria-expanded={doneOpen} className="flex items-center gap-1 px-1 text-sm font-semibold text-ink-soft">
              <Caret open={doneOpen} />
              Finished paychecks
            </button>
          </h2>
          {doneOpen && (
            <ul className="mt-2 space-y-3">
              {done.map((c) => (
                <li key={c.payDate}>
                  <TaskCard c={c} onToggle={(item, skip) => void toggle(c, item, skip)} />
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </Page>
  )
}

function TaskCard({ c, onToggle }: { c: PayCheck; onToggle: (item: PlanItem, skip?: boolean) => void }) {
  const [folded, setFolded] = useState(!c.isCurrent)
  const plan = c.plan!
  const doneCount = plan.items.filter((i) => i.done || i.skipped).length
  const title = c.isCurrent ? `Paid ${weekday(c.payDate)}` : weekday(c.payDate)
  const amount = `${c.landedNetCents === null ? 'about ' : ''}${formatCents(c.landedNetCents ?? c.netCents)}`

  if (folded)
    return (
      <button type="button" onClick={() => setFolded(false)} aria-expanded={false} className="flex w-full items-center gap-3 rounded-3xl neu px-4 py-3 text-left">
        <span className="min-w-0 flex-1">
          <span className="block font-semibold">{title}</span>
          <span className="block truncate text-xs text-ink-soft">
            {doneCount} of {plan.items.length} done · {amount}
          </span>
        </span>
        <Caret open={false} />
      </button>
    )

  return (
    <article className="animate-rise-in rounded-3xl neu p-4">
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="flex flex-wrap items-center gap-x-2 gap-y-1 font-semibold">
          {title}
          {c.isCurrent && <span className="whitespace-nowrap rounded-full bg-ink/[0.06] px-2 py-0.5 text-xs font-medium text-good">You're on this check</span>}
        </h2>
        <span className="shrink-0 text-xs text-ink-soft">
          {doneCount} of {plan.items.length} done
          {!c.isCurrent && (
            <button type="button" onClick={() => setFolded(true)} className="ml-2 font-medium text-accent">
              Fold
            </button>
          )}
        </span>
      </div>
      <p className="text-xs text-ink-soft">
        {amount} · covers {formatDay(c.periodStart)}–{formatDay(c.periodEnd)}
      </p>
      {plan.shortCents > 0 && (
        <p className="mt-2 rounded-lg neu-inset px-3 py-2 text-xs text-danger">This check is {formatCents(plan.shortCents)} short of everything planned for it.</p>
      )}
      <ul className="mt-2 space-y-1">
        {plan.items.map((item) => (
          <li key={item.key}>
            <PlanRow item={item} onToggle={() => onToggle(item)} onSkip={(skip) => onToggle(item, skip)} />
          </li>
        ))}
      </ul>
    </article>
  )
}

/** One part of the payday plan: check it off, or it's done on its own from your accounts. */
function PlanRow({ item, onToggle, onSkip }: { item: PlanItem; onToggle: () => void; onSkip: (skip: boolean) => void }) {
  const [billsOpen, setBillsOpen] = useState(false)
  const body = (
    <span className="min-w-0 flex-1">
      <span className={`block text-sm ${item.done || item.skipped ? 'text-ink-faint line-through' : ''}`}>{planText(item)}</span>
      {item.status && <span className={`block text-xs ${item.done ? 'text-good' : item.skipped ? 'text-ink-soft' : 'text-gold-ink'}`}>{item.status}</span>}
      <span className="block text-xs text-ink-soft">{item.why}</span>
    </span>
  )
  return (
    <div className="px-1 py-1.5">
      <label className="flex cursor-pointer items-start gap-3">
        <input
          type="checkbox"
          checked={item.done}
          disabled={item.auto || item.skipped}
          onChange={onToggle}
          aria-label={item.auto ? `${planText(item)}: done, from your accounts` : planText(item)}
          className="mt-1 size-4 shrink-0 accent-accent"
        />
        {body}
      </label>
      {item.skippable && !item.done && (
        <div className="ml-7 mt-1">
          <button type="button" onClick={() => onSkip(!item.skipped)} className={item.skipped ? smallBtnOn : `${smallBtn} text-danger`}>
            {item.skipped ? 'Pay it off after all' : 'Skip this time'}
          </button>
          {!item.skipped && <span className="ml-1 text-xs text-ink-faint">The charges stay on the card as debt.</span>}
        </div>
      )}
      {item.bills && item.bills.length > 0 && (
        <div className="ml-7">
          <button type="button" onClick={() => setBillsOpen(!billsOpen)} aria-expanded={billsOpen} className="flex items-center gap-1 text-xs font-medium text-accent">
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

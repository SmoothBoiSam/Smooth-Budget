import { useCallback, useEffect, useState } from 'react'
import { ApiError, payBadge, paydayApi, type PayCheck, type PaydaySummary, type PlanItem } from './api'
import { Caret, ErrorNotice, Loading, Page, PageHeader, smallBtn, smallBtnOn } from './ui'
import { formatCents, formatDay } from './format'
import { useResume } from './offline'
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
      onOpenItems(payBadge(p))
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
        onOpenItems(payBadge(p))
      })
      .catch(fail)
  }, [fail, onOpenItems])
  useResume(load)

  async function toggle(check: PayCheck, item: PlanItem, skip?: boolean) {
    try {
      if (skip !== undefined) await paydayApi.setSkipped(check.payDate, item.key, skip)
      else if (item.parts) {
        // A merged task ticks (or unticks) each of its parts the accounts haven't already settled.
        for (const p of item.parts) if (!p.auto && !p.skipped && p.done === item.done) await paydayApi.setDone(check.payDate, p.key, !item.done)
      } else await paydayApi.setDone(check.payDate, item.key, !item.done)
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
          Nothing to do yet. Once your next check is forecast, the steps for it show up here.{' '}
          <a href="#/payday/setup" className="font-medium text-accent">
            Schedule & pay stubs
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
      {plan.heldEarlierCents > 0 && plan.heldOn && (
        <p className="mt-2 rounded-lg neu-inset px-3 py-2 text-xs text-ink-soft">
          This check doesn’t cover everything planned for it. {formatCents(plan.heldEarlierCents)} comes from what your {formatDay(plan.heldOn)} check keeps
          for it.
        </p>
      )}
      {/* A later check's tasks open on its payday (DECISIONS.md 221): the money isn't there yet. */}
      {!c.isCurrent && <p className="mt-2 text-xs text-ink-soft">You can check these off on payday, {weekday(c.payDate)}.</p>}
      <ul className="mt-2 space-y-1">
        {plan.items.map((item) => (
          <li key={item.key}>
            <PlanRow item={item} locked={!c.isCurrent} onToggle={() => onToggle(item)} onSkip={(part, skip) => onToggle(part, skip)} />
          </li>
        ))}
      </ul>
    </article>
  )
}

/** One part of the payday plan: check it off, or it's done on its own from your accounts. Locked until payday. */
function PlanRow({ item, locked, onToggle, onSkip }: { item: PlanItem; locked: boolean; onToggle: () => void; onSkip: (part: PlanItem, skip: boolean) => void }) {
  const [billsOpen, setBillsOpen] = useState(false)
  const body = (
    <span className="min-w-0 flex-1">
      <span className={`block text-sm ${item.done || item.skipped ? 'text-ink-faint line-through' : ''}`}>{planText(item)}</span>
      {item.status && <span className={`block text-xs ${item.done ? 'text-good' : item.skipped ? 'text-ink-soft' : 'text-gold-ink'}`}>{item.status}</span>}
      {item.why && <span className="block text-xs text-ink-soft">{item.why}</span>}
    </span>
  )
  return (
    <div className="px-1 py-1.5">
      <label className={`flex items-start gap-3 ${locked ? '' : 'cursor-pointer'}`}>
        <input
          type="checkbox"
          checked={item.done}
          disabled={locked || item.auto || item.skipped}
          onChange={onToggle}
          aria-label={item.auto ? `${planText(item)}: done, from your accounts` : planText(item)}
          className="mt-1 size-4 shrink-0 accent-accent"
        />
        {body}
      </label>
      {item.skippable && !item.done && <SkipButton item={item} onSkip={(skip) => onSkip(item, skip)} />}
      {item.parts && (
        // What the one transfer is made of (DECISIONS.md 220), each part with its own reason.
        <ul aria-label="Breakdown" className="ml-7 mt-2 divide-y divide-ink/[0.07] rounded-xl neu-inset px-3 text-xs">
          {item.parts.map((p) => (
            <li key={p.key} className="py-2">
              <div className={`flex justify-between gap-3 font-medium ${p.done || p.skipped ? 'text-ink-faint line-through' : ''}`}>
                <span>{partName(p)}</span>
                <span className="shrink-0 tabular-nums">{formatCents(p.cents)}</span>
              </div>
              <p className="mt-0.5 text-ink-soft">{p.why}</p>
              {p.skipped && p.status && <p className="mt-0.5 text-ink-soft">{p.status}</p>}
              {p.skippable && !p.done && <SkipButton item={p} onSkip={(skip) => onSkip(p, skip)} />}
            </li>
          ))}
        </ul>
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

/** "Skip this time" for paying off what you charged (DECISIONS.md 195). */
function SkipButton({ item, onSkip }: { item: PlanItem; onSkip: (skip: boolean) => void }) {
  return (
    <div className="mt-1.5">
      <button type="button" onClick={() => onSkip(!item.skipped)} className={item.skipped ? smallBtnOn : `${smallBtn} text-danger`}>
        {item.skipped ? 'Pay it off after all' : 'Skip this time'}
      </button>
      {!item.skipped && <span className="ml-1 text-xs text-ink-faint">The charges stay on the card as debt.</span>}
    </div>
  )
}

/** A part's name in a merged task's breakdown. */
function partName(p: PlanItem) {
  if (p.kind === 'cardspend') return p.label === 'Charged so far' ? 'What you’ve charged so far' : 'What you charged'
  if (p.kind === 'sweep') return 'Last month’s leftover'
  return p.label
}

function planText(i: PlanItem) {
  const amount = formatCents(i.cents)
  switch (i.kind) {
    case 'send': {
      // Partly sent: say what's left (the done parts are crossed off in the breakdown).
      const left = (i.parts ?? []).filter((p) => !p.done && !p.skipped).reduce((n, p) => n + p.cents, 0)
      return !i.done && left < i.cents ? `Send ${formatCents(left)} more to ${i.destination}` : `Send ${amount} to ${i.destination}`
    }
    case 'keep':
      return `Keep ${amount} in ${i.destination} for ${i.label}`
    case 'hold':
      // On top of the bills above: the next check won't cover everything planned for it.
      return `Keep ${amount} more in ${i.destination} for ${i.label}`
    case 'cardspend':
      // What you charged to a card: paid from the spending money left in checking, not from this check.
      return `Pay ${amount} to ${i.destination} for what you charged${i.label === 'Charged so far' ? ' so far' : ''}`
    default:
      return `Send ${amount} to ${i.destination} (${i.label.toLowerCase()})`
  }
}

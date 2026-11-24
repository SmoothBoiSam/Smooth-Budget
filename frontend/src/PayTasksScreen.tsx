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

  // Tasks check themselves off from your accounts (DECISIONS.md 313); the one choice left is skipping paying off a card.
  async function skip(check: PayCheck, item: PlanItem, skipped: boolean) {
    try {
      await paydayApi.setSkipped(check.payDate, item.key, skipped)
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
          <a href="#/income" className="font-medium text-accent">
            Income
          </a>
        </p>
      )}

      {open.length > 0 && (
        <ul className="mt-6 space-y-4">
          {open.map((c) => (
            <li key={c.payDate}>
              <TaskCard c={c} onSkip={(item, skipped) => void skip(c, item, skipped)} />
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
                  <TaskCard c={c} onSkip={(item, skipped) => void skip(c, item, skipped)} />
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </Page>
  )
}

function TaskCard({ c, onSkip }: { c: PayCheck; onSkip: (item: PlanItem, skipped: boolean) => void }) {
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
      {/* A later check's tasks start on its payday (DECISIONS.md 221): the money isn't there yet. */}
      {!c.isCurrent && <p className="mt-2 text-xs text-ink-soft">These start on payday, {weekday(c.payDate)}.</p>}
      <ul className="mt-2 divide-y divide-ink/[0.07]">
        {plan.items.map((item) => (
          <li key={item.key}>
            <PlanRow item={item} live={c.isCurrent} onSkip={onSkip} />
          </li>
        ))}
      </ul>
    </article>
  )
}

/**
 * One task (DECISIONS.md 313). It checks itself off from your accounts: what it asks, then how far along it is, and a bar.
 * Keeping money for bills: how far over or short the account is. A move: what it's made of, and how much has gone so far.
 */
function PlanRow({ item, live, onSkip }: { item: PlanItem; live: boolean; onSkip: (item: PlanItem, skipped: boolean) => void }) {
  const [billsOpen, setBillsOpen] = useState(false)
  const keeps = item.kind === 'keep' || item.kind === 'hold'
  // Paying off what you charged can be skipped, on its own or as part of a card's payment.
  const skippable = (item.parts ?? [item]).filter((p) => p.skippable && !p.done)
  return (
    <div className="flex gap-3 px-1 py-3">
      <DoneMark item={item} />
      <div className="min-w-0 flex-1">
        {keeps ? <KeepLines item={item} live={live} /> : <SendLines item={item} live={live} />}
        {skippable.map((p) => (
          <SkipButton key={p.key} item={p} onSkip={(skipped) => onSkip(p, skipped)} />
        ))}
        {item.bills && item.bills.length > 0 && (
          <div className="mt-1.5">
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
    </div>
  )
}

/** Done or not, as your accounts show it: nothing to tap. */
function DoneMark({ item }: { item: PlanItem }) {
  if (item.done)
    return (
      <span role="img" aria-label="Done" className="mt-0.5 grid size-5 shrink-0 place-items-center rounded-full bg-good text-white">
        <svg viewBox="0 0 16 16" className="size-3" fill="none" stroke="currentColor" strokeWidth={2.4} strokeLinecap="round" strokeLinejoin="round" aria-hidden>
          <path d="m3.5 8.5 3 3 6-7" />
        </svg>
      </span>
    )
  return <span role="img" aria-label={item.skipped ? 'Skipped' : 'Not done yet'} className="mt-0.5 size-5 shrink-0 rounded-full border-2 border-ink/20" />
}

/** "Keep $1,650.00 in Checking for Bills", how far over or short the account is, and a bar. */
function KeepLines({ item, live }: { item: PlanItem; live: boolean }) {
  const what = item.kind === 'hold' ? `Keep ${formatCents(item.cents)} more in ${item.destination} for ${item.label}` : `Keep ${formatCents(item.cents)} in ${item.destination} for Bills`
  const have = item.haveCents
  const need = item.needCents
  const known = live && have != null && need != null
  const diff = known ? have - need : 0
  return (
    <>
      <p className="text-sm font-medium">{what}</p>
      {known ? (
        <>
          <p className={`mt-0.5 text-xs font-medium ${diff < 0 ? 'text-gold-ink' : 'text-good'}`}>
            {diff < 0 ? `⚠️ ${formatCents(-diff)} short` : diff > 0 ? `✅ ${formatCents(diff)} over` : '✅ Exactly enough'}
          </p>
          <KeepBar have={have} need={need} />
        </>
      ) : (
        live && item.status && <p className="mt-0.5 text-xs text-ink-soft">{item.status}</p>
      )}
    </>
  )
}

/**
 * Short: how much of what the bills need the account has (gold). Over: the account's whole balance, grey up to the line
 * where the bills' part ends and green past it.
 */
function KeepBar({ have, need }: { have: number; need: number }) {
  const label = `${formatCents(Math.max(0, have))} of ${formatCents(need)} needed`
  if (have < need)
    return (
      <div role="img" aria-label={label} className="mt-2 h-2 overflow-hidden rounded-full neu-inset">
        <div className="h-full rounded-full bg-gold" style={{ width: `${need > 0 ? Math.max(0, (have / need) * 100) : 0}%` }} />
      </div>
    )
  const at = have > 0 ? (need / have) * 100 : 100
  return (
    <div role="img" aria-label={label} className="relative mt-2">
      <div className="flex h-2 overflow-hidden rounded-full neu-inset">
        <div className="h-full bg-ink/25" style={{ width: `${at}%` }} />
        <div className="h-full flex-1 bg-good" />
      </div>
      {need > 0 && <span aria-hidden className="absolute -top-1 h-4 w-0.5 -translate-x-1/2 rounded-full bg-ink" style={{ left: `${at}%` }} />}
    </div>
  )
}

/** "Send $917.26 to Visa", what it's waiting for if anything, what it's made of, and how much has gone so far. */
function SendLines({ item, live }: { item: PlanItem; live: boolean }) {
  const sent = item.done ? item.cents : Math.min(item.cents, item.sentCents ?? 0)
  const left = item.cents - sent
  const wait = item.done ? 0 : Math.min(left, item.waitCents ?? 0)
  const of = wait > 0 && wait < left ? `${formatCents(wait)} of it ` : ''
  const waiting =
    wait <= 0 ? null : item.waitDate && item.waitName ? `⏳ ${of}on ${formatDay(item.waitDate)} when ${item.waitName} arrives` : `⏳ ${of}once checking has room`
  const terms = breakdown(item)
  const showTerms = terms.length > 1 || (terms.length === 1 && (item.kind === 'cardspend' || item.kind === 'sweep'))
  return (
    <>
      {live && waiting && <p className="text-xs font-medium text-gold-ink">{waiting}</p>}
      <p className={`text-sm font-medium ${item.skipped ? 'text-ink-faint line-through' : ''}`}>
        Send {formatCents(item.cents)} to {item.destination}
      </p>
      {showTerms && <p className="mt-0.5 text-xs text-ink-soft">{terms.join(' + ')}</p>}
      {item.skipped && <p className="mt-0.5 text-xs text-ink-soft">Skipped: these charges stay on the card.</p>}
      {live && !item.skipped && item.cents > 0 && (
        <div role="img" aria-label={`${formatCents(sent)} of ${formatCents(item.cents)} sent`} className="mt-2 h-2 overflow-hidden rounded-full neu-inset">
          <div className={`h-full rounded-full ${item.done ? 'bg-good' : 'bg-accent'}`} style={{ width: `${(sent / item.cents) * 100}%` }} />
        </div>
      )}
    </>
  )
}

/**
 * What a move is made of: "$160.00 minimum payment + $757.26 extra", "$50.00 savings + $20.00 last month's leftover".
 * Parts of the same kind add up into one (two months' leftovers are one "leftover").
 */
function breakdown(i: PlanItem) {
  const terms = new Map<string, number>()
  const add = (what: string, cents: number) => cents > 0 && terms.set(what, (terms.get(what) ?? 0) + cents)
  for (const p of (i.parts ?? [i]).filter((x) => !x.skipped))
    switch (p.kind) {
      case 'debt': {
        const min = Math.min(p.minimumCents ?? 0, p.cents)
        if (min <= 0) add('card payment', p.cents)
        else {
          add('minimum payment', min)
          add('extra', p.cents - min)
        }
        break
      }
      case 'cardspend':
        add(`for what you charged${p.label === 'Charged so far' ? ' so far' : ''}`, p.cents)
        break
      case 'sweep':
        add('leftover from last month', p.cents)
        break
      case 'savings':
        add('savings', p.cents)
        break
      default:
        add(p.label.toLowerCase(), p.cents)
    }
  return [...terms].map(([what, cents]) => `${formatCents(cents)} ${what}`)
}

/** "Skip this time" for paying off what you charged (DECISIONS.md 195). */
function SkipButton({ item, onSkip }: { item: PlanItem; onSkip: (skipped: boolean) => void }) {
  return (
    <div className="mt-2">
      <button type="button" onClick={() => onSkip(!item.skipped)} className={item.skipped ? smallBtnOn : `${smallBtn} text-danger`}>
        {item.skipped ? 'Pay off what you charged after all' : 'Skip paying off what you charged'}
      </button>
      {!item.skipped && <p className="mt-1 text-xs text-ink-faint">The charges stay on the card as debt.</p>}
    </div>
  )
}

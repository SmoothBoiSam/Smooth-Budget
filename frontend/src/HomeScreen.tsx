import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, api, budgetApi, plaidApi, type BankUpdate, type CashCheck, type Category, type HomeSummary, type Me, type PickUp, type Transaction } from './api'
import { TransactionList } from './BudgetScreen'
import { TransactionEditSheet } from './ReviewScreen'
import { formatCents, formatDay, formatTime, healthBar } from './format'
import { useArrivals, useTween } from './motion'
import { saved, useOnline, useResume } from './offline'
import { Card, Chevron, Loading, Notice, Pill, Row, Rows } from './ui'

/**
 * How Left to Spend looks (DECISIONS.md 188): green with plenty left, yellow once it's getting low (under 40% of the
 * month's spending money), red close to zero (under 15%, the same point as the "running low" alert) or below it. The
 * ring, your status cat (DECISIONS.md 139) and the label all follow it.
 */
type Level = 'plenty' | 'getting-low' | 'near-zero' | 'over'
const levels: Record<Level, { ring: string; tone: 'good' | 'todo' | 'danger'; label: string; cat: { src: string; alt: string } }> = {
  plenty: { ring: 'stroke-good', tone: 'good', label: 'On track', cat: { src: '/brand/cat-plenty.png', alt: 'A happy cat holding a stack of coins' } },
  'getting-low': { ring: 'stroke-gold', tone: 'todo', label: 'Getting low', cat: { src: '/brand/cat-low.png', alt: 'A worried cat holding a few coins' } },
  'near-zero': { ring: 'stroke-danger', tone: 'danger', label: 'Running low', cat: { src: '/brand/cat-over.png', alt: 'A crying cat' } },
  over: { ring: 'stroke-danger', tone: 'danger', label: 'Overspent', cat: { src: '/brand/cat-over.png', alt: 'A crying cat' } },
}
/** The level for an amount left: the server's own lines (below zero; zero or under 15%) plus the 40% one. */
function levelAt(leftCents: number, limitCents: number): Level {
  if (leftCents < 0) return 'over'
  if (leftCents === 0 || leftCents * 100 < 15 * limitCents) return 'near-zero'
  return leftCents * 100 < 40 * limitCents ? 'getting-low' : 'plenty'
}

const ringColors: Record<'good' | 'gold' | 'danger', Level[]> = { good: ['plenty'], gold: ['getting-low'], danger: ['near-zero', 'over'] }

// The first time Home shows in a visit, Left to Spend counts up from zero with its ring (DECISIONS.md 190); after
// that it shows straight away and glides when it changes.
let introPlayed = false

/** Left to Spend as shown: counting up on the first open, gliding after; its level follows the number as it moves. */
function useShownLeft(s: HomeSummary) {
  const [intro] = useState(() => !introPlayed)
  useEffect(() => {
    introPlayed = true
  }, [])
  const left = Math.round(useTween(s.leftToSpendCents, intro ? 900 : 500, intro ? 0 : s.leftToSpendCents))
  const share = s.totalVariableLimitCents > 0 ? Math.max(0, Math.min(1, left / s.totalVariableLimitCents)) : 0
  // At rest it's the server's status; on the way there, the same lines applied to the number shown.
  const level = left === s.leftToSpendCents ? (s.status === 'overspent' ? 'over' : s.status === 'low' ? 'near-zero' : levelAt(left, s.totalVariableLimitCents)) : levelAt(left, s.totalVariableLimitCents)
  return { left, share, level, intro }
}

export default function HomeScreen({ me, onSignedOut }: { me: Me; onSignedOut: () => void }) {
  // Opens straight to the last summary saved on this phone, then refreshes it (milestone 10).
  const [summary, setSummary] = useState<HomeSummary | null>(() => saved.home(me.householdId)?.summary ?? null)
  const [savedAt, setSavedAt] = useState<string | null>(null) // set while showing a saved summary because the server can't be reached
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const online = useOnline()

  const load = useCallback(async () => {
    try {
      const s = await api.homeSummary()
      setSummary(s)
      saved.setHome(me.householdId, s)
      setSavedAt(null)
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      const cached = e instanceof ApiError && e.offline ? saved.home(me.householdId) : null
      if (cached) {
        setSummary(cached.summary)
        setSavedAt(cached.savedUtc)
        setError(null)
      } else setError(e instanceof Error ? e.message : 'Could not load your summary.')
    } finally {
      setLoading(false)
    }
  }, [onSignedOut, me.householdId])

  useEffect(() => {
    void load()
  }, [load])
  useResume(load)

  // Pull to refresh (SPEC section 4.2) and the Refresh link both sync connected banks, then reload.
  async function refresh() {
    setLoading(true)
    setSyncing(true)
    await plaidApi.syncAll().catch(() => undefined) // a failed sync shows up in summary.sync
    await load()
    setSyncing(false)
  }

  // "Update bank data" (DECISIONS.md 242): Plaid goes to each bank now (a fee per bank, limited), then everything syncs.
  // What a bank sends a little later comes by webhook, and one more free sync half a minute on.
  const [bankNote, setBankNote] = useState<string | null>(null)
  // Bank updates left this month (DECISIONS.md 243). Each bank has its own monthly cap, and one tap uses one from every
  // bank, so what's left is the busiest bank's.
  const [checksLeft, setChecksLeft] = useState<{ left: number; per: number } | null>(null)
  const hasBanks = !!summary && summary.sync.state !== 'none'
  const loadChecks = useCallback(
    () =>
      plaidApi
        .items()
        .then((items) => {
          const active = items.filter((i) => i.status === 'Active')
          setChecksLeft(
            active.length === 0
              ? null
              : { left: Math.max(0, active[0].bankCheck.perMonth - Math.max(...active.map((i) => i.bankCheck.thisMonth))), per: active[0].bankCheck.perMonth },
          )
        })
        .catch(() => {}), // extra: the button works without it
    [],
  )
  useEffect(() => {
    if (hasBanks) void loadChecks()
  }, [hasBanks, loadChecks])

  async function updateBanks() {
    setLoading(true)
    setSyncing(true)
    setBankNote(null)
    try {
      const r = await plaidApi.checkBanks()
      setBankNote(describeBankUpdate(r))
      void loadChecks()
      window.setTimeout(() => {
        plaidApi
          .syncAll()
          .then(() => load())
          .catch(() => {})
      }, 30_000)
    } catch (e) {
      setBankNote(e instanceof Error ? e.message : 'Couldn’t reach your banks.')
    }
    await load()
    setSyncing(false)
  }

  const startY = useRef<number | null>(null)
  const [pull, setPull] = useState(0)
  const [syncing, setSyncing] = useState(false)
  const PULL_TRIGGER = 70

  function onTouchStart(e: React.TouchEvent) {
    startY.current = window.scrollY <= 0 && !loading ? e.touches[0].clientY : null
  }
  function onTouchMove(e: React.TouchEvent) {
    if (startY.current === null) return
    const dy = e.touches[0].clientY - startY.current
    setPull(dy > 0 ? Math.min(dy * 0.6, 100) : 0)
  }
  function onTouchEnd() {
    if (pull >= PULL_TRIGGER) void refresh()
    setPull(0)
    startY.current = null
  }

  return (
    <div className="mx-auto flex min-h-screen max-w-md flex-col" onTouchStart={onTouchStart} onTouchMove={onTouchMove} onTouchEnd={onTouchEnd}>
      {/* Easter egg (DECISIONS.md 139): the tabby hangs down from the top of the screen as you pull to sync, and swings while it syncs. */}
      <div aria-hidden className="pointer-events-none fixed inset-x-0 top-0 z-30 flex justify-center">
        <div className={pull > 0 ? '' : 'transition-transform duration-300 ease-out'} style={{ transform: `translateY(${(syncing ? 100 : pull * 1.1) - 143}px)` }}>
          <img src="/brand/cat-tabby.png" alt="" className={`w-32 origin-top ${syncing ? 'animate-tabby-swing' : ''}`} />
        </div>
      </div>
      {(pull > 0 || (loading && summary)) && (
        <div
          aria-live="polite"
          className="flex items-end justify-center overflow-hidden text-xs font-medium text-ink-soft transition-[height]"
          style={{ height: syncing ? 108 : loading ? 32 : pull }}
        >
          <span className="pb-1">{loading ? 'Syncing…' : pull >= PULL_TRIGGER ? 'Release to sync' : 'Pull to sync'}</span>
        </div>
      )}
      <header className="px-6 pt-[max(1.25rem,env(safe-area-inset-top))]">
        <h1 className="text-2xl font-bold">{summary ? summary.budgetMonth.label : 'This month'}</h1>
        {summary && (
          <p className="text-xs text-ink-faint">
            {formatDay(summary.budgetMonth.start)} – {formatDay(summary.budgetMonth.end)}
            {savedAt && !online && ` · as of ${formatTime(savedAt)}`}
          </p>
        )}
      </header>

      <main className="flex-1 px-5 pb-[calc(7rem+env(safe-area-inset-bottom))]">
        {error && (
          <div className="mt-6">
            <Notice tone="danger">
              Couldn’t load Left to Spend. {error}{' '}
              <button type="button" onClick={refresh} className="font-semibold text-accent">
                Try again
              </button>
            </Notice>
          </div>
        )}

        {!summary && !error && <Loading label="Loading Left to Spend" />}

        {/* With the phone offline, the bar at the top already says so (DECISIONS.md 160). This is for a server that can't be reached. */}
        {savedAt && online && (
          <div className="mt-6">
            <Notice tone="todo">
              Showing your numbers from {formatTime(savedAt)}.{' '}
              <button type="button" onClick={refresh} disabled={loading} className="font-semibold text-accent disabled:opacity-50">
                {loading ? 'Trying…' : 'Try again'}
              </button>
            </Notice>
          </div>
        )}

        {summary && <Summary summary={summary} stale={!!savedAt} />}
        {summary && online && <RecentTransactions summary={summary} onChanged={() => void load()} />}

        {/* With banks connected: one button that has Plaid check them all now (DECISIONS.md 242). Pull to refresh stays free. */}
        {summary && summary.sync.state !== 'none' && online && (
          <div className="mt-8 flex flex-col items-center gap-2">
            <button
              type="button"
              onClick={() => void updateBanks()}
              disabled={loading}
              className="rounded-full neu-sm px-5 py-2.5 text-sm font-semibold text-accent disabled:opacity-50"
            >
              {loading && syncing ? 'Updating…' : 'Update bank data'}
            </button>
            {checksLeft && checksLeft.per > 0 && (
              <div className="w-24" role="img" aria-label={`${checksLeft.left} of ${checksLeft.per} bank updates left this month`}>
                <p className="text-center text-[11px] tabular-nums text-ink-faint">
                  {checksLeft.left} / {checksLeft.per}
                </p>
                <div className="mt-0.5 h-1 overflow-hidden rounded-full neu-inset">
                  <div
                    className={`h-full rounded-full ${checksLeft.left <= checksLeft.per * 0.2 ? 'bg-gold' : 'bg-accent'}`}
                    style={{ width: `${(checksLeft.left / checksLeft.per) * 100}%` }}
                  />
                </div>
              </div>
            )}
            {bankNote && (
              <p role="status" className="max-w-xs text-center text-xs text-ink-soft">
                {bankNote}
              </p>
            )}
          </div>
        )}

        {summary && (
          <p className={`${summary.sync.state !== 'none' && online ? 'mt-3' : 'mt-8'} text-center text-xs text-ink-faint`}>
            {summary.sync.state !== 'none' && summary.sync.lastSyncedUtc
              ? `Synced ${formatTime(summary.sync.lastSyncedUtc)}`
              : summary.lastUpdatedUtc
                ? `Updated ${formatTime(summary.lastUpdatedUtc)}`
                : 'No transactions yet'}
            {(summary.sync.state === 'none' || !online) && (
              <>
                {' · '}
                <button type="button" onClick={refresh} disabled={loading} className="font-semibold text-accent disabled:opacity-50">
                  {loading ? 'Syncing…' : 'Refresh'}
                </button>
              </>
            )}
          </p>
        )}
      </main>
    </div>
  )
}

/** "Asked Chase for new activity: 2 new." or, when every bank was checked lately, when the next check can happen. */
function describeBankUpdate(r: BankUpdate): string {
  const added = r.results.reduce((n, x) => n + (x.ok ? x.added : 0), 0)
  const parts: string[] = []
  if (r.checked.length > 0)
    parts.push(`Asked ${r.checked.join(' and ')} for new activity: ${added === 0 ? 'nothing new yet' : `${added} new`}. More may show up in a minute.`)
  else if (r.nextCheckUtc)
    parts.push(`Your banks were updated recently, so this synced what Plaid already had (${added} new). Next bank update ${formatTime(r.nextCheckUtc)}.`)
  else if (r.problems.length === 0) parts.push(`Synced: ${added} new.`)
  parts.push(...r.problems)
  return parts.join(' ')
}

/** Whole days from one ISO date to another. */
function daysBetween(from: string, to: string) {
  const day = (iso: string) => {
    const [y, m, d] = iso.split('-').map(Number)
    return Date.UTC(y, m - 1, d) / 86_400_000
  }
  return Math.round(day(to) - day(from))
}

/**
 * Every spending category as a thin row under Left to Spend (DECISIONS.md 254, 255): its name, what's left, and a bar
 * filled and colored the way its line on the Budget tab is (how much of the limit is used; green, gold when low, red
 * when over). A tap opens the category.
 */
function SpendingRows({ items }: { items: NonNullable<HomeSummary['spending']> }) {
  return (
    <ul aria-label="Left in each spending category" className="mt-4">
      {items.map((c) => {
        const used = c.limitCents > 0 ? Math.min(1, Math.max(0, c.spentCents / c.limitCents)) : c.spentCents > 0 ? 1 : 0
        const over = c.leftCents < 0
        return (
          <li key={c.id}>
            <a
              href={`#/budget/c/${c.id}`}
              aria-label={`${c.name}: ${over ? `${formatCents(-c.leftCents)} over` : `${formatCents(c.leftCents)} left of ${formatCents(c.limitCents)}`}`}
              className="grid grid-cols-[minmax(0,1fr)_auto_5rem] items-center gap-3 py-1.5 text-xs"
            >
              <span className="truncate text-ink-soft">{c.name}</span>
              <span aria-hidden className={`text-right font-semibold tabular-nums ${over ? 'text-danger' : ''}`}>
                {over ? `${formatCents(-c.leftCents)} over` : formatCents(c.leftCents)}
              </span>
              <span aria-hidden className="block h-2 overflow-hidden rounded-full neu-inset">
                <span className={`block h-full rounded-full ${healthBar[c.health]}`} style={{ width: `${used * 100}%` }} />
              </span>
            </a>
          </li>
        )
      })}
    </ul>
  )
}

/** Left to Spend in a pressed-in well, with a ring for the share of the variable budget still left. */
function Gauge({ s, shown }: { s: HomeSummary; shown: ReturnType<typeof useShownLeft> }) {
  const style = levels[shown.level]
  const r = 88
  const arc = 2 * Math.PI * r * 0.75 // a three-quarter ring, open at the bottom
  return (
    <div className="neu-inset relative mx-auto grid aspect-square w-full max-w-60 place-items-center rounded-full">
      <svg viewBox="0 0 200 200" aria-hidden className="absolute inset-0 size-full">
        <defs>
          {(['good', 'gold', 'danger'] as const).map((c) => (
            <linearGradient key={c} id={`ring-${c}`} x1="0" y1="1" x2="1" y2="0">
              <stop offset="0" style={{ stopColor: `var(--color-${c})` }} />
              <stop offset="1" style={{ stopColor: `color-mix(in oklab, var(--color-${c}) 65%, white)` }} />
            </linearGradient>
          ))}
        </defs>
        <g transform="rotate(135 100 100)">
          <circle cx="100" cy="100" r={r} fill="none" strokeWidth="10" strokeLinecap="round" className="stroke-ink/10" strokeDasharray={`${arc} 999`} />
          {/* One ring per color, cross-fading as the level changes. */}
          {shown.share > 0 &&
            (['good', 'gold', 'danger'] as const).map((c) => (
              <circle
                key={c}
                cx="100"
                cy="100"
                r={r}
                fill="none"
                strokeWidth="10"
                strokeLinecap="round"
                stroke={`url(#ring-${c})`}
                strokeDasharray={`${arc * shown.share} 999`}
                className="transition-opacity duration-500"
                opacity={ringColors[c].includes(shown.level) ? 1 : 0}
              />
            ))}
        </g>
      </svg>
      <div className="relative text-center">
        <div className="relative mx-auto size-16">
          {(['plenty', 'getting-low', 'near-zero'] as const).map((l) => {
            const on = (shown.level === 'over' ? 'near-zero' : shown.level) === l
            return (
              <img
                key={l}
                src={levels[l].cat.src}
                alt={on ? style.cat.alt : ''}
                aria-hidden={!on}
                className={`absolute inset-0 size-16 object-contain transition-[opacity,transform] duration-500 ${on ? 'scale-100 opacity-100' : 'scale-90 opacity-0'}`}
              />
            )
          })}
        </div>
        <h2 id="lts-label" className="mt-1 text-xs font-medium text-ink-soft">
          Left to Spend
        </h2>
        <p data-testid="left-to-spend" className="text-4xl font-bold tracking-tight tabular-nums" aria-label={formatCents(s.leftToSpendCents)}>
          {formatCents(shown.left)}
        </p>
        <p className="mt-0.5 text-xs text-ink-faint">
          {s.daysRemaining} {s.daysRemaining === 1 ? 'day' : 'days'} left
        </p>
      </div>
    </div>
  )
}

/**
 * Left to Spend checked against the bank (DECISIONS.md 183): checking now and money still coming, less what still has to
 * go out this month. Folded, it says whether the accounts cover Left to Spend; open, it shows every part. Shown only
 * while they don't (DECISIONS.md 236).
 */
function CashCheckRow({ cash: c, shortCents, pickUp }: { cash: CashCheck; shortCents: number; pickUp: PickUp | null }) {
  const rows: [string, number, 1 | -1][] = [
    ['In checking now', c.checkingCents, 1],
    ['Pay still coming', c.incomeStillComingCents, 1],
    ['Money from friends still coming', c.partnerStillComingCents, 1],
    ['Bills not paid yet', c.billsStillDueCents, -1],
    ['Cards to pay off', c.cardsToPayCents, -1],
    ['Savings not moved yet', c.savingsStillToMoveCents, -1],
    ['Last month’s leftover to move', c.leftoverToMoveCents, -1],
  ]
  return (
    <li>
      <details className="group">
        <summary className="flex cursor-pointer list-none items-center gap-3 px-5 py-3.5 text-sm">
          <span className="min-w-0 flex-1">
            <span className="block">Your accounts</span>
            {shortCents > 0 && pickUp && (
              <span className="mt-0.5 block text-xs text-ink-soft">
                Pick up {pickUp.hours} flight {pickUp.hours === 1 ? 'hour' : 'hours'} to make it up
              </span>
            )}
          </span>
          <span className={`shrink-0 whitespace-nowrap font-semibold tabular-nums ${shortCents > 0 ? 'text-danger' : 'text-good'}`}>
            {shortCents > 0 ? `${formatCents(shortCents)} short` : 'Cover it'}
          </span>
          <span className="transition-transform group-open:rotate-90">
            <Chevron />
          </span>
        </summary>
        <dl className="neu-inset mx-4 mb-4 space-y-1.5 rounded-2xl px-4 py-3 text-xs">
          {rows
            .filter(([label, cents]) => cents !== 0 || label === 'In checking now')
            .map(([label, cents, sign]) => (
              <div key={label} className="flex justify-between gap-3">
                <dt className="text-ink-soft">{label}</dt>
                <dd className="shrink-0 whitespace-nowrap tabular-nums">
                  {sign < 0 ? '−' : '+'}
                  {formatCents(Math.abs(cents))}
                </dd>
              </div>
            ))}
          <div className="flex justify-between gap-3 border-t border-ink/10 pt-1.5 font-semibold">
            <dt>Available</dt>
            <dd className="tabular-nums">{formatCents(c.availableCents)}</dd>
          </div>
          {shortCents > 0 && <p className="pt-1 text-ink-faint">Left to Spend is capped at what’s available.</p>}
          {shortCents > 0 && pickUp && (
            <p className="text-ink-faint">
              At {formatCents(pickUp.rateCents)}/hour, an extra flight hour brings home about {formatCents(pickUp.takeHomePerHourCents)} after taxes, so{' '}
              {pickUp.hours} {pickUp.hours === 1 ? 'hour covers' : 'hours cover'} the {formatCents(shortCents)}. It lands on the check that pays for the trip.
            </p>
          )}
        </dl>
      </details>
    </li>
  )
}

function Summary({ summary: s, stale }: { summary: HomeSummary; stale: boolean }) {
  const shown = useShownLeft(s)
  const style = levels[shown.level]
  // What lasts until the next check: the safe daily amount for each day before payday (DECISIONS.md 148).
  const daysToPayday = s.nextPayday ? daysBetween(s.today, s.nextPayday.payDate) : 0
  const untilPayday =
    s.nextPayday && daysToPayday > 0 && daysToPayday < s.daysRemaining
      ? { date: s.nextPayday.payDate, cents: Math.max(0, Math.min(s.leftToSpendCents, s.safeToSpendPerDayCents * daysToPayday)) }
      : null

  // Everything that wants a tap, most urgent first: problems, then things to do, then good news.
  const notices: { key: string; tone: 'danger' | 'todo' | 'good'; href?: string; text: React.ReactNode }[] = []
  if (!stale && (s.sync.state === 'stale' || s.sync.state === 'error'))
    notices.push({ key: 'sync', tone: s.sync.state === 'error' ? 'danger' : 'todo', href: '#/more/banks', text: s.sync.message ?? 'Bank sync needs a look.' })
  for (const w of s.cashWarnings ?? [])
    notices.push({
      key: `cash-${w.account}`,
      tone: 'danger',
      href: '#/income',
      text: (
        <>
          {w.account} is <b className="tabular-nums">{formatCents(w.shortCents)}</b> short for {w.billCount === 1 ? 'a bill' : `${w.billCount} bills`} due
          before {w.nextPayDate ? `payday ${formatDay(w.nextPayDate)}` : 'your next payday'}
        </>
      ),
    })
  for (const f of (s.contributions ?? []).filter((f) => f.state === 'late' || (f.state === 'differentAmount' && f.shortfallCents > 0)))
    notices.push({
      key: `partner-${f.arrangementId}`,
      tone: 'danger',
      href: f.friendId ? `#/friends/${f.friendId}` : '#/friends',
      text:
        f.state === 'late'
          ? `${formatCents(f.expectedCents)} for ${f.categoryName} hasn’t come yet`
          : `Only ${formatCents(f.receivedCents)} of ${formatCents(f.expectedCents)} came for ${f.categoryName}`,
    })
  if (s.overBudgetCategoryCount > 0)
    notices.push({
      key: 'over',
      tone: 'danger',
      href: '#/budget',
      text: (
        <>
          Over by <b className="tabular-nums">{formatCents(s.overBudgetCents)}</b> in {s.overBudgetCategoryCount}{' '}
          {s.overBudgetCategoryCount === 1 ? 'category' : 'categories'}
        </>
      ),
    })
  if (s.incomeUnknown) notices.push({ key: 'income', tone: 'todo', href: '#/budget/plan', text: 'Set this month’s income' })
  if (s.uncategorizedCount > 0)
    notices.push({ key: 'uncat', tone: 'todo', href: '#/tasks/transactions', text: `${s.uncategorizedCount} ${s.uncategorizedCount === 1 ? 'purchase needs' : 'purchases need'} a category` })
  if (s.reviewCount > 0)
    notices.push({ key: 'review', tone: 'todo', href: '#/tasks/transactions', text: `${s.reviewCount} ${s.reviewCount === 1 ? 'purchase' : 'purchases'} to confirm` })
  for (const p of s.pendingSweeps)
    notices.push({
      key: `sweep-${p.id}`,
      tone: 'good',
      href: '#/tasks/payday',
      text: (
        <>
          Send <b className="tabular-nums">{formatCents(p.amountCents)}</b> of {p.fromMonth.split(' ')[0]}’s leftover to {p.destination}
        </>
      ),
    })

  // A notice that turns up while you're looking (after a sync, say) slides in; the ones already there don't.
  const isNew = useArrivals(notices.map((n) => n.key))

  return (
    <>
      <section aria-labelledby="lts-label" className="neu mt-5 rounded-[2rem] px-5 pt-6 pb-5">
        <Gauge s={s} shown={shown} />
        <div className="mt-4 text-center">
          <Pill tone={style.tone}>{style.label}</Pill>
        </div>
        <dl className="mt-5 grid grid-cols-2 gap-3">
          <div className="neu-inset rounded-2xl px-4 py-3">
            <dt className="text-xs text-ink-soft">Safe per day</dt>
            <dd className="text-lg font-semibold tabular-nums">
              <Cents cents={s.safeToSpendPerDayCents} countUp={shown.intro} />
            </dd>
          </div>
          <div className="neu-inset rounded-2xl px-4 py-3">
            <dt className="text-xs text-ink-soft">{untilPayday ? `Until payday ${formatDay(untilPayday.date)}` : 'Days left'}</dt>
            <dd className="text-lg font-semibold tabular-nums">{untilPayday ? <Cents cents={untilPayday.cents} countUp={shown.intro} /> : s.daysRemaining}</dd>
          </div>
        </dl>
        {s.spending && s.spending.length > 0 && <SpendingRows items={s.spending} />}
      </section>

      {notices.length > 0 && (
        <ul className="mt-6 space-y-3">
          {notices.map((n) => (
            <li key={n.key} className={isNew(n.key) ? 'reveal' : undefined}>
              <Notice tone={n.tone} href={n.href}>
                {n.text}
              </Notice>
            </li>
          ))}
        </ul>
      )}

      <Card title="This month">
        <Rows>
          <Row
            label="Spent"
            value={
              <>
                {formatCents(s.totalVariableSpentCents)} <span className="font-normal text-ink-faint">of {formatCents(s.totalVariableLimitCents)}</span>
              </>
            }
            href="#/budget"
          />
          {s.carriedInDeficitCents > 0 && <Row label="Carried over" value={`−${formatCents(s.carriedInDeficitCents)}`} tone="danger" />}
          {s.unfiledSpendingCents > 0 && <Row label="Not in a category" value={`−${formatCents(s.unfiledSpendingCents)}`} tone="danger" href="#/tasks/transactions" />}
          {s.overplannedCents > 0 && <Row label="Planned beyond your money" value={`−${formatCents(s.overplannedCents)}`} tone="danger" href="#/budget/plan" />}
          {/* Only when the budget couldn't shrink enough to fit the accounts (DECISIONS.md 236): otherwise there's nothing to act on. */}
          {s.cash && s.cashShortCents > 0 && <CashCheckRow cash={s.cash} shortCents={s.cashShortCents} pickUp={s.pickUp ?? null} />}
          {s.nextPayday && (
            <Row
              label={`Payday ${formatDay(s.nextPayday.payDate)}`}
              hint={
                s.nextPayday.hasSchedule
                  ? `${s.nextPayday.lowNetCents === s.nextPayday.highNetCents ? 'Forecast' : `${formatCents(s.nextPayday.lowNetCents)}–${formatCents(s.nextPayday.highNetCents)}`}${s.nextPayday.lowConfidence ? ' · rough guess' : ''}`
                  : 'Import your schedule to forecast it'
              }
              value={s.nextPayday.hasSchedule ? `≈ ${formatCents(s.nextPayday.netCents)}` : undefined}
              href="#/income"
            />
          )}
        </Rows>
      </Card>
    </>
  )
}

/**
 * The newest few transactions, any month (DECISIONS.md 171). Tap one to change it; "See all" opens the full list and
 * search. Reloads whenever Home does, so a pull to refresh shows new ones.
 */
function RecentTransactions({ summary, onChanged }: { summary: HomeSummary; onChanged: () => void }) {
  const [items, setItems] = useState<Transaction[] | null>(null)
  const [categories, setCategories] = useState<Category[]>([])
  const [selected, setSelected] = useState<Transaction | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    Promise.all([budgetApi.recent(5), budgetApi.categories()])
      .then(([r, c]) => {
        if (!live) return
        setItems(r)
        setCategories(c)
      })
      .catch(() => live && setItems((x) => x ?? []))
    return () => {
      live = false
    }
  }, [summary])

  return (
    <section aria-labelledby="recent-h" className="mt-6">
      <div className="mb-2 flex items-baseline justify-between px-1">
        <h2 id="recent-h" className="px-1 text-sm font-semibold text-ink-soft">
          Recent transactions
        </h2>
        <a href="#/transactions" className="text-sm font-semibold text-accent">
          See all
        </a>
      </div>
      {notice && (
        <div className="mb-3">
          <Notice tone="good">{notice}</Notice>
        </div>
      )}
      {items === null ? (
        <div className="neu h-40 animate-pulse rounded-3xl" />
      ) : (
        <TransactionList items={items} empty="No transactions yet." onSelect={setSelected} className="" />
      )}
      {selected && (
        <TransactionEditSheet
          transaction={selected}
          categories={categories}
          onClose={() => setSelected(null)}
          onSaved={(m) => {
            setSelected(null)
            setNotice(m)
            onChanged()
          }}
        />
      )}
    </section>
  )
}

/** An amount that counts up with Left to Spend on the first open and glides when it changes. */
function Cents({ cents, countUp }: { cents: number; countUp: boolean }) {
  return <>{formatCents(Math.round(useTween(cents, countUp ? 900 : 500, countUp ? 0 : cents)))}</>
}

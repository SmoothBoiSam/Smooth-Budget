import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ApiError, api, plaidApi, type BankUpdate, type CashCheck, type HomeSummary, type Me, type MonthOverview, type PickUp } from './api'
import { homeBadge, homeNotices } from './homeNotices'
import { formatCents, formatDay, formatTime, isMaxed, spendingBar } from './format'
import { useArrivals, useTween } from './motion'
import { saved, useOnline, useResume } from './offline'
import { Card, Chevron, Loading, Notice, Pill, Rows } from './ui'

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

/**
 * Left to Spend as shown: counting up on the first open, gliding after; its level follows the number as it moves. The
 * big number, ring and status are the budget month's, payday to payday (DECISIONS.md 269), except while checking can't
 * cover Left to Spend until the next payday: then they're Safe to Spend, what it can cover until then (273, 276).
 */
function useShownLeft(s: HomeSummary) {
  const [intro] = useState(() => !introPlayed)
  useEffect(() => {
    introPlayed = true
  }, [])
  const adjusted = s.safeToSpend ?? null
  const [target, total, status] = [adjusted ? adjusted.safeCents : s.leftToSpendCents, s.totalVariableLimitCents, s.status]
  const left = Math.round(useTween(target, intro ? 900 : 500, intro ? 0 : target))
  const share = total > 0 ? Math.max(0, Math.min(1, left / total)) : 0
  // While it's Safe to Spend, Left to Spend shows behind it as a grey ghost ring (DECISIONS.md 284).
  const ghost = adjusted && total > 0 ? Math.max(0, Math.min(1, s.leftToSpendCents / total)) : 0
  // At rest it's the server's status (the month's, so not while adjusted); on the way there, the same lines applied to the number shown.
  const level =
    left === target && !adjusted ? (status === 'overspent' ? 'over' : status === 'low' ? 'near-zero' : levelAt(left, total)) : levelAt(left, total)
  return { left, target, share, ghost, level, intro, adjusted: adjusted !== null }
}

export default function HomeScreen({ me, onSignedOut, onNoticeCount }: { me: Me; onSignedOut: () => void; onNoticeCount?: (n: number) => void }) {
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
  // The bubble on the Home tab (DECISIONS.md 283, 294): how many cards here you can do something about now.
  useEffect(() => {
    if (summary) onNoticeCount?.(homeBadge(summary, !!savedAt))
  }, [summary, savedAt, onNoticeCount])

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
    <div className="mx-auto flex min-h-dvh max-w-md flex-col" onTouchStart={onTouchStart} onTouchMove={onTouchMove} onTouchEnd={onTouchEnd}>
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

/**
 * Every spending category as a thin row under Left to Spend (DECISIONS.md 254, 255): its name, what's left, and a bar
 * filled and colored the way its line on the Budget tab is (how much of the limit is used; green, gold when low, red
 * when over). A tap opens the category.
 */
const overviewHref = { bills: '#/budget', spending: '#/budget', debt: '#/debt', savings: '#/savings' } as const

/**
 * Home's Month overview (DECISIONS.md 286): the month's math in one place. Money in at the top (received of expected),
 * then minus each kind of plan (spent or paid of its limit), minus carried-over overspending and spending not in a
 * category when there is any, equals what isn't planned yet. Each line opens its tab.
 */
function MonthOverviewLines({ o }: { o: MonthOverview }) {
  const short = o.notPlannedCents < 0
  return (
    <ul aria-label="This month's money" className="text-xs">
      <OverviewLine label="Income" href="#/income" done={o.incomeReceivedCents} of={o.incomeExpectedCents} bar="bg-good" />
      {o.lines.map((l) => {
        const over = l.spentCents > l.limitCents && l.limitCents > 0
        const filled = l.limitCents > 0 && l.spentCents >= l.limitCents
        const bar = over && (l.key === 'spending' || l.key === 'bills') ? 'bg-danger' : l.key === 'spending' ? 'bg-accent' : filled ? 'bg-good' : 'bg-gold'
        return <OverviewLine key={l.key} minus label={l.label} href={overviewHref[l.key]} done={l.spentCents} of={l.limitCents} bar={bar} />
      })}
      {o.carriedOverCents > 0 && <OverviewLine minus label="Carried over" href="#/budget" amount={o.carriedOverCents} danger />}
      {o.notInCategoryCents > 0 && <OverviewLine minus label="Not in a category" href="#/tasks/transactions" amount={o.notInCategoryCents} danger />}
      <li className="mt-1 border-t border-ink/10 pt-1">
        <a href="#/budget/plan" className="flex items-baseline justify-between gap-3 py-1.5">
          <span className="font-semibold">= {short ? 'Planned beyond your money' : 'Not planned yet'}</span>
          <span className={`font-semibold tabular-nums ${short ? 'text-danger' : ''}`}>
            {short ? '−' : ''}
            {formatCents(Math.abs(o.notPlannedCents))}
          </span>
        </a>
      </li>
    </ul>
  )
}

/** One Month overview line: name, a bar of done ÷ of, and "$X of $Y"; or just an amount for a line with no plan. */
function OverviewLine(p: { label: string; href: string; minus?: boolean; done?: number; of?: number; bar?: string; amount?: number; danger?: boolean }) {
  const used = p.of && p.of > 0 ? Math.min(1, Math.max(0, (p.done ?? 0) / p.of)) : 0
  return (
    <li>
      <a href={p.href} className="grid grid-cols-[6.5rem_minmax(0,1fr)_auto] items-center gap-3 py-1.5">
        <span className="truncate text-ink-soft">
          <span aria-hidden className="inline-block w-3 text-ink-faint">
            {p.minus ? '−' : ''}
          </span>
          {p.label}
        </span>
        {p.amount === undefined ? (
          <span aria-hidden className="block h-2 overflow-hidden rounded-full neu-inset">
            <span className={`block h-full rounded-full ${p.bar}`} style={{ width: `${used * 100}%` }} />
          </span>
        ) : (
          <span />
        )}
        <span className={`text-right tabular-nums ${p.danger ? 'font-semibold text-danger' : ''}`}>
          {p.amount !== undefined ? (
            formatCents(p.amount)
          ) : (
            <>
              <span className="font-semibold">{formatCents(p.done ?? 0)}</span> <span className="text-ink-faint">of {formatCents(p.of ?? 0)}</span>
            </>
          )}
        </span>
      </a>
    </li>
  )
}

function SpendingRows({ items }: { items: NonNullable<HomeSummary['spending']> }) {
  return (
    <ul aria-label="Left in each spending category" className="mt-4">
      {items.map((c) => {
        const used = c.limitCents > 0 ? Math.min(1, Math.max(0, c.spentCents / c.limitCents)) : c.spentCents > 0 ? 1 : 0
        const over = c.leftCents < 0
        const maxed = !over && isMaxed(c.spentCents, c.leftCents) // DECISIONS.md 280
        return (
          <li key={c.id}>
            <a
              href={`#/budget/c/${c.id}`}
              aria-label={`${c.name}: ${over ? `${formatCents(-c.leftCents)} over` : maxed ? 'maxed' : `${formatCents(c.leftCents)} left of ${formatCents(c.limitCents)}`}`}
              className="grid grid-cols-[minmax(0,1fr)_auto_5rem] items-center gap-3 py-1.5 text-xs"
            >
              <span className="truncate text-ink-soft">{c.name}</span>
              <span aria-hidden className={`text-right font-semibold tabular-nums ${over || maxed ? 'text-danger' : ''}`}>
                {over ? `${formatCents(-c.leftCents)} over` : maxed ? 'Maxed' : formatCents(c.leftCents)}
              </span>
              <span aria-hidden className="block h-2 overflow-hidden rounded-full neu-inset">
                <span className={`block h-full rounded-full ${spendingBar(c.health, c.spentCents, c.leftCents)}`} style={{ width: `${used * 100}%` }} />
              </span>
            </a>
          </li>
        )
      })}
    </ul>
  )
}

/**
 * A font size that fits `text` in `box` (DECISIONS.md 262): `maxPx` while it fits, smaller when it doesn't. It measures
 * the text in a hidden copy at `maxPx` in the real font, again when the box resizes and once the web fonts load, so
 * it's right on every phone; a guess at glyph widths ran past the ring on an iPhone.
 */
function useFitText(text: string, maxPx: number) {
  const box = useRef<HTMLDivElement>(null)
  const probe = useRef<HTMLSpanElement>(null)
  const [size, setSize] = useState(maxPx)
  useLayoutEffect(() => {
    const b = box.current
    if (!b) return
    const fit = () => {
      const p = probe.current
      if (!p) return // gone already
      const room = b.clientWidth * 0.9 // a little air between the amount and the ring
      const wide = p.getBoundingClientRect().width
      setSize(wide > room && wide > 0 ? Math.max(12, Math.floor((maxPx * room * 10) / wide) / 10) : maxPx)
    }
    fit()
    const watch = new ResizeObserver(fit)
    watch.observe(b)
    document.fonts?.ready.then(fit).catch(() => undefined)
    return () => watch.disconnect()
  }, [text, maxPx])
  return { box, probe, size }
}

/** Left to Spend in a pressed-in well, with a ring for the share of the variable budget still left. */
function Gauge({ s, shown }: { s: HomeSummary; shown: ReturnType<typeof useShownLeft> }) {
  const style = levels[shown.level]
  // The amount fits inside the ring (DECISIONS.md 262): full size while it fits, smaller for a big one (a bonus on the way).
  // Sized from the final amount, so the count-up doesn't change size as it runs.
  const amount = formatCents(shown.target)
  const { box: fitBox, probe: fitProbe, size: fitSize } = useFitText(amount, 36)
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
          {shown.ghost > 0 && (
            <circle
              cx="100"
              cy="100"
              r={r}
              fill="none"
              strokeWidth="10"
              strokeLinecap="round"
              className="stroke-ink-soft/60 transition-[stroke-dasharray] duration-500"
              strokeDasharray={`${arc * shown.ghost} 999`}
            />
          )}
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
      <div ref={fitBox} className="relative w-[76%] text-center">
        {/* The final amount at full size, measured but never seen. */}
        <span aria-hidden className="pointer-events-none invisible absolute top-0 left-0 size-0 overflow-hidden">
          <span ref={fitProbe} className="inline-block font-bold tracking-tight whitespace-nowrap tabular-nums" style={{ fontSize: 36 }}>
            {amount}
          </span>
        </span>
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
          {shown.adjusted ? 'Safe to Spend' : 'Left to Spend'}
          {shown.adjusted && (
            <span role="img" aria-label="Caution" className="ml-1">
              ⚠️
            </span>
          )}
        </h2>
        <p
          data-testid="left-to-spend"
          className="font-bold leading-tight tracking-tight whitespace-nowrap tabular-nums"
          style={{ fontSize: fitSize }}
          aria-label={amount}
        >
          {formatCents(shown.left)}
        </p>
        <p className="mt-0.5 text-xs text-ink-faint">
          {/* Just the day; the line under the ring says what lands then (DECISIONS.md 292). */}
          {s.safeToSpend ? `until ${formatDay(s.safeToSpend.untilDate)}` : `${s.daysRemaining} ${s.daysRemaining === 1 ? 'day' : 'days'} left`}
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
  const notices = homeNotices(s, stale)

  // A notice that turns up while you're looking (after a sync, say) slides in; the ones already there don't.
  const isNew = useArrivals(notices.map((n) => n.key))

  return (
    <>
      <section aria-labelledby="lts-label" className="neu mt-5 rounded-[2rem] px-5 pt-6 pb-5">
        <Gauge s={s} shown={shown} />
        {/* Safe to Spend (DECISIONS.md 276): while checking can't cover Left to Spend until payday, the number is what it
            can. It's Home's one cash warning (275); the payday tasks say which bills and transfers come first. */}
        {s.safeToSpend && (
          <a href="#/tasks/payday" className="mt-3 block text-center text-xs font-medium text-ink-soft">
            Checking can't cover Left to Spend until {s.safeToSpend.until}.
          </a>
        )}
        <div className="mt-4 text-center">
          <Pill tone={style.tone}>{style.label}</Pill>
        </div>
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

      <Card title="Month overview">
        <div className="px-5 py-4">
        {s.overview && <MonthOverviewLines o={s.overview} />}
        {/* Only when the budget couldn't shrink enough to fit the accounts (DECISIONS.md 236): otherwise there's nothing to act on. */}
        {s.cash && s.cashShortCents > 0 && (
          <div className="-mx-5">
            <Rows>
              <CashCheckRow cash={s.cash} shortCents={s.cashShortCents} pickUp={s.pickUp ?? null} />
            </Rows>
          </div>
        )}
        {s.nextPayday && (
          <a href="#/income" className="mt-3 flex items-baseline justify-between gap-3 border-t border-ink/10 pt-3 text-xs text-ink-faint">
            <span>
              Payday {formatDay(s.nextPayday.payDate)}
              {s.nextPayday.hasSchedule
                ? s.nextPayday.lowNetCents === s.nextPayday.highNetCents
                  ? ''
                  : ` · ${formatCents(s.nextPayday.lowNetCents)}–${formatCents(s.nextPayday.highNetCents)}`
                : ' · import your schedule to forecast it'}
              {s.nextPayday.hasSchedule && s.nextPayday.lowConfidence ? ' · rough guess' : ''}
            </span>
            {s.nextPayday.hasSchedule && <span className="shrink-0 tabular-nums">≈ {formatCents(s.nextPayday.netCents)}</span>}
          </a>
        )}
        </div>
      </Card>
    </>
  )
}

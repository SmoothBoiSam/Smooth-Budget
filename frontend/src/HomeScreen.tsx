import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, api, budgetApi, plaidApi, type CashCheck, type Category, type HomeSummary, type Me, type Status, type Transaction } from './api'
import { TransactionList } from './BudgetScreen'
import TransactionSheet from './TransactionSheet'
import { formatCents, formatDay, formatTime } from './format'
import { saved, useOnline } from './offline'
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
function levelOf(s: { status: Status; leftToSpendCents: number; totalVariableLimitCents: number }) {
  const level: Level =
    s.status === 'overspent'
      ? 'over'
      : s.status === 'low'
        ? 'near-zero'
        : s.leftToSpendCents * 100 < 40 * s.totalVariableLimitCents
          ? 'getting-low'
          : 'plenty'
  return levels[level]
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

  // Pull to refresh (SPEC section 4.2) and the Refresh link both sync connected banks, then reload.
  async function refresh() {
    setLoading(true)
    setSyncing(true)
    await plaidApi.syncAll().catch(() => undefined) // a failed sync shows up in summary.sync
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

      <main className="flex-1 px-5 pb-32">
        {error && (
          <div className="mt-6">
            <Notice tone="danger">
              Couldn’t load Left to Spend. {error}{' '}
              <button type="button" onClick={refresh} className="font-semibold text-coral">
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
              <button type="button" onClick={refresh} disabled={loading} className="font-semibold text-coral disabled:opacity-50">
                {loading ? 'Trying…' : 'Try again'}
              </button>
            </Notice>
          </div>
        )}

        {summary && <Summary summary={summary} stale={!!savedAt} />}
        {summary && online && <RecentTransactions summary={summary} onChanged={() => void load()} />}

        {summary && (
          <p className="mt-8 text-center text-xs text-ink-faint">
            {summary.sync.state !== 'none' && summary.sync.lastSyncedUtc
              ? `Synced ${formatTime(summary.sync.lastSyncedUtc)}`
              : summary.lastUpdatedUtc
                ? `Updated ${formatTime(summary.lastUpdatedUtc)}`
                : 'No transactions yet'}
            {' · '}
            <button type="button" onClick={refresh} disabled={loading} className="font-semibold text-coral disabled:opacity-50">
              {loading ? 'Syncing…' : 'Refresh'}
            </button>
          </p>
        )}
      </main>
    </div>
  )
}

/** Whole days from one ISO date to another. */
function daysBetween(from: string, to: string) {
  const day = (iso: string) => {
    const [y, m, d] = iso.split('-').map(Number)
    return Date.UTC(y, m - 1, d) / 86_400_000
  }
  return Math.round(day(to) - day(from))
}

/** Left to Spend in a pressed-in well, with a ring for the share of the variable budget still left. */
function Gauge({ s }: { s: HomeSummary }) {
  const style = levelOf(s)
  const share = s.totalVariableLimitCents > 0 ? Math.max(0, Math.min(1, s.leftToSpendCents / s.totalVariableLimitCents)) : 0
  const r = 88
  const arc = 2 * Math.PI * r * 0.75 // a three-quarter ring, open at the bottom
  return (
    <div className="neu-inset relative mx-auto grid aspect-square w-full max-w-60 place-items-center rounded-full">
      <svg viewBox="0 0 200 200" aria-hidden className="absolute inset-0 size-full">
        <g transform="rotate(135 100 100)">
          <circle cx="100" cy="100" r={r} fill="none" strokeWidth="10" strokeLinecap="round" className="stroke-ink/10" strokeDasharray={`${arc} 999`} />
          {share > 0 && (
            <circle cx="100" cy="100" r={r} fill="none" strokeWidth="10" strokeLinecap="round" className={style.ring} strokeDasharray={`${arc * share} 999`} />
          )}
        </g>
      </svg>
      <div className="relative text-center">
        <img src={style.cat.src} alt={style.cat.alt} className="mx-auto size-16 object-contain" />
        <h2 id="lts-label" className="mt-1 text-xs font-medium text-ink-soft">
          Left to Spend
        </h2>
        <p data-testid="left-to-spend" className="text-4xl font-bold tracking-tight tabular-nums">
          {formatCents(s.leftToSpendCents)}
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
 * go out this month. Folded, it says whether the accounts cover Left to Spend; open, it shows every part.
 */
function CashCheckRow({ cash: c, shortCents }: { cash: CashCheck; shortCents: number }) {
  const rows: [string, number, 1 | -1][] = [
    ['In checking now', c.checkingCents, 1],
    ['Pay still coming', c.incomeStillComingCents, 1],
    ['Partner money still coming', c.partnerStillComingCents, 1],
    ['Bills not paid yet', c.billsStillDueCents, -1],
    ['Cards to pay off', c.cardsToPayCents, -1],
    ['Savings not moved yet', c.savingsStillToMoveCents, -1],
    ['Last month’s leftover to move', c.leftoverToMoveCents, -1],
  ]
  return (
    <li>
      <details className="group">
        <summary className="flex cursor-pointer list-none items-center gap-3 px-5 py-3.5 text-sm">
          <span className="flex-1">Your accounts</span>
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
        </dl>
      </details>
    </li>
  )
}

function Summary({ summary: s, stale }: { summary: HomeSummary; stale: boolean }) {
  const style = levelOf(s)
  // What lasts until the next check: the safe daily amount for each day before payday (DECISIONS.md 148).
  const daysToPayday = s.nextPayday ? daysBetween(s.today, s.nextPayday.payDate) : 0
  const untilPayday =
    s.nextPayday && daysToPayday > 0 && daysToPayday < s.daysRemaining
      ? { date: s.nextPayday.payDate, cents: Math.max(0, Math.min(s.leftToSpendCents, s.safeToSpendPerDayCents * daysToPayday)) }
      : null

  // Everything that wants a tap, most urgent first: problems, then things to do, then good news.
  const notices: { key: string; tone: 'danger' | 'todo' | 'good'; href?: string; text: React.ReactNode }[] = []
  if (!stale && (s.sync.state === 'stale' || s.sync.state === 'error'))
    notices.push({ key: 'sync', tone: s.sync.state === 'error' ? 'danger' : 'todo', href: '#/more', text: s.sync.message ?? 'Bank sync needs a look.' })
  for (const w of s.cashWarnings ?? [])
    notices.push({
      key: `cash-${w.account}`,
      tone: 'danger',
      href: '#/payday',
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
      href: '#/partner',
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
    notices.push({ key: 'uncat', tone: 'todo', href: '#/review', text: `${s.uncategorizedCount} ${s.uncategorizedCount === 1 ? 'purchase needs' : 'purchases need'} a category` })
  if (s.reviewCount > 0)
    notices.push({ key: 'review', tone: 'todo', href: '#/review', text: `${s.reviewCount} ${s.reviewCount === 1 ? 'purchase' : 'purchases'} to confirm` })
  for (const p of s.pendingSweeps)
    notices.push({
      key: `sweep-${p.id}`,
      tone: 'good',
      href: '#/payday',
      text: (
        <>
          Send <b className="tabular-nums">{formatCents(p.amountCents)}</b> of {p.fromMonth.split(' ')[0]}’s leftover to {p.destination}
        </>
      ),
    })

  return (
    <>
      <section aria-labelledby="lts-label" className="neu mt-5 rounded-[2rem] px-5 pt-6 pb-5">
        <Gauge s={s} />
        <div className="mt-4 text-center">
          <Pill tone={style.tone}>{style.label}</Pill>
        </div>
        <dl className="mt-5 grid grid-cols-2 gap-3">
          <div className="neu-inset rounded-2xl px-4 py-3">
            <dt className="text-xs text-ink-soft">Safe per day</dt>
            <dd className="text-lg font-semibold tabular-nums">{formatCents(s.safeToSpendPerDayCents)}</dd>
          </div>
          <div className="neu-inset rounded-2xl px-4 py-3">
            <dt className="text-xs text-ink-soft">{untilPayday ? `Until payday ${formatDay(untilPayday.date)}` : 'Days left'}</dt>
            <dd className="text-lg font-semibold tabular-nums">{untilPayday ? formatCents(untilPayday.cents) : s.daysRemaining}</dd>
          </div>
        </dl>
      </section>

      {notices.length > 0 && (
        <ul className="mt-6 space-y-3">
          {notices.map((n) => (
            <li key={n.key}>
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
          {s.unfiledSpendingCents > 0 && <Row label="Not in a category" value={`−${formatCents(s.unfiledSpendingCents)}`} tone="danger" href="#/review" />}
          {s.overplannedCents > 0 && <Row label="Planned beyond your money" value={`−${formatCents(s.overplannedCents)}`} tone="danger" href="#/budget/plan" />}
          {s.cash && <CashCheckRow cash={s.cash} shortCents={s.cashShortCents} />}
          {s.nextPayday && (
            <Row
              label={`Payday ${formatDay(s.nextPayday.payDate)}`}
              hint={
                s.nextPayday.hasSchedule
                  ? `${formatCents(s.nextPayday.lowNetCents)}–${formatCents(s.nextPayday.highNetCents)}${s.nextPayday.lowConfidence ? ' · rough guess' : ''}`
                  : 'Import your schedule to forecast it'
              }
              value={s.nextPayday.hasSchedule ? `≈ ${formatCents(s.nextPayday.netCents)}` : undefined}
              href="#/payday"
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
          Recent
        </h2>
        <a href="#/transactions" className="text-sm font-semibold text-coral">
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
        <TransactionSheet
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

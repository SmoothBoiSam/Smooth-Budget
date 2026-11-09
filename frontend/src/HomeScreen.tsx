import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, api, budgetApi, plaidApi, type Category, type HomeSummary, type Me, type Status, type Transaction } from './api'
import { TransactionList } from './BudgetScreen'
import TransactionSheet from './TransactionSheet'
import { formatCents, formatDay, formatTime } from './format'
import { saved, useOnline } from './offline'
import { Loading } from './ui'

/** Your status cats (DECISIONS.md 139): plenty left, running low, over budget. */
const statusCats: Record<Status, { src: string; alt: string }> = {
  healthy: { src: '/brand/cat-plenty.png', alt: 'A happy cat holding a stack of coins' },
  low: { src: '/brand/cat-low.png', alt: 'A worried cat holding a few coins' },
  overspent: { src: '/brand/cat-over.png', alt: 'A crying cat' },
}

const statusStyles: Record<Status, { number: string; badge: string; label: string }> = {
  healthy: { number: 'text-emerald-600', badge: 'bg-emerald-100 text-emerald-800', label: 'On track' },
  low: { number: 'text-amber-500', badge: 'bg-amber-100 text-amber-800', label: 'Running low' },
  overspent: { number: 'text-red-600', badge: 'bg-red-100 text-red-800', label: 'Overspent' },
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
    <div
      className="mx-auto flex min-h-screen max-w-md flex-col bg-slate-50"
      onTouchStart={onTouchStart}
      onTouchMove={onTouchMove}
      onTouchEnd={onTouchEnd}
    >
      {/* Easter egg (DECISIONS.md 139): the tabby hangs down from the top of the screen as you pull to sync, and swings while it syncs. */}
      <div aria-hidden className="pointer-events-none fixed inset-x-0 top-0 z-30 flex justify-center">
        <div className={pull > 0 ? '' : 'transition-transform duration-300 ease-out'} style={{ transform: `translateY(${(syncing ? 100 : pull * 1.1) - 143}px)` }}>
          <img src="/brand/cat-tabby.png" alt="" className={`w-32 origin-top ${syncing ? 'animate-tabby-swing' : ''}`} />
        </div>
      </div>
      {(pull > 0 || (loading && summary)) && (
        <div
          aria-live="polite"
          className="flex items-end justify-center overflow-hidden text-xs font-medium text-slate-500 transition-[height]"
          style={{ height: syncing ? 108 : loading ? 32 : pull }}
        >
          <span className="pb-1">{loading ? 'Syncing…' : pull >= PULL_TRIGGER ? 'Release to sync' : 'Pull to sync'}</span>
        </div>
      )}
      {/* Sign out lives in More → Settings only (DECISIONS.md 160). */}
      <header className="px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-2">
        <p className="text-xs font-medium uppercase tracking-wide text-slate-500">
          {summary ? summary.budgetMonth.label : 'This month'}
        </p>
        {summary && (
          <p className="text-xs text-slate-400">
            {formatDay(summary.budgetMonth.start)} – {formatDay(summary.budgetMonth.end)}
            {savedAt && !online && ` · as of ${formatTime(savedAt)}`}
          </p>
        )}
      </header>

      <main className="flex-1 px-5 pb-28">
        {error && (
          <div role="alert" className="mt-6 rounded-2xl bg-red-50 p-4 text-sm text-red-800">
            <p className="font-semibold">Couldn't load Left to Spend.</p>
            <p className="mt-1">{error}</p>
            <button type="button" onClick={refresh} className="mt-3 font-semibold underline">
              Try again
            </button>
          </div>
        )}

        {!summary && !error && <Loading label="Loading Left to Spend" />}

        {/* With the phone offline, the bar at the top already says so: one message, not two (DECISIONS.md 160). This card is for a server that can't be reached. */}
        {savedAt && online && (
          <div role="alert" className="mt-4 rounded-xl bg-amber-50 px-4 py-3 text-sm text-amber-900">
            <span className="font-semibold">Offline: last known numbers. </span>
            This is your Left to Spend as of {formatTime(savedAt)}. It may have changed since; it updates when you’re back online.
            <button type="button" onClick={refresh} disabled={loading} className="mt-1 block font-semibold underline disabled:opacity-50">
              {loading ? 'Trying…' : 'Try again'}
            </button>
          </div>
        )}

        {summary && !savedAt && (summary.sync.state === 'stale' || summary.sync.state === 'error') && (
          <a
            href="#/more"
            role="alert"
            className={`mt-4 block rounded-xl px-4 py-3 text-sm ${summary.sync.state === 'error' ? 'bg-red-50 text-red-800' : 'bg-amber-50 text-amber-900'}`}
          >
            <span className="font-semibold">{summary.sync.state === 'error' ? 'Bank sync problem. ' : 'Data may be out of date. '}</span>
            {summary.sync.message}
          </a>
        )}

        {summary && <Summary summary={summary} />}
        {summary && online && <RecentTransactions summary={summary} onChanged={() => void load()} />}

        {summary && (
          <div className="mt-6 flex items-center justify-center gap-2 text-xs text-slate-400">
            <span>
              {summary.sync.state !== 'none' && summary.sync.lastSyncedUtc
                ? `Synced ${formatTime(summary.sync.lastSyncedUtc)}`
                : summary.lastUpdatedUtc
                  ? `Data updated ${formatTime(summary.lastUpdatedUtc)}`
                  : 'No transactions yet'}
            </span>
            <span aria-hidden>·</span>
            <button type="button" onClick={refresh} disabled={loading} className="font-medium text-slate-600 underline disabled:opacity-50">
              {loading ? 'Syncing…' : 'Refresh'}
            </button>
          </div>
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

function Summary({ summary: s }: { summary: HomeSummary }) {
  const style = statusStyles[s.status]
  // What lasts until the next check: the safe daily amount for each day before payday (DECISIONS.md 148).
  const daysToPayday = s.nextPayday ? daysBetween(s.today, s.nextPayday.payDate) : 0
  const untilPayday =
    s.nextPayday && daysToPayday > 0 && daysToPayday < s.daysRemaining
      ? { date: s.nextPayday.payDate, cents: Math.max(0, Math.min(s.leftToSpendCents, s.safeToSpendPerDayCents * daysToPayday)) }
      : null
  return (
    <>
      <section aria-labelledby="lts-label" className="mt-6 rounded-3xl bg-white p-6 text-center shadow-sm ring-1 ring-slate-200">
        <img src={statusCats[s.status].src} alt={statusCats[s.status].alt} className="mx-auto -mt-2 mb-1 size-28 object-contain" />
        <h1 id="lts-label" className="text-sm font-medium text-slate-500">
          Left to Spend
        </h1>
        <p data-testid="left-to-spend" className={`mt-2 text-6xl font-bold tracking-tight tabular-nums ${style.number}`}>
          {formatCents(s.leftToSpendCents)}
        </p>
        <span className={`mt-3 inline-block rounded-full px-3 py-1 text-xs font-semibold ${style.badge}`}>{style.label}</span>
        <p className="mt-2 text-xs text-slate-500">
          {s.daysRemaining} {s.daysRemaining === 1 ? 'day' : 'days'} left in {s.budgetMonth.label.split(' ')[0]}’s budget
        </p>

        <dl className="mt-5 grid grid-cols-2 gap-3 text-left">
          <div className="rounded-2xl bg-slate-50 p-3">
            <dt className="text-xs text-slate-500">Safe per day</dt>
            <dd className="text-xl font-semibold tabular-nums">{formatCents(s.safeToSpendPerDayCents)}</dd>
          </div>
          {untilPayday ? (
            <div className="rounded-2xl bg-slate-50 p-3">
              <dt className="text-xs text-slate-500">Until payday {formatDay(untilPayday.date)}</dt>
              <dd className="text-xl font-semibold tabular-nums">{formatCents(untilPayday.cents)}</dd>
            </div>
          ) : (
            <div className="rounded-2xl bg-slate-50 p-3">
              <dt className="text-xs text-slate-500">Days left</dt>
              <dd className="text-xl font-semibold tabular-nums">{s.daysRemaining}</dd>
            </div>
          )}
        </dl>
      </section>

      <ul className="mt-4 space-y-2 text-sm">
        {s.carriedInDeficitCents > 0 && (
          <li className="flex justify-between rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
            <span className="text-slate-600">Carried over</span>
            <span className="font-semibold tabular-nums text-red-600">−{formatCents(s.carriedInDeficitCents)}</span>
          </li>
        )}
        {s.unfiledSpendingCents > 0 && (
          <li>
            <a href="#/review" className="flex justify-between gap-3 rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
              <span className="text-slate-600">Not filed in a category yet</span>
              <span className="font-semibold tabular-nums text-red-600">−{formatCents(s.unfiledSpendingCents)} ›</span>
            </a>
          </li>
        )}
        {s.overplannedCents > 0 && (
          <li>
            <a href="#/budget/plan" className="flex justify-between gap-3 rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
              <span className="text-slate-600">Planned beyond this month’s money</span>
              <span className="font-semibold tabular-nums text-red-600">−{formatCents(s.overplannedCents)} ›</span>
            </a>
          </li>
        )}
        {s.incomeUnknown && (
          <li>
            <a href="#/budget/plan" className="block rounded-xl bg-amber-50 px-4 py-3 text-amber-900">
              No income is set for this month, so Left to Spend can’t check your plan against your money.{' '}
              <span className="font-semibold">Set it ›</span>
            </a>
          </li>
        )}
        {(s.cashWarnings ?? []).map((w) => (
          <li key={w.account}>
            <a href="#/payday" className="flex justify-between gap-3 rounded-xl bg-red-50 px-4 py-3 text-red-800">
              <span>
                {w.account} is <span className="font-semibold tabular-nums">{formatCents(w.shortCents)}</span> short for the{' '}
                {w.billCount === 1 ? 'bill' : `${w.billCount} bills`} ({formatCents(w.billsCents)}) due before
                {w.nextPayDate ? ` payday ${formatDay(w.nextPayDate)}` : ' your next payday'}.
              </span>
              <span aria-hidden className="font-semibold">›</span>
            </a>
          </li>
        ))}
        {s.pendingSweeps.map((p) => (
          <li key={p.id}>
            <a href="#/payday"className="flex justify-between gap-3 rounded-xl bg-emerald-50 px-4 py-3 text-emerald-900">
              <span>
                Leftover from {p.fromMonth.split(" ")[0]}: send <span className="font-semibold tabular-nums">{formatCents(p.amountCents)}</span> to {p.destination}
              </span>
              <span aria-hidden className="font-semibold">›</span>
            </a>
          </li>
        ))}
        {s.overBudgetCategoryCount > 0 && (
          <li className="rounded-xl bg-red-50 px-4 py-3 text-red-800">
            Over budget by <span className="font-semibold tabular-nums">{formatCents(s.overBudgetCents)}</span> in{' '}
            {s.overBudgetCategoryCount} {s.overBudgetCategoryCount === 1 ? 'category' : 'categories'}
          </li>
        )}
        {(s.contributions ?? [])
          .filter((f) => f.state === 'late' || (f.state === 'differentAmount' && f.shortfallCents > 0))
          .map((f) => (
            <li key={f.arrangementId}>
              <a href="#/partner" className="flex justify-between gap-3 rounded-xl bg-red-50 px-4 py-3 text-red-800">
                <span>
                  {f.state === 'late'
                    ? `The ${formatCents(f.expectedCents)} toward ${f.categoryName} hasn't come yet.`
                    : `Only ${formatCents(f.receivedCents)} of ${formatCents(f.expectedCents)} came toward ${f.categoryName}.`}{' '}
                  Left to Spend counts without the missing <span className="font-semibold tabular-nums">{formatCents(f.shortfallCents)}</span>.
                </span>
                <span aria-hidden className="font-semibold">›</span>
              </a>
            </li>
          ))}
        {s.reviewCount > 0 && (
          <li>
            <a href="#/review" className="flex justify-between gap-3 rounded-xl bg-amber-50 px-4 py-3 text-amber-900">
              <span>
                {s.reviewCount} {s.reviewCount === 1 ? "purchase" : "purchases"} this month to confirm
              </span>
              <span aria-hidden className="font-semibold">›</span>
            </a>
          </li>
        )}
        {s.uncategorizedCount > 0 && (
          <li>
            <a href="#/review" className="flex justify-between gap-3 rounded-xl bg-amber-50 px-4 py-3 text-amber-900">
              <span>
                {s.uncategorizedCount} {s.uncategorizedCount === 1 ? 'purchase' : 'purchases'} this month{' '}
                {s.uncategorizedCount === 1 ? 'needs' : 'need'} a category and {s.uncategorizedCount === 1 ? "isn't" : "aren't"} counted yet
              </span>
              <span aria-hidden className="font-semibold">›</span>
            </a>
          </li>
        )}
        <li className="flex justify-between rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
          <span className="text-slate-600">Spent of variable budget</span>
          <span className="tabular-nums">
            {formatCents(s.totalVariableSpentCents)} <span className="text-slate-400">/ {formatCents(s.totalVariableLimitCents)}</span>
          </span>
        </li>
      </ul>

      {s.nextPayday && (
        <a href="#/payday" className="mt-4 flex items-center justify-between gap-3 rounded-xl bg-white px-4 py-3 text-sm ring-1 ring-slate-200">
          <span>
            <span className="font-medium">Next payday · {formatDay(s.nextPayday.payDate)}</span>
            <span className="block text-xs text-slate-500">
              {s.nextPayday.hasSchedule
                ? `Range ${formatCents(s.nextPayday.lowNetCents)}–${formatCents(s.nextPayday.highNetCents)}${s.nextPayday.lowConfidence ? ' · low confidence' : ''}`
                : 'Import the schedule to forecast it'}
            </span>
          </span>
          {s.nextPayday.hasSchedule && <span className="shrink-0 font-semibold tabular-nums">about {formatCents(s.nextPayday.netCents)}</span>}
        </a>
      )}

    </>
  )
}

/**
 * The newest few transactions, any month, under the payday card (DECISIONS.md 171). Tap one to change it; "All
 * transactions" opens the full list and search. Reloads whenever Home does, so a pull to refresh shows new ones.
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
      <h2 id="recent-h" className="text-sm font-semibold text-slate-700">
        Recent transactions
      </h2>
      {notice && (
        <p role="status" className="mt-2 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {items === null ? (
        <p className="mt-2 text-sm text-slate-400">Loading…</p>
      ) : (
        <TransactionList items={items} empty="No transactions yet." onSelect={setSelected} />
      )}
      <a href="#/transactions" className="mt-3 flex items-center justify-between rounded-xl bg-white px-4 py-3 text-sm font-medium ring-1 ring-slate-200">
        All transactions and search
        <span aria-hidden>›</span>
      </a>
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

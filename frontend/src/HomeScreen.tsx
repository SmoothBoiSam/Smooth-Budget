import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, api, plaidApi, type HomeSummary, type Me, type Status } from './api'
import { formatCents, formatDay, formatTime } from './format'

const statusStyles: Record<Status, { number: string; badge: string; label: string }> = {
  healthy: { number: 'text-emerald-600', badge: 'bg-emerald-100 text-emerald-800', label: 'On track' },
  low: { number: 'text-amber-500', badge: 'bg-amber-100 text-amber-800', label: 'Running low' },
  overspent: { number: 'text-red-600', badge: 'bg-red-100 text-red-800', label: 'Overspent' },
}

export default function HomeScreen({ me, onSignedOut }: { me: Me; onSignedOut: () => void }) {
  const [summary, setSummary] = useState<HomeSummary | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    try {
      setSummary(await api.homeSummary())
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Could not load your summary.')
    } finally {
      setLoading(false)
    }
  }, [onSignedOut])

  useEffect(() => {
    void load()
  }, [load])

  // Pull to refresh (SPEC section 4.2) and the Refresh link both sync connected banks, then reload.
  async function refresh() {
    setLoading(true)
    await plaidApi.syncAll().catch(() => undefined) // a failed sync shows up in summary.sync
    await load()
  }

  const startY = useRef<number | null>(null)
  const [pull, setPull] = useState(0)
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

  async function signOut() {
    await api.logout().catch(() => undefined)
    onSignedOut()
  }

  return (
    <div
      className="mx-auto flex min-h-screen max-w-md flex-col bg-slate-50"
      onTouchStart={onTouchStart}
      onTouchMove={onTouchMove}
      onTouchEnd={onTouchEnd}
    >
      {(pull > 0 || (loading && summary)) && (
        <div
          aria-live="polite"
          className="flex items-end justify-center overflow-hidden text-xs font-medium text-slate-500 transition-[height]"
          style={{ height: loading ? 32 : pull }}
        >
          <span className="pb-1">{loading ? 'Syncing…' : pull >= PULL_TRIGGER ? 'Release to sync' : 'Pull to sync'}</span>
        </div>
      )}
      <header className="flex items-center justify-between px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-2">
        <div>
          <p className="text-xs font-medium uppercase tracking-wide text-slate-500">
            {summary ? summary.budgetMonth.label : 'This month'}
          </p>
          {summary && (
            <p className="text-xs text-slate-400">
              {formatDay(summary.budgetMonth.start)} – {formatDay(summary.budgetMonth.end)}
            </p>
          )}
        </div>
        <button
          type="button"
          onClick={signOut}
          title={`Signed in as ${me.email}`}
          className="rounded-full px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-200"
        >
          Sign out
        </button>
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

        {!summary && !error && <div className="mt-16 text-center text-slate-400">Loading…</div>}

        {summary && (summary.sync.state === 'stale' || summary.sync.state === 'error') && (
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

function Summary({ summary: s }: { summary: HomeSummary }) {
  const style = statusStyles[s.status]
  return (
    <>
      <section aria-labelledby="lts-label" className="mt-6 rounded-3xl bg-white p-6 text-center shadow-sm ring-1 ring-slate-200">
        <h1 id="lts-label" className="text-sm font-medium text-slate-500">
          Left to Spend
        </h1>
        <p data-testid="left-to-spend" className={`mt-2 text-6xl font-bold tracking-tight tabular-nums ${style.number}`}>
          {formatCents(s.leftToSpendCents)}
        </p>
        <span className={`mt-3 inline-block rounded-full px-3 py-1 text-xs font-semibold ${style.badge}`}>{style.label}</span>

        <dl className="mt-6 grid grid-cols-2 gap-3 text-left">
          <div className="rounded-2xl bg-slate-50 p-3">
            <dt className="text-xs text-slate-500">Days left</dt>
            <dd className="text-xl font-semibold tabular-nums">{s.daysRemaining}</dd>
          </div>
          <div className="rounded-2xl bg-slate-50 p-3">
            <dt className="text-xs text-slate-500">Safe per day</dt>
            <dd className="text-xl font-semibold tabular-nums">{formatCents(s.safeToSpendPerDayCents)}</dd>
          </div>
        </dl>
      </section>

      <ul className="mt-4 space-y-2 text-sm">
        {s.carriedInDeficitCents > 0 && (
          <li className="flex justify-between rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
            <span className="text-slate-600">Carried over</span>
            <span className="font-semibold tabular-nums text-red-600">−{formatCents(s.carriedInDeficitCents)}</span>
          </li>
        )}
        {s.overBudgetCategoryCount > 0 && (
          <li className="rounded-xl bg-red-50 px-4 py-3 text-red-800">
            Over budget by <span className="font-semibold tabular-nums">{formatCents(s.overBudgetCents)}</span> in{' '}
            {s.overBudgetCategoryCount} {s.overBudgetCategoryCount === 1 ? 'category' : 'categories'}
          </li>
        )}
        {s.uncategorizedCount > 0 && (
          <li>
            <a href="#/budget/inbox" className="flex justify-between gap-3 rounded-xl bg-amber-50 px-4 py-3 text-amber-900">
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
    </>
  )
}

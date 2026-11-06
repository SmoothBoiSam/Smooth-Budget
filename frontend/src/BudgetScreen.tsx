import { useCallback, useEffect, useState } from 'react'
import {
  ApiError,
  budgetApi,
  budgetSummaryApi,
  type BudgetCategoryLine,
  type BudgetGroup,
  type BudgetSummary,
  type Category,
  type Health,
  type Transaction,
} from './api'
import { formatCents, formatDay, formatSigned, titleCase } from './format'
import TransactionSheet from './TransactionSheet'

// Sub-views of the Budget tab, from the URL hash:
//   #/budget            categories overview
//   #/budget/inbox      transactions that need a category
//   #/budget/all        every transaction this month
//   #/budget/c/<id>     one category's transactions
type View = { kind: 'overview' } | { kind: 'inbox' } | { kind: 'all' } | { kind: 'category'; id: string }

function viewFromHash(): View {
  const parts = window.location.hash.replace(/^#\/?/, '').split('/')
  if (parts[1] === 'inbox') return { kind: 'inbox' }
  if (parts[1] === 'all') return { kind: 'all' }
  if (parts[1] === 'c' && parts[2]) return { kind: 'category', id: parts[2] }
  return { kind: 'overview' }
}

const healthBar: Record<Health, string> = { onTrack: 'bg-emerald-500', low: 'bg-amber-500', over: 'bg-red-500' }
const statusText: Record<BudgetSummary['status'], string> = {
  healthy: 'text-emerald-600',
  low: 'text-amber-500',
  overspent: 'text-red-600',
}

export default function BudgetScreen({ onChanged, onSignedOut }: { onChanged: () => void; onSignedOut: () => void }) {
  const [view, setView] = useState<View>(viewFromHash)
  const [summary, setSummary] = useState<BudgetSummary | null>(null)
  const [month, setMonth] = useState<Transaction[]>([])
  const [inbox, setInbox] = useState<Transaction[]>([])
  const [categories, setCategories] = useState<Category[]>([])
  const [selected, setSelected] = useState<Transaction | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    const onHash = () => {
      setView(viewFromHash())
      setNotice(null)
      window.scrollTo(0, 0)
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const load = useCallback(async () => {
    try {
      const [s, m, i, c] = await Promise.all([
        budgetSummaryApi.get(),
        budgetApi.monthTransactions(),
        budgetApi.inbox(),
        budgetApi.categories(),
      ])
      setSummary(s)
      setMonth(m.items)
      setInbox(i)
      setCategories(c)
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Could not load your budget.')
    }
  }, [onSignedOut])

  useEffect(() => {
    void load()
  }, [load])

  function saved(message: string) {
    setSelected(null)
    setNotice(message)
    onChanged()
    void load()
  }

  const line =
    view.kind === 'category' ? summary?.groups.flatMap((g) => g.categories).find((c) => c.id === view.id) : undefined

  return (
    <div className="mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">
      {view.kind === 'overview' ? (
        <header>
          <h1 className="text-2xl font-bold tracking-tight">Budget</h1>
          {summary && (
            <p className="text-sm text-slate-500">
              {summary.budgetMonth.label} · {formatDay(summary.budgetMonth.start)} – {formatDay(summary.budgetMonth.end)}
            </p>
          )}
        </header>
      ) : (
        <header>
          <a href="#/budget" className="text-sm font-medium text-slate-500">
            ‹ Budget
          </a>
          <h1 className="mt-1 text-2xl font-bold tracking-tight">
            {view.kind === 'inbox' ? 'Needs a category' : view.kind === 'all' ? 'All transactions' : (line?.name ?? 'Category')}
          </h1>
        </header>
      )}

      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && (
        <p role="alert" className="mt-4 rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">
          {error}
        </p>
      )}
      {!summary && !error && <p className="mt-10 text-center text-slate-400">Loading…</p>}

      {summary && view.kind === 'overview' && <Overview summary={summary} inboxCount={inbox.length} />}

      {summary && view.kind === 'category' && line && (
        <>
          <CategoryHeader line={line} />
          <TransactionList
            items={month.filter((t) => t.categoryId === line.id)}
            empty="Nothing in this category yet this month."
            onSelect={setSelected}
          />
        </>
      )}
      {summary && view.kind === 'category' && !line && (
        <p className="mt-6 text-sm text-slate-500">This category no longer exists.</p>
      )}

      {summary && view.kind === 'inbox' && (
        <>
          <p className="mt-2 text-sm text-slate-500">
            These aren't counted in Left to Spend until they have a category. Tap one to set it.
          </p>
          <TransactionList items={inbox} empty="All caught up. Every purchase has a category." onSelect={setSelected} />
        </>
      )}

      {summary && view.kind === 'all' && (
        <TransactionList items={month} empty="No transactions this month yet. Import a CSV from More." onSelect={setSelected} />
      )}

      {selected && (
        <TransactionSheet transaction={selected} categories={categories} onClose={() => setSelected(null)} onSaved={saved} />
      )}
    </div>
  )
}

function Overview({ summary: s, inboxCount }: { summary: BudgetSummary; inboxCount: number }) {
  return (
    <>
      <section className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
        <div className="flex items-baseline justify-between gap-3">
          <h2 className="text-sm font-medium text-slate-500">Left to Spend</h2>
          <p className={`text-2xl font-bold tabular-nums ${statusText[s.status]}`}>{formatCents(s.leftToSpendCents)}</p>
        </div>
        <p className="mt-1 text-xs text-slate-500">
          {formatCents(s.totalVariableSpentCents)} spent of {formatCents(s.totalVariableLimitCents)} in spending categories
          {s.carriedInDeficitCents > 0 && <> · {formatCents(s.carriedInDeficitCents)} carried over</>} · {s.daysRemaining}{' '}
          {s.daysRemaining === 1 ? 'day' : 'days'} left
        </p>
      </section>

      {inboxCount > 0 && (
        <a
          href="#/budget/inbox"
          className="mt-3 flex items-center justify-between rounded-xl bg-amber-50 px-4 py-3 text-sm text-amber-900 ring-1 ring-amber-200"
        >
          <span>
            <span className="font-semibold">{inboxCount}</span> {inboxCount === 1 ? 'transaction needs' : 'transactions need'} a category
          </span>
          <span aria-hidden className="font-semibold">
            ›
          </span>
        </a>
      )}

      {s.groups.map((g) => (
        <Group key={g.type} group={g} />
      ))}

      <a
        href="#/budget/all"
        className="mt-8 flex items-center justify-between rounded-xl bg-white px-4 py-3 text-sm font-medium ring-1 ring-slate-200"
      >
        All transactions this month
        <span aria-hidden>›</span>
      </a>
    </>
  )
}

function Group({ group: g }: { group: BudgetGroup }) {
  const spending = g.countsTowardLeftToSpend
  return (
    <section aria-labelledby={`g-${g.type}`} className="mt-7">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id={`g-${g.type}`} className="text-sm font-semibold text-slate-700">
          {g.label}
        </h2>
        <p className="text-xs tabular-nums text-slate-500">
          {formatCents(g.spentCents)} {spending ? 'spent' : 'paid'} of {formatCents(g.limitCents)}
        </p>
      </div>
      {!spending && <p className="text-xs text-slate-400">Not part of Left to Spend</p>}
      <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
        {g.categories.map((c) => (
          <li key={c.id}>
            <a href={`#/budget/c/${c.id}`} className="block px-4 py-3 hover:bg-slate-50">
              <CategoryRow line={c} spending={spending} />
            </a>
          </li>
        ))}
      </ul>
    </section>
  )
}

function CategoryRow({ line: c, spending }: { line: BudgetCategoryLine; spending: boolean }) {
  // Bar width is display only: how much of the limit is used, capped at full.
  const used = c.limitCents > 0 ? Math.min(1, Math.max(0, c.spentCents / c.limitCents)) : c.spentCents > 0 ? 1 : 0
  const barColor = spending ? healthBar[c.health] : c.health === 'over' ? 'bg-red-500' : 'bg-sky-500'
  return (
    <>
      <div className="flex items-baseline justify-between gap-3">
        <span className="truncate text-sm font-medium">{c.name}</span>
        <RemainingLabel line={c} spending={spending} />
      </div>
      <div className="mt-2 h-2 overflow-hidden rounded-full bg-slate-100" aria-hidden>
        <div className={`h-full rounded-full ${barColor}`} style={{ width: `${used * 100}%` }} />
      </div>
      <p className="mt-1 text-xs tabular-nums text-slate-500">
        {formatCents(c.spentCents)} of {formatCents(c.limitCents)}
      </p>
    </>
  )
}

function RemainingLabel({ line: c, spending }: { line: BudgetCategoryLine; spending: boolean }) {
  if (c.remainingCents < 0)
    return <span className="shrink-0 text-sm font-semibold tabular-nums text-red-600">{formatCents(-c.remainingCents)} over</span>
  if (!spending)
    return c.remainingCents === 0 ? (
      <span className="shrink-0 text-sm font-semibold text-sky-700">Paid</span>
    ) : (
      <span className="shrink-0 text-sm tabular-nums text-slate-600">{formatCents(c.remainingCents)} to go</span>
    )
  return (
    <span className={`shrink-0 text-sm font-semibold tabular-nums ${c.health === 'low' ? 'text-amber-600' : 'text-emerald-700'}`}>
      {formatCents(c.remainingCents)} left
    </span>
  )
}

function CategoryHeader({ line: c }: { line: BudgetCategoryLine }) {
  const spending = c.type === 'Variable'
  return (
    <section className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <CategoryRow line={c} spending={spending} />
      {!spending && <p className="mt-2 text-xs text-slate-400">Not part of Left to Spend</p>}
    </section>
  )
}

function TransactionList({ items, empty, onSelect }: { items: Transaction[]; empty: string; onSelect: (t: Transaction) => void }) {
  if (items.length === 0)
    return <p className="mt-4 rounded-xl bg-white px-4 py-3 text-sm text-slate-500 ring-1 ring-slate-200">{empty}</p>
  return (
    <ul className="mt-4 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
      {items.map((t) => (
        <li key={t.id}>
          <button type="button" onClick={() => onSelect(t)} className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-slate-50">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{titleCase(t.merchant)}</p>
              <p className="truncate text-xs text-slate-500">
                {formatDay(t.date)} · {t.accountName}
                {t.pending && ' · Pending'}
              </p>
            </div>
            <div className="shrink-0 text-right">
              <p className={`text-sm font-semibold tabular-nums ${t.amountCents < 0 ? 'text-emerald-600' : ''}`}>
                {formatSigned(t.amountCents)}
              </p>
              <Tag t={t} />
            </div>
          </button>
        </li>
      ))}
    </ul>
  )
}

function Tag({ t }: { t: Transaction }) {
  if (t.isTransfer) return <span className="text-xs text-slate-400">{t.categoryName ?? 'Transfer'}</span>
  if (t.categoryName) return <span className="text-xs text-slate-500">{t.categoryName}</span>
  if (t.kind === 'Income') return <span className="text-xs text-emerald-600">Income</span>
  return <span className="text-xs font-medium text-amber-600">Needs category</span>
}

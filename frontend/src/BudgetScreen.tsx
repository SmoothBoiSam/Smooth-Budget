import { useCallback, useEffect, useState } from 'react'
import {
  ApiError,
  budgetApi,
  budgetEditApi,
  budgetSummaryApi,
  type BudgetCategoryLine,
  type BudgetGroup,
  type BudgetSummary,
  type Category,
  type ContributionStatus,
  type Health,
  type LastMonth,
  type RecurringItem,
  type SearchFilters,
  recurringApi,
  type Transaction,
} from './api'
import { useRemembered } from './offline'
import { Caret, ErrorNotice, Loading } from './ui'
import BudgetPlanner from './BudgetPlanner'
import CategoryEditSheet from './CategoryEditSheet'
import CategoriesPage from './CategoriesPage'
import { centsToInput, formatCents, formatDay, formatSigned, formatTime, parseDollars, titleCase } from './format'
import TransactionSheet from './TransactionSheet'

// Sub-views of the Budget tab, from the URL hash:
//   #/budget            categories overview
//   (#/budget/inbox moved to the Review tab, #/review)
//   #/transactions      every transaction and search, opened from Home (DECISIONS.md 171; #/budget/all still works)
//   #/budget/c/<id>     one category's transactions
//   #/budget/plan       budget settings and the budget generator
//   #/budget/categories add or delete categories
type View = { kind: 'overview' } | { kind: 'all' } | { kind: 'plan' } | { kind: 'categories' } | { kind: 'category'; id: string }

function viewFromHash(): View {
  const parts = window.location.hash.replace(/^#\/?/, '').split('/')
  if (parts[0] === 'transactions' || parts[1] === 'all') return { kind: 'all' }
  if (parts[1] === 'plan') return { kind: 'plan' }
  if (parts[1] === 'categories') return { kind: 'categories' }
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
  const [bills, setBills] = useState<RecurringItem[]>([])
  const [categories, setCategories] = useState<Category[]>([])
  const [selected, setSelected] = useState<Transaction | null>(null)
  const [editing, setEditing] = useState<BudgetCategoryLine | null | undefined>(undefined) // null = adding a new one
  const [error, setError] = useState<string | null>(null)
  const searchFailed = useCallback((e: unknown) => setError(e instanceof Error ? e.message : 'Search failed.'), [])
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
      const [s, m, c, rec] = await Promise.all([
        budgetSummaryApi.get(),
        budgetApi.monthTransactions(),
        budgetApi.categories(),
        recurringApi.summary().catch(() => null), // the bill list is extra: the budget still shows without it
      ])
      setSummary(s)
      setMonth(m.items)
      setBills(rec?.items ?? [])
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
    setEditing(undefined)
    setNotice(message)
    onChanged()
    void load()
  }

  async function act(action: () => Promise<unknown>, message: string) {
    try {
      await action()
      saved(message)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    }
  }

  /** Moves a category one place up or down within its group, keeping every other category's order. */
  async function move(l: BudgetCategoryLine, direction: -1 | 1) {
    if (!summary) return
    const group = summary.groups.find((g) => g.categories.some((c) => c.id === l.id))!
    const ids = group.categories.map((c) => c.id)
    const i = ids.indexOf(l.id)
    const j = i + direction
    if (j < 0 || j >= ids.length) return
    ;[ids[i], ids[j]] = [ids[j], ids[i]]
    const all = summary.groups.flatMap((g) => (g === group ? ids : g.categories.map((c) => c.id)))
    await budgetEditApi.reorder(all)
    await load()
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
          <a href={view.kind === 'all' ? '#/' : '#/budget'} className="text-sm font-medium text-slate-500">
            {view.kind === 'all' ? '‹ Home' : '‹ Budget'}
          </a>
          <div className="mt-1 flex items-center justify-between gap-3">
            <h1 className="truncate text-2xl font-bold tracking-tight">
              {view.kind === 'all'
                ? 'Transactions'
                : view.kind === 'plan'
                  ? 'Budget settings'
                  : view.kind === 'categories'
                    ? 'Categories'
                    : (line?.name ?? 'Category')}
            </h1>
            {view.kind === 'category' && line && (
              <button type="button" onClick={() => setEditing(line)} className="shrink-0 rounded-full bg-white px-3 py-1.5 text-sm font-medium ring-1 ring-slate-200">
                Edit
              </button>
            )}
          </div>
        </header>
      )}

      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!summary && !error && <Loading />}

      {summary && view.kind === 'overview' && (
        <Overview summary={summary} bills={bills} />
      )}

      {/* Budget settings (DECISIONS.md 168): one card for this month, then the planner. With the automatic budget off,
          the planner's own income fields do that job. */}
      {view.kind === 'plan' && (
        <BudgetPlanner
          top={(p) =>
            summary && (
              <IncomeCard
                summary={summary}
                explanation={p.incomeExplanation}
                onIncome={(income, partner, automatic) =>
                  act(() => budgetEditApi.setIncome(income, partner, automatic), automatic ? 'Income follows your paycheck forecast again.' : 'Expected income updated.')
                }
              />
            )
          }
          onApplied={(message) => {
            window.location.hash = '#/budget'
            saved(message)
          }}
        />
      )}

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

      {summary && view.kind === 'categories' && (
        <CategoriesPage categories={categories} onAdd={() => setEditing(null)} onDeleted={saved} />
      )}

      {summary && view.kind === 'all' && (
        <SearchableList month={month} categories={categories} onSelect={setSelected} onError={searchFailed} />
      )}

      {selected && (
        <TransactionSheet transaction={selected} categories={categories} onClose={() => setSelected(null)} onSaved={saved} />
      )}


      {editing !== undefined && (
        <CategoryEditSheet
          line={editing}
          usualLimitCents={editing ? categories.find((c) => c.id === editing.id)?.monthlyLimitCents : undefined}
          onClose={() => setEditing(undefined)}
          onSaved={(message) => {
            if (editing && message.endsWith('is hidden.')) window.location.hash = '#/budget'
            saved(message)
          }}
          onMove={editing ? (d) => move(editing, d) : undefined}
          auto={editing && summary?.auto ? { on: summary.auto.on, setByYou: summary.auto.setByYou.includes(editing.id) } : undefined}
        />
      )}
    </div>
  )
}

function Overview({ summary: s, bills }: { summary: BudgetSummary; bills: RecurringItem[] }) {
  // Home has the big number; here it's one line above the categories (DECISIONS.md 149).
  return (
    <>
      <p className="mt-3 text-sm text-slate-600">
        <span className={`font-semibold tabular-nums ${statusText[s.status]}`}>{formatCents(s.leftToSpendCents)}</span> left to spend ·{' '}
        {formatCents(s.totalVariableSpentCents)} of {formatCents(s.totalVariableLimitCents)} spent
        {s.carriedInDeficitCents > 0 && <> · {formatCents(s.carriedInDeficitCents)} carried over</>}
      </p>

      {s.lastMonth && <LastMonthCard last={s.lastMonth} />}
      {s.auto?.on && s.auto.shortCents > 0 && (
        <a href="#/budget/plan" role="alert" className="mt-3 block rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">
          This month is {formatCents(s.auto.shortCents)} short even with spending squeezed as far as it sensibly goes. Budget settings ›
        </a>
      )}

      {/* What needs a category or a confirmation is on the Review tab's badge, not here (DECISIONS.md 171). */}
      {s.auto && !s.auto.on && <AutoBudgetOff />}

      {s.groups.map((g) => (
        <Group key={g.type} group={g} bills={g.type === 'FixedBill' ? bills : []} month={s.budgetMonth} today={s.today} />
      ))}

      <div className="mt-6 grid grid-cols-2 gap-3">
        {/* Add and delete live on their own page (DECISIONS.md 169). */}
        <a href="#/budget/categories" className="rounded-xl bg-white py-3 text-center text-sm font-medium ring-1 ring-slate-200">
          Categories
        </a>
        <a href="#/budget/plan" className="rounded-xl bg-white py-3 text-center text-sm font-medium ring-1 ring-slate-200">
          Budget settings
        </a>
      </div>
    </>
  )
}

function Group({
  group: g,
  bills,
  month,
  today,
}: {
  group: BudgetGroup
  bills: RecurringItem[]
  month: { start: string; end: string }
  today: string
}) {
  const spending = g.countsTowardLeftToSpend
  // Each detected bill under the bill line it's filed in (DECISIONS.md 101). Ones filed under spending are asked
  // about in Review → Bills (DECISIONS.md 152).
  const live = bills.filter((b) => b.status !== 'canceled')
  const [open, setOpen] = useRemembered(`budget.${g.type}`, true)
  return (
    <section aria-labelledby={`g-${g.type}`} className="mt-7">
      <h2 id={`g-${g.type}`}>
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex w-full items-baseline justify-between gap-3 text-left">
          <span className="flex items-center gap-1 text-sm font-semibold text-slate-700">
            <Caret open={open} />
            {g.label}
          </span>
          <span className="text-xs font-normal tabular-nums text-slate-500">
            {formatCents(g.spentCents)} {spending ? 'spent' : 'paid'} of {formatCents(g.limitCents)}
          </span>
        </button>
      </h2>
      {open && (
        <>
      {!spending && <p className="text-xs text-slate-400">Not part of Left to Spend</p>}
      {g.type === 'DebtPayment' && (
        <p className="text-xs text-slate-400">Card payments count only what you pay beyond this month’s card purchases, which already count in their own categories.</p>
      )}
      <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
        {g.categories.map((c) => {
          const items = live.filter((b) => b.categoryId === c.id)
          return (
            <li key={c.id}>
              <a href={`#/budget/c/${c.id}`} className="block px-4 py-3 hover:bg-slate-50">
                <CategoryRow line={c} spending={spending} />
              </a>
              {items.length > 0 && <BillItems lineId={c.id} items={items} month={month} today={today} />}
            </li>
          )
        })}
      </ul>
        </>
      )}
    </section>
  )
}

/** The bills under one bill line; a long list folds behind a caret. */
function BillItems({ lineId, items, month, today }: { lineId: string; items: RecurringItem[]; month: { start: string; end: string }; today: string }) {
  const [open, setOpen] = useRemembered(`budget.items.${lineId}`, items.length <= 3)
  return (
    <div className="mb-2 ml-4 mr-4 border-l-2 border-slate-100 pl-3">
      {items.length > 3 && (
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex items-center gap-1 py-1 text-xs font-medium text-slate-500">
          <Caret open={open} />
          {items.length} bills
        </button>
      )}
      {open && (
        <ul className="divide-y divide-slate-100">
          {items.map((b) => (
            <BillItemRow key={b.key} b={b} month={month} today={today} />
          ))}
        </ul>
      )}
    </div>
  )
}

/** One bill or subscription: paid this budget month, due (or late), or next due after it. */
function BillItemRow({ b, month, today }: { b: RecurringItem; month: { start: string; end: string }; today: string }) {
  const paidThisMonth = b.lastDate >= month.start && b.lastDate <= month.end
  const [text, cls] =
    b.status === 'possiblyCanceled'
      ? [`Stopped? Last ${formatDay(b.lastDate)}`, 'text-slate-400']
      : paidThisMonth
        ? [`Paid ${formatDay(b.lastDate)}`, 'text-emerald-700']
        : b.nextExpected < today
          ? [`Due ${formatDay(b.nextExpected)}: not seen yet`, 'text-amber-600']
          : b.nextExpected <= month.end
            ? [`Due ${formatDay(b.nextExpected)}`, 'text-slate-500']
            : [`Next ${formatDay(b.nextExpected)}`, 'text-slate-400']
  return (
    <li>
      <a href={`#/review/bills/${encodeURIComponent(b.key)}`} className="flex items-baseline justify-between gap-3 py-2 text-sm">
        <span className="min-w-0">
          <span className="block truncate">{b.name}</span>
          <span className={`block text-xs ${cls}`}>
            {text}
          </span>
        </span>
        <span className="shrink-0 tabular-nums">
          {formatCents(b.amountCents)}
          {(b.status === 'priceUp' || b.status === 'priceDown') && (
            <span className="block text-right text-[11px] text-amber-600">{b.status === 'priceUp' ? 'price went up' : 'price went down'}</span>
          )}
        </span>
      </a>
    </li>
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
        {c.hasMonthOverride && <span className="ml-1 rounded bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-600">this month only</span>}
      </p>
      {c.partnerOffsetCents > 0 && (
        <p className="mt-0.5 text-xs tabular-nums text-slate-500">
          Partner covers {formatCents(c.partnerOffsetCents)} · your share <span className="font-medium text-slate-700">{formatCents(c.limitCents - c.partnerOffsetCents)}</span>
        </p>
      )}
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

/**
 * This month's transactions (with arrows back through earlier months), or a search across all of them (DECISIONS.md 150,
 * 164): words, a category, an amount range and a date range, all applied together.
 */
function SearchableList({
  month,
  categories,
  onSelect,
  onError,
}: {
  month: Transaction[]
  categories: Category[]
  onSelect: (t: Transaction) => void
  onError: (e: unknown) => void
}) {
  const [f, setF] = useState<SearchFilters>(noFilters)
  const [open, setOpen] = useState(false)
  const [found, setFound] = useState<{ key: string; items: Transaction[]; total: number; netCents: number } | null>(null)
  // Step back through earlier months to check how everything was filed (DECISIONS.md 153). 0 = this month.
  const [back, setBack] = useState(0)
  const [earlier, setEarlier] = useState<{ back: number; label: string; items: Transaction[] } | null>(null)

  useEffect(() => {
    if (back === 0) return
    const run = { live: true }
    budgetApi
      .monthTransactions(back)
      .then((r) => run.live && setEarlier({ back, label: r.label ?? '', items: r.items }))
      .catch((e) => run.live && onError(e))
    return () => {
      run.live = false
    }
    // `month` changes after any save, so an edit in an earlier month shows up right away.
  }, [back, month, onError])

  const shown = back === 0 ? month : earlier?.back === back ? earlier.items : null
  const label = back === 0 ? 'This month' : earlier?.back === back ? earlier.label : '…'

  // Only what's filled in and makes sense is sent; a half-typed amount or a backwards range waits.
  const minBad = f.min !== '' && parseDollars(f.min) === null
  const maxBad = f.max !== '' && parseDollars(f.max) === null
  const amountsBackwards = !minBad && !maxBad && f.min !== '' && f.max !== '' && parseDollars(f.min)! > parseDollars(f.max)!
  const datesBackwards = f.from !== '' && f.to !== '' && f.from > f.to
  const sent: SearchFilters = {
    q: f.q.trim().length >= 2 ? f.q : '',
    categoryId: f.categoryId,
    min: minBad || amountsBackwards ? '' : f.min.replace(/[$,\s]/g, ''),
    max: maxBad || amountsBackwards ? '' : f.max.replace(/[$,\s]/g, ''),
    from: datesBackwards ? '' : f.from,
    to: datesBackwards ? '' : f.to,
  }
  const key = JSON.stringify(sent)
  const searching = key !== JSON.stringify(noFilters)
  const filterCount = [f.categoryId, f.min || f.max, f.from || f.to].filter(Boolean).length

  useEffect(() => {
    if (!searching) return
    const run = { live: true }
    // Wait for a pause in typing before asking the server.
    const timer = window.setTimeout(() => {
      budgetApi
        .search(JSON.parse(key) as SearchFilters)
        .then((r) => run.live && setFound({ key, ...r }))
        .catch((e) => run.live && onError(e))
    }, 300)
    return () => {
      run.live = false
      window.clearTimeout(timer)
    }
    // `month` changes after any save, so a transaction you just changed drops out of (or into) the results right away.
  }, [key, searching, month, onError])

  const results = searching && found?.key === key ? found : null
  const set = (patch: Partial<SearchFilters>) => setF((x) => ({ ...x, ...patch }))
  const categoryName = categories.find((c) => c.id === f.categoryId)?.name
  const field = 'block w-full min-w-0 rounded-lg border border-slate-300 bg-white px-2.5 py-2 text-base'

  return (
    <>
      <div className="mt-4 flex gap-2">
        <input
          type="search"
          value={f.q}
          onChange={(e) => set({ q: e.target.value })}
          placeholder="Store, description or amount"
          aria-label="Search transactions"
          className="block min-w-0 flex-1 rounded-xl border border-slate-300 bg-white px-4 py-2.5 text-base"
        />
        <button
          type="button"
          aria-expanded={open}
          aria-controls="search-filters"
          onClick={() => setOpen(!open)}
          className={`shrink-0 rounded-xl px-3.5 text-sm font-semibold ring-1 ${open || filterCount > 0 ? 'bg-slate-900 text-white ring-slate-900' : 'bg-white text-slate-700 ring-slate-300'}`}
        >
          Filters{filterCount > 0 && ` · ${filterCount}`}
        </button>
      </div>

      {open && (
        <div id="search-filters" className="mt-2 space-y-3 rounded-xl bg-white p-3 ring-1 ring-slate-200">
          <label className="block">
            <span className="text-xs font-medium text-slate-600">Category</span>
            <select value={f.categoryId} onChange={(e) => set({ categoryId: e.target.value })} className={`mt-1 ${field}`}>
              <option value="">Any category</option>
              {groupOrder.map((type) => {
                const inType = categories.filter((c) => c.type === type)
                return inType.length === 0 ? null : (
                  <optgroup key={type} label={groupNames[type]}>
                    {inType.map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name}
                      </option>
                    ))}
                  </optgroup>
                )
              })}
            </select>
          </label>

          <fieldset>
            <legend className="text-xs font-medium text-slate-600">Amount (charges or refunds)</legend>
            <div className="mt-1 flex items-center gap-2">
              <input aria-label="At least" placeholder="$ min" inputMode="decimal" value={f.min} onChange={(e) => set({ min: e.target.value })}
                aria-invalid={minBad || amountsBackwards} className={`${field} ${minBad || amountsBackwards ? 'border-red-400' : ''}`} />
              <span className="text-slate-400">to</span>
              <input aria-label="At most" placeholder="$ max" inputMode="decimal" value={f.max} onChange={(e) => set({ max: e.target.value })}
                aria-invalid={maxBad || amountsBackwards} className={`${field} ${maxBad || amountsBackwards ? 'border-red-400' : ''}`} />
            </div>
            {amountsBackwards && <p className="mt-1 text-xs text-red-700">The lowest amount is bigger than the highest.</p>}
            <p className="mt-1 text-xs text-slate-500">Fill in one for "at least" or "at most", or the same in both for an exact amount.</p>
          </fieldset>

          <fieldset>
            <legend className="text-xs font-medium text-slate-600">Dates</legend>
            <div className="mt-1 flex items-center gap-2">
              <input type="date" aria-label="From" value={f.from} max={f.to || undefined} onChange={(e) => set({ from: e.target.value })} className={field} />
              <span className="text-slate-400">to</span>
              <input type="date" aria-label="To" value={f.to} min={f.from || undefined} onChange={(e) => set({ to: e.target.value })} className={field} />
            </div>
            {datesBackwards && <p className="mt-1 text-xs text-red-700">The start date is after the end date.</p>}
            <div className="mt-2 flex flex-wrap gap-1.5">
              {datePresets().map((p) => (
                <button
                  key={p.label}
                  type="button"
                  onClick={() => set({ from: p.from, to: p.to })}
                  aria-pressed={f.from === p.from && f.to === p.to}
                  className={`rounded-full px-2.5 py-1 text-xs font-medium ring-1 ${f.from === p.from && f.to === p.to ? 'bg-slate-900 text-white ring-slate-900' : 'bg-white text-slate-700 ring-slate-200'}`}
                >
                  {p.label}
                </button>
              ))}
            </div>
          </fieldset>

          <div className="flex gap-2">
            <button type="button" onClick={() => setF({ ...noFilters, q: f.q })} className="flex-1 rounded-lg bg-slate-100 py-2 text-sm font-medium text-slate-700">
              Clear filters
            </button>
            <button type="button" onClick={() => setOpen(false)} className="flex-1 rounded-lg bg-slate-900 py-2 text-sm font-semibold text-white">
              Done
            </button>
          </div>
        </div>
      )}

      {/* What's being searched, each removable, while the filters are folded away. */}
      {!open && filterCount > 0 && (
        <div className="mt-2 flex flex-wrap gap-1.5">
          {f.categoryId && <Chip label={categoryName ?? 'Category'} onRemove={() => set({ categoryId: '' })} />}
          {(f.min || f.max) && <Chip label={amountLabel(f.min, f.max)} onRemove={() => set({ min: '', max: '' })} />}
          {(f.from || f.to) && <Chip label={dateLabel(f.from, f.to)} onRemove={() => set({ from: '', to: '' })} />}
        </div>
      )}

      {!searching && (
        <div className="mt-3 flex items-center justify-between gap-2 rounded-xl bg-white px-2 py-1.5 ring-1 ring-slate-200">
          <button type="button" onClick={() => setBack(back + 1)} disabled={back >= 120} aria-label="Earlier month" className="rounded-lg px-3 py-1.5 text-lg font-semibold text-slate-600 disabled:opacity-30">
            ‹
          </button>
          <span className="text-sm font-semibold" aria-live="polite">
            {label}
          </span>
          <button type="button" onClick={() => setBack(Math.max(0, back - 1))} disabled={back === 0} aria-label="Later month" className="rounded-lg px-3 py-1.5 text-lg font-semibold text-slate-600 disabled:opacity-30">
            ›
          </button>
        </div>
      )}
      {!searching && <p className="mt-2 text-xs text-slate-500">Tap any transaction to change it. Search and filters look through every month.</p>}
      {searching && results === null && <Loading label="Searching" />}
      {searching && results && (
        <div className="mt-3 flex items-baseline justify-between gap-3 text-xs text-slate-500" aria-live="polite">
          <p>
            <span className="font-semibold text-slate-700">
              {results.total} {results.total === 1 ? 'match' : 'matches'}
            </span>
            {results.total > 0 && ` · ${formatCents(Math.abs(results.netCents))} ${results.netCents < 0 ? 'back' : 'spent'}`}
            {results.total > results.items.length && ` · showing the newest ${results.items.length}`}
          </p>
          <button type="button" onClick={() => setF(noFilters)} className="shrink-0 font-medium text-slate-700 underline">
            Clear all
          </button>
        </div>
      )}
      {!searching && shown === null && <Loading />}
      {!searching && shown && (
        <TransactionList items={shown} empty={back === 0 ? 'No transactions this month yet. Import a CSV from More.' : `No transactions in ${label}.`} onSelect={onSelect} />
      )}
      {searching && results && <TransactionList items={results.items} empty="Nothing matches all of that. Try removing a filter." onSelect={onSelect} />}
    </>
  )
}

const noFilters: SearchFilters = { q: '', categoryId: '', min: '', max: '', from: '', to: '' }
const groupOrder: Category['type'][] = ['Variable', 'FixedBill', 'DebtPayment', 'SavingsTransfer']
const groupNames: Record<Category['type'], string> = { Variable: 'Spending', FixedBill: 'Bills', DebtPayment: 'Debt', SavingsTransfer: 'Savings' }

/** yyyy-mm-dd in the phone's own time zone. */
const isoDay = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

function datePresets() {
  const now = new Date()
  const daysAgo = (n: number) => isoDay(new Date(now.getFullYear(), now.getMonth(), now.getDate() - n))
  const y = now.getFullYear()
  return [
    { label: 'Last 30 days', from: daysAgo(29), to: isoDay(now) },
    { label: 'Last 3 months', from: isoDay(new Date(y, now.getMonth() - 3, now.getDate())), to: isoDay(now) },
    { label: 'This year', from: `${y}-01-01`, to: isoDay(now) },
    { label: 'Last year', from: `${y - 1}-01-01`, to: `${y - 1}-12-31` },
  ]
}

function amountLabel(min: string, max: string) {
  const m = (s: string) => `$${s.replace(/[$,\s]/g, '')}`
  if (min && max) return min === max ? `Exactly ${m(min)}` : `${m(min)} to ${m(max)}`
  return min ? `At least ${m(min)}` : `At most ${m(max)}`
}

function dateLabel(from: string, to: string) {
  if (from && to) return `${formatDay(from)} – ${formatDay(to)}`
  return from ? `Since ${formatDay(from)}` : `Until ${formatDay(to)}`
}

function Chip({ label, onRemove }: { label: string; onRemove: () => void }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-slate-900 py-1 pr-1 pl-2.5 text-xs font-medium text-white">
      {label}
      <button type="button" onClick={onRemove} aria-label={`Remove ${label}`} className="rounded-full px-1.5 leading-none text-slate-300">
        ×
      </button>
    </span>
  )
}

export function TransactionList({ items, empty, onSelect }: { items: Transaction[]; empty: string; onSelect: (t: Transaction) => void }) {
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
  if (t.categoryName && t.needsReview)
    return (
      <span className={`text-xs ${t.unsure ? 'font-medium text-amber-600' : 'text-slate-500'}`} title="Not confirmed yet">
        {t.categoryName}
        {t.unsure ? '?' : <span className="text-amber-500"> •</span>}
      </span>
    )
  if (t.categoryName)
    return (
      <span className="text-xs text-slate-500">
        {t.categoryName}
        {t.categorySource === 'BestGuess' && <span className="text-slate-400"> · best guess</span>}
      </span>
    )
  if (t.kind === 'Income') return <span className="text-xs text-emerald-600">Income</span>
  if (t.kind === 'PartnerContribution') return <span className="text-xs text-emerald-600">Partner</span>
  return <span className="text-xs font-medium text-amber-600">Needs category</span>
}

/**
 * The month that just ended (SPEC section 3): a surplus becomes "send $X to …" items; a deficit carries
 * into this month as "Carried over". Until it closes (a few days after it ends, or "Close now"),
 * late transactions can still change it.
 */
function LastMonthCard({ last }: { last: LastMonth }) {
  const pending = last.sweeps.filter((s) => !s.done)
  return (
    <section aria-labelledby="last-h" className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="last-h" className="text-sm font-semibold">
          {last.month.label}
        </h2>
        {/* It closes on its own after a few days, so late transactions still count (DECISIONS.md 149). */}
        <span className="text-xs text-slate-500">{last.closed ? 'Closed' : `Closes on its own ${formatDay(last.closesOn)}`}</span>
      </div>

      {last.deficitCents > 0 ? (
        <p className="mt-1 text-sm">
          Ended <span className="font-semibold tabular-nums text-red-600">{formatCents(last.deficitCents)} over</span>. That's carried into this
          month and comes out of Left to Spend.
        </p>
      ) : last.surplusCents > 0 ? (
        <p className="mt-1 text-sm">
          Ended <span className="font-semibold tabular-nums text-emerald-700">{formatCents(last.surplusCents)} under</span>. Leftover money
          isn't added to this month.
          {pending.length > 0 && (
            <>
              {' '}
              <a href="#/payday" className="font-medium text-slate-700 underline">
                Where to send it: your Plan
              </a>
            </>
          )}
        </p>
      ) : (
        <p className="mt-1 text-sm">Ended exactly on budget.</p>
      )}
      {last.carriedInDeficitCents > 0 && (
        <p className="mt-1 text-xs text-slate-500">Includes {formatCents(last.carriedInDeficitCents)} carried in from the month before.</p>
      )}
    </section>
  )
}

/** On the Budget tab when the automatic budget is off. Its last change and any shortfall are in Budget settings. */
function AutoBudgetOff() {
  return (
    <a href="#/budget/plan" className="mt-6 block rounded-2xl bg-white px-4 py-3 text-sm ring-1 ring-slate-200">
      <span className="font-semibold">Automatic budget is off.</span> <span className="text-slate-500">Limits stay as they are. Turn it on ›</span>
    </a>
  )
}

/**
 * Budget settings' one card (DECISIONS.md 168): the automatic budget's last change, this month's income and partner
 * money with where the numbers come from, money in vs planned, and any shortfall, once.
 */
function IncomeCard({
  summary: s,
  explanation,
  onIncome,
}: {
  summary: BudgetSummary
  explanation?: string
  onIncome: (income: number, partner: number, automatic?: boolean) => void
}) {
  const [editing, setEditing] = useState(false)
  const [income, setIncome] = useState(centsToInput(s.incomeExpectedCents))
  const [partner, setPartner] = useState(centsToInput(s.partnerExpectedCents))
  const incomeCents = parseDollars(income)
  const partnerCents = parseDollars(partner)
  const over = s.unallocatedCents < 0
  const first = s.partnerName ? titleCase(s.partnerName.split(' ')[0]) : null
  const partnerLabel = first ? `From ${first}` : 'Partner contributions'
  const fixed = s.contributions ?? []
  const a = s.auto
  const moneyIn = s.incomeExpectedCents + s.partnerExpectedCents
  const short = a?.on ? a.shortCents : Math.max(0, -s.unallocatedCents)

  return (
    <section aria-labelledby="income-h" className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="income-h" className="text-sm font-semibold">
          This month
        </h2>
        {!editing && (
          <button type="button" onClick={() => setEditing(true)} className="text-xs font-medium text-slate-600 underline">
            Edit income
          </button>
        )}
      </div>
      {a?.on && (
        <p className="mt-1 text-xs text-slate-500">
          The automatic budget updates itself when your pay, bills, debt plan, savings or transactions change. A limit you change from its category on
          the Budget tab stays yours.
          {a.note && (
            <span className="mt-1 block text-slate-700">
              <span className="font-medium">Last change{a.noteUtc ? `, ${formatTime(a.noteUtc)}` : ''}:</span> {a.note}
            </span>
          )}
        </p>
      )}

      {editing ? (
        <form
          className="mt-2 space-y-2"
          onSubmit={(e) => {
            e.preventDefault()
            if (incomeCents !== null && partnerCents !== null) {
              onIncome(incomeCents, partnerCents)
              setEditing(false)
            }
          }}
        >
          <MoneyField label="Your expected income" value={income} onChange={setIncome} />
          {fixed.length > 0 ? (
            <p className="text-xs text-slate-500">
              {partnerLabel}: set on the{' '}
              <a href="#/partner" className="underline">
                Partner screen
              </a>
              .
            </p>
          ) : (
            <MoneyField label={`${partnerLabel}, expected`} value={partner} onChange={setPartner} />
          )}
          <button
            type="submit"
            disabled={incomeCents === null || partnerCents === null}
            className="w-full rounded-lg bg-slate-900 py-2 text-sm font-semibold text-white disabled:opacity-50"
          >
            Save
          </button>
        </form>
      ) : (
        <>
          <dl className="mt-3 space-y-1 text-sm">
            <div className="flex justify-between gap-3">
              <dt className="text-slate-600">Your income</dt>
              <dd className="tabular-nums">
                {formatCents(s.incomeExpectedCents)} <span className="text-xs text-slate-500">· {formatCents(s.incomeReceivedCents)} in so far</span>
              </dd>
            </div>
            {s.auto?.on && (
              <p className="text-xs text-slate-500">
                {s.auto.incomeSetByYou ? (
                  <>
                    You set this.{' '}
                    <button type="button" onClick={() => onIncome(0, s.partnerExpectedCents, true)} className="font-medium text-sky-700 underline">
                      Use your paychecks instead
                    </button>
                  </>
                ) : (
                  (explanation ?? 'From your paychecks this month (the Plan tab), or your usual income until there is pay data.')
                )}
              </p>
            )}
            {fixed.length === 0 ? (
            <div className="flex justify-between gap-3">
              <dt className="text-slate-600">{partnerLabel}</dt>
              <dd className="tabular-nums">
                {formatCents(s.partnerExpectedCents)} <span className="text-xs text-slate-500">· {formatCents(s.partnerReceivedCents)} in so far</span>
              </dd>
            </div>
            ) : (
              fixed.map((f) => <ContributionRow key={f.arrangementId} f={f} who={first ?? 'Partner'} />)
            )}
          </dl>
          <dl className="mt-3 grid grid-cols-3 gap-2 text-center">
            <div className="rounded-xl bg-slate-50 p-2">
              <dt className="text-xs text-slate-500">Money in</dt>
              <dd className="text-sm font-semibold tabular-nums">{formatCents(moneyIn)}</dd>
            </div>
            <div className="rounded-xl bg-slate-50 p-2">
              <dt className="text-xs text-slate-500">Planned</dt>
              <dd className="text-sm font-semibold tabular-nums">{formatCents(s.plannedCents)}</dd>
            </div>
            <div className={`rounded-xl p-2 ${over ? 'bg-red-50' : 'bg-slate-50'}`}>
              <dt className="text-xs text-slate-500">{over ? 'Short' : 'Unallocated'}</dt>
              <dd className={`text-sm font-semibold tabular-nums ${over ? 'text-red-600' : ''}`}>{formatCents(Math.abs(s.unallocatedCents))}</dd>
            </div>
          </dl>
          {short > 0 && (
            <p role="alert" className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-xs text-red-800">
              {a?.on
                ? `Even with spending squeezed as far as it sensibly goes, this month is ${formatCents(short)} short. Bills and minimums come first; the rest has to come from savings or extra pay.`
                : `This month plans ${formatCents(short)} more than comes in.`}
            </p>
          )}
          {!s.partnerName && (
            <p className="mt-2 text-xs text-slate-500">
              <a href="#/more" className="underline">
                Set your partner's name
              </a>{' '}
              in More to track her contributions apart from your income.
            </p>
          )}
        </>
      )}
      {s.incomeExpectedCents === 0 && !editing && (
        <p className="mt-2 text-xs text-slate-500">
          Set your expected income, or{' '}
          <a href="#/budget/plan" className="underline">
            build a budget from your history
          </a>
          .
        </p>
      )}
    </section>
  )
}

function MoneyField({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <label className="block">
      <span className="text-xs text-slate-500">{label}</span>
      <div className="mt-0.5 flex items-center rounded-lg border border-slate-300 bg-white px-3">
        <span className="text-sm text-slate-500">$</span>
        <input
          aria-label={label}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          inputMode="decimal"
          aria-invalid={parseDollars(value) === null}
          className="block w-full bg-transparent px-1 py-2 tabular-nums outline-none"
        />
      </div>
    </label>
  )
}


/** A fixed contribution this month (SPEC section 3): expected until her window passes, then received, short, or late. */
function ContributionRow({ f, who }: { f: ContributionStatus; who: string }) {
  const note =
    f.state === 'received'
      ? { text: 'received', cls: 'text-emerald-700' }
      : f.state === 'expected'
        ? { text: `expected by ${formatDay(f.windowEnd)}`, cls: 'text-slate-500' }
        : f.state === 'late'
          ? { text: 'late: Left to Spend counts without it', cls: 'font-medium text-red-600' }
          : {
              text: `${formatCents(f.receivedCents)} came, not ${formatCents(f.expectedCents)}${f.shortfallCents > 0 ? ': Left to Spend counts without the rest' : ''}`,
              cls: 'font-medium text-amber-600',
            }
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-slate-600">
        {who} toward {f.categoryName}
      </dt>
      <dd className="text-right tabular-nums">
        {formatCents(f.expectedCents)} <span className={`block text-xs ${note.cls}`}>{note.text}</span>
        {f.state !== 'received' && (
          // Sent before the budget month started? The Partner screen can count it here (DECISIONS.md 166).
          <a href="#/partner" className="block text-xs font-medium text-sky-700">
            Sent early? ›
          </a>
        )}
      </dd>
    </div>
  )
}

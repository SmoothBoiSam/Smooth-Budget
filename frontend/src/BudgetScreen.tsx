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
import { Caret, ErrorNotice, Loading, Notice, Page, PageHeader, headerActionCls } from './ui'
import BudgetPlanner from './BudgetPlanner'
import SweepSetting from './SweepSetting'
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

const healthBar: Record<Health, string> = { onTrack: 'bg-good', low: 'bg-gold', over: 'bg-danger' }
const statusText: Record<BudgetSummary['status'], string> = {
  healthy: 'text-good',
  low: 'text-gold-ink',
  overspent: 'text-danger',
}

export default function BudgetScreen({ onChanged, onSignedOut }: { onChanged: () => void; onSignedOut: () => void }) {
  const [view, setView] = useState<View>(viewFromHash)
  const [summary, setSummary] = useState<BudgetSummary | null>(null)
  const [month, setMonth] = useState<Transaction[]>([])
  const [bills, setBills] = useState<RecurringItem[]>([])
  const [categories, setCategories] = useState<Category[]>([])
  const [unusedCount, setUnusedCount] = useState(0)
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
      const [s, m, c, rec, unused] = await Promise.all([
        budgetSummaryApi.get(),
        budgetApi.monthTransactions(),
        budgetApi.categories(),
        recurringApi.summary().catch(() => null), // the bill list is extra: the budget still shows without it
        budgetEditApi.unusedCategories().catch(() => []), // so is the archive suggestion (DECISIONS.md 180)
      ])
      setSummary(s)
      setMonth(m.items)
      setBills(rec?.items ?? [])
      setCategories(c)
      setUnusedCount(unused.length)
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
    <Page>
      <PageHeader
        title={
          view.kind === 'overview'
            ? 'Budget'
            : view.kind === 'all'
              ? 'Transactions'
              : view.kind === 'plan'
                ? 'Budget settings'
                : view.kind === 'categories'
                  ? 'Categories'
                  : (line?.name ?? 'Category')
        }
        sub={view.kind === 'overview' && summary ? `${summary.budgetMonth.label} · ${formatDay(summary.budgetMonth.start)} – ${formatDay(summary.budgetMonth.end)}` : undefined}
        back={view.kind === 'overview' ? undefined : view.kind === 'all' ? { href: '#/', label: 'Home' } : { href: '#/budget', label: 'Budget' }}
        action={
          view.kind === 'category' && line ? (
            <button type="button" onClick={() => setEditing(line)} className={headerActionCls}>
              Edit
            </button>
          ) : undefined
        }
      />

      {notice && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!summary && !error && <Loading />}

      {summary && view.kind === 'overview' && (
        <Overview summary={summary} bills={bills} unusedCount={unusedCount} />
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
      {view.kind === 'plan' && (
        <div className="mt-6">
          <SweepSetting />
        </div>
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
        <p className="mt-6 text-sm text-ink-soft">This category no longer exists.</p>
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
            if (editing && message.includes(' is archived.')) window.location.hash = '#/budget'
            saved(message)
          }}
          onMove={editing ? (d) => move(editing, d) : undefined}
          auto={editing && summary?.auto ? { on: summary.auto.on, setByYou: summary.auto.setByYou.includes(editing.id) } : undefined}
        />
      )}
    </Page>
  )
}

function Overview({ summary: s, bills, unusedCount }: { summary: BudgetSummary; bills: RecurringItem[]; unusedCount: number }) {
  // Home has the big number; here it's one line above the categories (DECISIONS.md 149).
  return (
    <>
      <p className="mt-3 text-sm text-ink-soft">
        <span className={`font-semibold tabular-nums ${statusText[s.status]}`}>{formatCents(s.leftToSpendCents)}</span> left to spend ·{' '}
        {formatCents(s.totalVariableSpentCents)} of {formatCents(s.totalVariableLimitCents)} spent
        {s.carriedInDeficitCents > 0 && <> · {formatCents(s.carriedInDeficitCents)} carried over</>}
        {s.unfiledSpendingCents > 0 && <> · {formatCents(s.unfiledSpendingCents)} not filed yet</>}
        {s.cashShortCents > 0 && <> · {formatCents(s.cashShortCents)} more than your accounts cover</>}
      </p>
      {/* Managing the budget sits right under its numbers, one tap away (DECISIONS.md 169, 187). */}
      <div className="mt-4 grid grid-cols-2 gap-3">
        <a href="#/budget/categories" className="neu-sm rounded-2xl py-2.5 text-center text-sm font-semibold">
          Categories
        </a>
        <a href="#/budget/plan" className="neu-sm rounded-2xl py-2.5 text-center text-sm font-semibold">
          Budget settings
        </a>
      </div>

      {(s.overplannedCents > 0 || (s.auto?.on && s.auto.shortCents > 0) || unusedCount > 0) && (
        <div className="mt-5 space-y-3">
          {s.overplannedCents > 0 && (
            <Notice tone="danger" href="#/budget/plan">
              The plan is {formatCents(s.overplannedCents)} more than this month’s money.
            </Notice>
          )}
          {s.auto?.on && s.auto.shortCents > 0 && (
            <Notice tone="danger" href="#/budget/plan">
              {formatCents(s.auto.shortCents)} short this month, even with spending trimmed.
            </Notice>
          )}
          {unusedCount > 0 && (
            <Notice tone="todo" href="#/budget/categories">
              {unusedCount === 1 ? '1 category' : `${unusedCount} categories`} unused for 3 months. Archive or keep {unusedCount === 1 ? 'it' : 'them'}.
            </Notice>
          )}
        </div>
      )}

      {s.lastMonth && <LastMonthCard last={s.lastMonth} />}

      {/* What needs a category or a confirmation is on the Review tab's badge, not here (DECISIONS.md 171). */}
      {s.auto && !s.auto.on && <AutoBudgetOff />}

      {s.groups.map((g) => (
        <Group key={g.type} group={g} bills={g.type === 'FixedBill' ? bills : []} month={s.budgetMonth} today={s.today} />
      ))}
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
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex w-full items-baseline justify-between gap-3 px-1 text-left">
          <span className="flex items-center gap-1 px-1 text-sm font-semibold text-ink-soft">
            <Caret open={open} />
            {g.label}
          </span>
          <span className="text-xs font-normal tabular-nums text-ink-soft">
            {formatCents(g.spentCents)} {spending ? 'spent' : 'paid'} of {formatCents(g.limitCents)}
          </span>
        </button>
      </h2>
      {open && (
        <>
      {!spending && <p className="px-1 text-xs text-ink-faint">Not part of Left to Spend</p>}
      {g.type === 'DebtPayment' && (
        <p className="px-1 text-xs text-ink-faint">Only what you pay beyond this month’s card purchases counts.</p>
      )}
      <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
        {g.categories.map((c) => {
          const items = live.filter((b) => b.categoryId === c.id)
          return (
            <li key={c.id}>
              <a href={`#/budget/c/${c.id}`} className="block px-4 py-3">
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
    <div className="mb-2 ml-4 mr-4 border-l-2 border-ink/10 pl-3">
      {items.length > 3 && (
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex items-center gap-1 py-1 text-xs font-medium text-ink-soft">
          <Caret open={open} />
          {items.length} bills
        </button>
      )}
      {open && (
        <ul className="divide-y divide-ink/[0.07]">
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
      ? [`Stopped? Last ${formatDay(b.lastDate)}`, 'text-ink-faint']
      : paidThisMonth
        ? [`Paid ${formatDay(b.lastDate)}`, 'text-good']
        : b.nextExpected < today
          ? [`Due ${formatDay(b.nextExpected)}: not seen yet`, 'text-gold-ink']
          : b.nextExpected <= month.end
            ? [`Due ${formatDay(b.nextExpected)}`, 'text-ink-soft']
            : [`Next ${formatDay(b.nextExpected)}`, 'text-ink-faint']
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
            <span className="block text-right text-[11px] text-gold-ink">{b.status === 'priceUp' ? 'price went up' : 'price went down'}</span>
          )}
        </span>
      </a>
    </li>
  )
}

function CategoryRow({ line: c, spending }: { line: BudgetCategoryLine; spending: boolean }) {
  // Bar width is display only: how much of the limit is used, capped at full.
  const used = c.limitCents > 0 ? Math.min(1, Math.max(0, c.spentCents / c.limitCents)) : c.spentCents > 0 ? 1 : 0
  const barColor = spending ? healthBar[c.health] : c.health === 'over' ? 'bg-danger' : 'bg-gold'
  return (
    <>
      <div className="flex items-baseline justify-between gap-3">
        <span className="truncate text-sm font-medium">{c.name}</span>
        <RemainingLabel line={c} spending={spending} />
      </div>
      <div className="mt-2 h-2 overflow-hidden rounded-full neu-inset" aria-hidden>
        <div className={`h-full rounded-full ${barColor}`} style={{ width: `${used * 100}%` }} />
      </div>
      <p className="mt-1 text-xs tabular-nums text-ink-soft">
        {formatCents(c.spentCents)} of {formatCents(c.limitCents)}
        {c.hasMonthOverride && <span className="ml-1 rounded neu-sm px-1.5 py-0.5 text-[10px] font-medium text-ink-soft">this month only</span>}
      </p>
      {c.partnerOffsetCents > 0 && (
        <p className="mt-0.5 text-xs tabular-nums text-ink-soft">
          Partner covers {formatCents(c.partnerOffsetCents)} · your share <span className="font-medium text-ink-soft">{formatCents(c.limitCents - c.partnerOffsetCents)}</span>
        </p>
      )}
    </>
  )
}

function RemainingLabel({ line: c, spending }: { line: BudgetCategoryLine; spending: boolean }) {
  if (c.remainingCents < 0)
    return <span className="shrink-0 text-sm font-semibold tabular-nums text-danger">{formatCents(-c.remainingCents)} over</span>
  if (!spending)
    return c.remainingCents === 0 ? (
      <span className="shrink-0 text-sm font-semibold text-good">Paid</span>
    ) : (
      <span className="shrink-0 text-sm tabular-nums text-ink-soft">{formatCents(c.remainingCents)} to go</span>
    )
  return (
    <span className={`shrink-0 text-sm font-semibold tabular-nums ${c.health === 'low' ? 'text-gold-ink' : 'text-good'}`}>
      {formatCents(c.remainingCents)} left
    </span>
  )
}

function CategoryHeader({ line: c }: { line: BudgetCategoryLine }) {
  const spending = c.type === 'Variable'
  return (
    <section className="mt-4 rounded-3xl neu p-4">
      <CategoryRow line={c} spending={spending} />
      {!spending && <p className="mt-2 text-xs text-ink-faint">Not part of Left to Spend</p>}
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
  const field = 'block w-full min-w-0 rounded-xl neu-inset px-2.5 py-2 text-base'

  return (
    <>
      <div className="mt-4 flex gap-2">
        <input
          type="search"
          value={f.q}
          onChange={(e) => set({ q: e.target.value })}
          placeholder="Store, description or amount"
          aria-label="Search transactions"
          className="block min-w-0 flex-1 rounded-xl neu-inset px-4 py-2.5 text-base"
        />
        <button
          type="button"
          aria-expanded={open}
          aria-controls="search-filters"
          onClick={() => setOpen(!open)}
          className={`shrink-0 rounded-xl px-3.5 text-sm font-semibold ${open || filterCount > 0 ? 'bg-coral shadow-neu-sm text-coral-ink' : 'neu-sm text-ink-soft'}`}
        >
          Filters{filterCount > 0 && ` · ${filterCount}`}
        </button>
      </div>

      {open && (
        <div id="search-filters" className="mt-2 space-y-3 rounded-2xl neu p-3">
          <label className="block">
            <span className="text-xs font-medium text-ink-soft">Category</span>
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
            <legend className="text-xs font-medium text-ink-soft">Amount (charges or refunds)</legend>
            <div className="mt-1 flex items-center gap-2">
              <input aria-label="At least" placeholder="$ min" inputMode="decimal" value={f.min} onChange={(e) => set({ min: e.target.value })}
                aria-invalid={minBad || amountsBackwards} className={`${field} ${minBad || amountsBackwards ? 'ring-2 ring-danger' : ''}`} />
              <span className="text-ink-faint">to</span>
              <input aria-label="At most" placeholder="$ max" inputMode="decimal" value={f.max} onChange={(e) => set({ max: e.target.value })}
                aria-invalid={maxBad || amountsBackwards} className={`${field} ${maxBad || amountsBackwards ? 'ring-2 ring-danger' : ''}`} />
            </div>
            {amountsBackwards && <p className="mt-1 text-xs text-danger">The lowest amount is bigger than the highest.</p>}
            <p className="mt-1 text-xs text-ink-soft">Fill in one for "at least" or "at most", or the same in both for an exact amount.</p>
          </fieldset>

          <fieldset>
            <legend className="text-xs font-medium text-ink-soft">Dates</legend>
            <div className="mt-1 flex items-center gap-2">
              <input type="date" aria-label="From" value={f.from} max={f.to || undefined} onChange={(e) => set({ from: e.target.value })} className={field} />
              <span className="text-ink-faint">to</span>
              <input type="date" aria-label="To" value={f.to} min={f.from || undefined} onChange={(e) => set({ to: e.target.value })} className={field} />
            </div>
            {datesBackwards && <p className="mt-1 text-xs text-danger">The start date is after the end date.</p>}
            <div className="mt-2 flex flex-wrap gap-1.5">
              {datePresets().map((p) => (
                <button
                  key={p.label}
                  type="button"
                  onClick={() => set({ from: p.from, to: p.to })}
                  aria-pressed={f.from === p.from && f.to === p.to}
                  className={`rounded-full px-2.5 py-1 text-xs font-medium ${f.from === p.from && f.to === p.to ? 'bg-coral shadow-neu-sm text-coral-ink' : 'neu text-ink-soft'}`}
                >
                  {p.label}
                </button>
              ))}
            </div>
          </fieldset>

          <div className="flex gap-2">
            <button type="button" onClick={() => setF({ ...noFilters, q: f.q })} className="flex-1 rounded-2xl neu py-3 text-sm font-medium text-ink-soft">
              Clear filters
            </button>
            <button type="button" onClick={() => setOpen(false)} className="flex-1 rounded-2xl bg-coral shadow-neu-sm py-3 text-sm font-semibold text-coral-ink">
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
        <div className="mt-3 flex items-center justify-between gap-2 rounded-2xl neu px-2 py-1.5">
          <button type="button" onClick={() => setBack(back + 1)} disabled={back >= 120} aria-label="Earlier month" className="rounded-lg px-3 py-1.5 text-lg font-semibold text-ink-soft disabled:opacity-30">
            ‹
          </button>
          <span className="text-sm font-semibold" aria-live="polite">
            {label}
          </span>
          <button type="button" onClick={() => setBack(Math.max(0, back - 1))} disabled={back === 0} aria-label="Later month" className="rounded-lg px-3 py-1.5 text-lg font-semibold text-ink-soft disabled:opacity-30">
            ›
          </button>
        </div>
      )}
      {searching && results === null && <Loading label="Searching" />}
      {searching && results && (
        <div className="mt-3 flex items-baseline justify-between gap-3 text-xs text-ink-soft" aria-live="polite">
          <p>
            <span className="font-semibold text-ink-soft">
              {results.total} {results.total === 1 ? 'match' : 'matches'}
            </span>
            {results.total > 0 && ` · ${formatCents(Math.abs(results.netCents))} ${results.netCents < 0 ? 'back' : 'spent'}`}
            {results.total > results.items.length && ` · showing the newest ${results.items.length}`}
          </p>
          <button type="button" onClick={() => setF(noFilters)} className="shrink-0 font-medium text-ink-soft underline">
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
    <span className="inline-flex items-center gap-1 rounded-full bg-coral shadow-neu-sm py-1 pr-1 pl-2.5 text-xs font-medium text-coral-ink">
      {label}
      <button type="button" onClick={onRemove} aria-label={`Remove ${label}`} className="rounded-full px-1.5 leading-none text-ink-faint">
        ×
      </button>
    </span>
  )
}

export function TransactionList({ items, empty, onSelect, className = "mt-4" }: { items: Transaction[]; empty: string; onSelect: (t: Transaction) => void; className?: string }) {
  if (items.length === 0) return <p className={`neu rounded-3xl ${className} px-5 py-4 text-sm text-ink-soft`}>{empty}</p>
  return (
    <ul className={`${className} neu divide-y divide-ink/[0.07] overflow-hidden rounded-3xl`}>
      {items.map((t) => (
        <li key={t.id}>
          <button type="button" onClick={() => onSelect(t)} className="flex w-full items-center gap-3 px-5 py-3 text-left">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{titleCase(t.merchant)}</p>
              <p className="truncate text-xs text-ink-faint">
                {formatDay(t.date)} · {t.accountName}
                {t.pending && ' · Pending'}
              </p>
            </div>
            <div className="shrink-0 text-right">
              <p className={`text-sm font-semibold tabular-nums ${t.amountCents < 0 ? 'text-good' : ''}`}>
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
  if (t.isTransfer) return <span className="text-xs text-ink-faint">{t.categoryName ?? 'Transfer'}</span>
  if (t.categoryName && t.needsReview)
    return (
      <span className={`text-xs ${t.unsure ? 'font-medium text-gold-ink' : 'text-ink-soft'}`} title="Not confirmed yet">
        {t.categoryName}
        {t.unsure ? '?' : <span className="text-gold"> •</span>}
      </span>
    )
  if (t.categoryName)
    return (
      <span className="text-xs text-ink-soft">
        {t.categoryName}
        {t.categorySource === 'BestGuess' && <span className="text-ink-faint"> · best guess</span>}
      </span>
    )
  if (t.kind === 'Income') return <span className="text-xs text-good">Income</span>
  if (t.kind === 'PartnerContribution') return <span className="text-xs text-good">Partner</span>
  return <span className="text-xs font-medium text-gold-ink">Needs category</span>
}

/**
 * The month that just ended (SPEC section 3): a surplus becomes "send $X to …" items; a deficit carries
 * into this month as "Carried over". Until it closes (a few days after it ends, or "Close now"),
 * late transactions can still change it.
 */
function LastMonthCard({ last }: { last: LastMonth }) {
  const pending = last.sweeps.filter((s) => !s.done)
  return (
    <section aria-labelledby="last-h" className="mt-4 rounded-3xl neu p-4">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="last-h" className="text-sm font-semibold">
          {last.month.label}
        </h2>
        {/* It closes on its own after a few days, so late transactions still count (DECISIONS.md 149). */}
        <span className="text-xs text-ink-soft">{last.closed ? 'Closed' : `Closes on its own ${formatDay(last.closesOn)}`}</span>
      </div>

      {last.deficitCents > 0 ? (
        <p className="mt-1 text-sm">
          Ended <span className="font-semibold tabular-nums text-danger">{formatCents(last.deficitCents)} over</span>. That's carried into this
          month and comes out of Left to Spend.
        </p>
      ) : last.surplusCents > 0 ? (
        <p className="mt-1 text-sm">
          Ended <span className="font-semibold tabular-nums text-good">{formatCents(last.surplusCents)} under</span>. Leftover money
          isn't added to this month.
          {pending.length > 0 && (
            <>
              {' '}
              <a href="#/payday" className="font-medium text-ink-soft underline">
                Where to send it: your Plan
              </a>
            </>
          )}
        </p>
      ) : (
        <p className="mt-1 text-sm">Ended exactly on budget.</p>
      )}
      {/* How the end figure was reached, from the API's own numbers, so nothing in it is a mystery. */}
      <dl className="mt-2 space-y-0.5 text-xs text-ink-soft">
        {(
          [
            ['Left in spending categories', last.totalUnspentCents, 1],
            ['Over in spending categories', last.totalOverspentCents, -1],
            ['Carried in from the month before', last.carriedInDeficitCents, -1],
            ['Partner money that didn’t come', last.partnerShortfallCents ?? 0, -1],
            ['Spending not filed in a category', last.unfiledSpendingCents ?? 0, -1],
            ['Planned beyond the month’s money', last.overplannedCents ?? 0, -1],
          ] as const
        )
          .filter(([, cents]) => cents > 0)
          .map(([label, cents, sign]) => (
            <div key={label} className="flex justify-between gap-3">
              <dt>{label}</dt>
              <dd className="tabular-nums">
                {sign < 0 ? '−' : '+'}
                {formatCents(cents)}
              </dd>
            </div>
          ))}
      </dl>
    </section>
  )
}

/** On the Budget tab when the automatic budget is off. Its last change and any shortfall are in Budget settings. */
function AutoBudgetOff() {
  return (
    <div className="mt-5">
      <Notice tone="todo" href="#/budget/plan">
        Automatic budget is off, so limits stay as they are.
      </Notice>
    </div>
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
    <section aria-labelledby="income-h" className="mt-4 rounded-3xl neu p-4">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="income-h" className="text-sm font-semibold">
          This month
        </h2>
        {!editing && (
          <button type="button" onClick={() => setEditing(true)} className="text-xs font-medium text-ink-soft underline">
            Edit income
          </button>
        )}
      </div>
      {a?.on && (
        <p className="mt-1 text-xs text-ink-soft">
          Updates itself as your pay, bills and spending change. Limits you set yourself stay.
          {a.note && (
            <span className="mt-1 block text-ink-soft">
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
            <p className="text-xs text-ink-soft">
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
            className="w-full rounded-2xl bg-coral shadow-neu-sm py-3 text-sm font-semibold text-coral-ink disabled:opacity-50"
          >
            Save
          </button>
        </form>
      ) : (
        <>
          <dl className="mt-3 space-y-1 text-sm">
            <div className="flex justify-between gap-3">
              <dt className="text-ink-soft">Your income</dt>
              <dd className="tabular-nums">
                {formatCents(s.incomeExpectedCents)} <span className="text-xs text-ink-soft">· {formatCents(s.incomeReceivedCents)} in so far</span>
              </dd>
            </div>
            {s.auto?.on && (
              <p className="text-xs text-ink-soft">
                {s.auto.incomeSetByYou ? (
                  <>
                    You set this.{' '}
                    <button type="button" onClick={() => onIncome(0, s.partnerExpectedCents, true)} className="font-medium text-coral underline">
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
              <dt className="text-ink-soft">{partnerLabel}</dt>
              <dd className="tabular-nums">
                {formatCents(s.partnerExpectedCents)} <span className="text-xs text-ink-soft">· {formatCents(s.partnerReceivedCents)} in so far</span>
              </dd>
            </div>
            ) : (
              fixed.map((f) => <ContributionRow key={f.arrangementId} f={f} who={first ?? 'Partner'} />)
            )}
          </dl>
          <dl className="mt-3 grid grid-cols-3 gap-2 text-center">
            <div className="rounded-xl neu-inset p-2">
              <dt className="text-xs text-ink-soft">Money in</dt>
              <dd className="text-sm font-semibold tabular-nums">{formatCents(moneyIn)}</dd>
            </div>
            <div className="rounded-xl neu-inset p-2">
              <dt className="text-xs text-ink-soft">Planned</dt>
              <dd className="text-sm font-semibold tabular-nums">{formatCents(s.plannedCents)}</dd>
            </div>
            <div className={`rounded-xl p-2 ${over ? 'neu-inset' : 'neu-inset'}`}>
              <dt className="text-xs text-ink-soft">{over ? 'Short' : 'Unallocated'}</dt>
              <dd className={`text-sm font-semibold tabular-nums ${over ? 'text-danger' : ''}`}>{formatCents(Math.abs(s.unallocatedCents))}</dd>
            </div>
          </dl>
          {short > 0 && (
            <p role="alert" className="mt-3 rounded-lg neu-inset px-3 py-2 text-xs text-danger">
              {a?.on
                ? `Even with spending squeezed as far as it sensibly goes, this month is ${formatCents(short)} short. Bills and minimums come first; the rest has to come from savings or extra pay.`
                : `This month plans ${formatCents(short)} more than comes in.`}
            </p>
          )}
          {!s.partnerName && (
            <p className="mt-2 text-xs text-ink-soft">
              <a href="#/partner" className="underline">
                Set your partner's name
              </a>{' '}
              to track her contributions apart from your income.
            </p>
          )}
        </>
      )}
      {s.incomeExpectedCents === 0 && !editing && (
        <p className="mt-2 text-xs text-ink-soft">
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
      <span className="text-xs text-ink-soft">{label}</span>
      <div className="mt-0.5 flex items-center rounded-xl neu-inset px-3">
        <span className="text-sm text-ink-soft">$</span>
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
      ? { text: 'received', cls: 'text-good' }
      : f.state === 'expected'
        ? { text: `expected by ${formatDay(f.windowEnd)}`, cls: 'text-ink-soft' }
        : f.state === 'late'
          ? { text: 'late: Left to Spend counts without it', cls: 'font-medium text-danger' }
          : {
              text: `${formatCents(f.receivedCents)} came, not ${formatCents(f.expectedCents)}${f.shortfallCents > 0 ? ': Left to Spend counts without the rest' : ''}`,
              cls: 'font-medium text-gold-ink',
            }
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-ink-soft">
        {who} toward {f.categoryName}
      </dt>
      <dd className="text-right tabular-nums">
        {formatCents(f.expectedCents)} <span className={`block text-xs ${note.cls}`}>{note.text}</span>
        {f.state !== 'received' && (
          // Sent before the budget month started? The Partner screen can count it here (DECISIONS.md 166).
          <a href="#/partner" className="block text-xs font-medium text-coral">
            Sent early? ›
          </a>
        )}
      </dd>
    </div>
  )
}

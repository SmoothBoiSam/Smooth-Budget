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
  type ReviewSummary,
  type RecurringItem,
  recurringApi,
  type Transaction,
} from './api'
import { ErrorNotice, Loading } from './ui'
import BudgetPlanner from './BudgetPlanner'
import CategoryEditSheet from './CategoryEditSheet'
import { centsToInput, formatCents, formatDay, formatSigned, parseDollars, titleCase } from './format'
import TransactionSheet from './TransactionSheet'

// Sub-views of the Budget tab, from the URL hash:
//   #/budget            categories overview
//   (#/budget/inbox moved to the Review tab, #/review)
//   #/budget/all        every transaction this month
//   #/budget/c/<id>     one category's transactions
//   #/budget/plan       the budget generator
type View = { kind: 'overview' } | { kind: 'all' } | { kind: 'plan' } | { kind: 'category'; id: string }

function viewFromHash(): View {
  const parts = window.location.hash.replace(/^#\/?/, '').split('/')
  if (parts[1] === 'all') return { kind: 'all' }
  if (parts[1] === 'plan') return { kind: 'plan' }
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
  const [reviewSummary, setReviewSummary] = useState<ReviewSummary | null>(null)
  const [bills, setBills] = useState<RecurringItem[]>([])
  const [categories, setCategories] = useState<Category[]>([])
  const [selected, setSelected] = useState<Transaction | null>(null)
  const [editing, setEditing] = useState<BudgetCategoryLine | null | undefined>(undefined) // null = adding a new one
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
      const [s, m, i, c, rs, rec] = await Promise.all([
        budgetSummaryApi.get(),
        budgetApi.monthTransactions(),
        budgetApi.inbox(),
        budgetApi.categories(),
        budgetApi.reviewSummary(),
        recurringApi.summary().catch(() => null), // the bill list is extra: the budget still shows without it
      ])
      setSummary(s)
      setMonth(m.items)
      setInbox(i)
      setReviewSummary(rs)
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
          <a href="#/budget" className="text-sm font-medium text-slate-500">
            ‹ Budget
          </a>
          <div className="mt-1 flex items-center justify-between gap-3">
            <h1 className="truncate text-2xl font-bold tracking-tight">
              {view.kind === 'all' ? 'All transactions' : view.kind === 'plan' ? 'Build your budget' : (line?.name ?? 'Category')}
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
        <Overview
          summary={summary}
          bills={bills}
          inboxCount={inbox.length}
          guessCount={reviewSummary?.month.total ?? 0}
          onAdd={() => setEditing(null)}
          onCloseMonth={(start) => act(() => budgetEditApi.closeMonth(start), 'Month closed.')}
          onSweep={(id, done) => act(() => (done ? budgetEditApi.sweepDone(id) : budgetEditApi.sweepUndo(id)), done ? 'Marked as sent.' : 'Marked as not sent.')}
          onIncome={(income, partner) => act(() => budgetEditApi.setIncome(income, partner), 'Expected income updated.')}
        />
      )}

      {view.kind === 'plan' && (
        <BudgetPlanner
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

      {summary && view.kind === 'all' && (
        <TransactionList items={month} empty="No transactions this month yet. Import a CSV from More." onSelect={setSelected} />
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
        />
      )}
    </div>
  )
}

function Overview({
  summary: s,
  bills,
  inboxCount,
  guessCount,
  onAdd,
  onCloseMonth,
  onSweep,
  onIncome,
}: {
  summary: BudgetSummary
  bills: RecurringItem[]
  inboxCount: number
  guessCount: number
  onAdd: () => void
  onCloseMonth: (start: string) => void
  onSweep: (id: string, done: boolean) => void
  onIncome: (income: number, partner: number) => void
}) {
  const lastMonthSweepIds = new Set(s.lastMonth?.sweeps.map((x) => x.id) ?? [])
  const olderSweeps = s.pendingSweeps.filter((p) => !lastMonthSweepIds.has(p.id))
  return (
    <>
      {s.lastMonth && <LastMonthCard last={s.lastMonth} onClose={onCloseMonth} onSweep={onSweep} />}
      {olderSweeps.map((p) => (
        <div key={p.id} className="mt-3 flex items-center justify-between gap-3 rounded-xl bg-white px-4 py-3 text-sm ring-1 ring-slate-200">
          <span>
            From {p.fromMonth}: send <span className="font-semibold tabular-nums">{formatCents(p.amountCents)}</span> to {p.destination}
          </span>
          <button type="button" onClick={() => onSweep(p.id, true)} className="shrink-0 font-medium text-slate-700 underline">
            Mark sent
          </button>
        </div>
      ))}

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

      {inboxCount + guessCount > 0 && (
        <a
          href="#/review"
          className="mt-3 flex items-center justify-between rounded-xl bg-amber-50 px-4 py-3 text-sm text-amber-900 ring-1 ring-amber-200"
        >
          <span>
            {guessCount > 0 && (
              <>
                <span className="font-semibold">{guessCount}</span> to confirm
              </>
            )}
            {guessCount > 0 && inboxCount > 0 && ' · '}
            {inboxCount > 0 && (
              <>
                <span className="font-semibold">{inboxCount}</span> {inboxCount === 1 ? 'needs' : 'need'} a category
              </>
            )}
          </span>
          <span aria-hidden className="font-semibold">
            ›
          </span>
        </a>
      )}

      <IncomeCard summary={s} onIncome={onIncome} />

      {s.groups.map((g) => (
        <Group key={g.type} group={g} bills={g.type === 'FixedBill' ? bills : []} month={s.budgetMonth} today={s.today} />
      ))}

      <div className="mt-6 grid grid-cols-2 gap-3">
        <button type="button" onClick={onAdd} className="rounded-xl bg-white py-3 text-sm font-medium ring-1 ring-slate-200">
          + Add category
        </button>
        <a href="#/budget/plan" className="rounded-xl bg-white py-3 text-center text-sm font-medium ring-1 ring-slate-200">
          Build from history
        </a>
      </div>

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
  // Each detected bill and subscription under the bill line it's filed in (DECISIONS.md 101); ones filed elsewhere
  // (a streaming charge in Entertainment) are listed after, so every recurring charge shows here individually.
  const live = bills.filter((b) => b.status !== 'canceled')
  const lineIds = new Set(g.categories.map((c) => c.id))
  const elsewhere = live.filter((b) => !b.categoryId || !lineIds.has(b.categoryId))
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
              {items.length > 0 && (
                <ul className="mb-2 ml-4 mr-4 divide-y divide-slate-100 border-l-2 border-slate-100 pl-3">
                  {items.map((b) => (
                    <BillItemRow key={b.key} b={b} month={month} today={today} />
                  ))}
                </ul>
              )}
            </li>
          )
        })}
      </ul>
      {elsewhere.length > 0 && (
        <>
          <p className="mt-3 text-xs text-slate-500">
            Also recurring, but filed under spending categories. Make one its own bill line on the{' '}
            <a href="#/bills" className="underline">
              Bills screen
            </a>
            .
          </p>
          <ul className="mt-1 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white px-4 ring-1 ring-slate-200">
            {elsewhere.map((b) => (
              <BillItemRow key={b.key} b={b} month={month} today={today} showCategory />
            ))}
          </ul>
        </>
      )}
    </section>
  )
}

/** One bill or subscription: paid this budget month, due (or late), or next due after it. */
function BillItemRow({ b, month, today, showCategory }: { b: RecurringItem; month: { start: string; end: string }; today: string; showCategory?: boolean }) {
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
      <a href="#/bills" className="flex items-baseline justify-between gap-3 py-2 text-sm">
        <span className="min-w-0">
          <span className="block truncate">{b.name}</span>
          <span className={`block text-xs ${cls}`}>
            {text}
            {showCategory && b.categoryName && <span className="text-slate-400"> · in {b.categoryName}</span>}
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
function LastMonthCard({ last, onClose, onSweep }: { last: LastMonth; onClose: (start: string) => void; onSweep: (id: string, done: boolean) => void }) {
  const name = last.month.label.split(' ')[0]
  return (
    <section aria-labelledby="last-h" className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="last-h" className="text-sm font-semibold">
          {last.month.label}
        </h2>
        <span className="text-xs text-slate-500">{last.closed ? 'Closed' : `Closes ${formatDay(last.closesOn)}`}</span>
      </div>

      {last.deficitCents > 0 ? (
        <p className="mt-1 text-sm">
          Ended <span className="font-semibold tabular-nums text-red-600">{formatCents(last.deficitCents)} over</span>. That's carried into this
          month and comes out of Left to Spend.
        </p>
      ) : last.surplusCents > 0 ? (
        <p className="mt-1 text-sm">
          Ended <span className="font-semibold tabular-nums text-emerald-700">{formatCents(last.surplusCents)} under</span>. Leftover money
          isn't added to this month; {last.closed ? 'send it here:' : 'once it closes, send it here:'}
        </p>
      ) : (
        <p className="mt-1 text-sm">Ended exactly on budget.</p>
      )}
      {last.carriedInDeficitCents > 0 && (
        <p className="mt-1 text-xs text-slate-500">Includes {formatCents(last.carriedInDeficitCents)} carried in from the month before.</p>
      )}

      {last.sweeps.length > 0 && (
        <ul className="mt-3 space-y-2">
          {last.sweeps.map((s, i) => (
            <li key={s.id === '00000000-0000-0000-0000-000000000000' ? i : s.id} className="flex items-center justify-between gap-3 rounded-xl bg-slate-50 px-3 py-2 text-sm">
              <span className={s.done ? 'text-slate-400 line-through' : ''}>
                Send <span className="font-semibold tabular-nums">{formatCents(s.amountCents)}</span> to {s.destination}
              </span>
              {last.closed && (
                <button type="button" onClick={() => onSweep(s.id, !s.done)} className="shrink-0 font-medium text-slate-700 underline">
                  {s.done ? 'Undo' : 'Mark sent'}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {!last.closed && (
        <button
          type="button"
          onClick={() => {
            if (window.confirm(`Close ${name} now? Transactions that arrive later won't change its result.`)) onClose(last.month.start)
          }}
          className="mt-3 text-sm font-medium text-slate-700 underline"
        >
          Close {name} now
        </button>
      )}
    </section>
  )
}

/**
 * SPEC section 3: money in (your income plus partner contributions) = all limits + an explicit remainder.
 * Your own income and the partner's contributions are shown separately, each expected vs received so far.
 */
function IncomeCard({ summary: s, onIncome }: { summary: BudgetSummary; onIncome: (income: number, partner: number) => void }) {
  const [editing, setEditing] = useState(false)
  const [income, setIncome] = useState(centsToInput(s.incomeExpectedCents))
  const [partner, setPartner] = useState(centsToInput(s.partnerExpectedCents))
  const incomeCents = parseDollars(income)
  const partnerCents = parseDollars(partner)
  const over = s.unallocatedCents < 0
  const first = s.partnerName ? titleCase(s.partnerName.split(' ')[0]) : null
  const partnerLabel = first ? `From ${first}` : 'Partner contributions'
  const fixed = s.contributions ?? []

  return (
    <section aria-labelledby="income-h" className="mt-6 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="income-h" className="text-sm font-semibold">
          This month's plan
        </h2>
        {!editing && (
          <button type="button" onClick={() => setEditing(true)} className="text-xs font-medium text-slate-600 underline">
            Edit
          </button>
        )}
      </div>

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
          <dl className="mt-2 space-y-1 text-sm">
            <div className="flex justify-between gap-3">
              <dt className="text-slate-600">Your income</dt>
              <dd className="tabular-nums">
                {formatCents(s.incomeExpectedCents)} <span className="text-xs text-slate-500">· {formatCents(s.incomeReceivedCents)} in so far</span>
              </dd>
            </div>
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
          <dl className="mt-3 grid grid-cols-2 gap-2 text-center">
            <div className="rounded-xl bg-slate-50 p-2">
              <dt className="text-xs text-slate-500">Planned</dt>
              <dd className="text-sm font-semibold tabular-nums">{formatCents(s.plannedCents)}</dd>
            </div>
            <div className={`rounded-xl p-2 ${over ? 'bg-red-50' : 'bg-slate-50'}`}>
              <dt className="text-xs text-slate-500">{over ? 'Over what comes in' : 'Unallocated'}</dt>
              <dd className={`text-sm font-semibold tabular-nums ${over ? 'text-red-600' : ''}`}>{formatCents(Math.abs(s.unallocatedCents))}</dd>
            </div>
          </dl>
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
      </dd>
    </div>
  )
}

import { useCallback, useEffect, useRef, useState } from 'react'
import CategoryHistory from './CategoryHistory'
import {
  ApiError,
  budgetApi,
  budgetEditApi,
  type Account,
  budgetSummaryApi,
  type BudgetCategoryLine,
  type CategoryMonth,
  type BudgetGroup,
  type BudgetSummary,
  type Category,
  type ContributionStatus,
  type LastMonth,
  type RecurringItem,
  type SearchFilters,
  recurringApi,
  type Transaction,
  listedType,
} from './api'
import { useRemembered, useResume } from './offline'
import { Caret, DateField, ErrorNotice, headerActionCls, Loading, Notice, Page, PageHeader, smallBtn, smallBtnOn } from './ui'
import BudgetPlanner from './BudgetPlanner'
import SweepSetting from './SweepSetting'
import PlannedExpenses from './PlannedExpenses'
import CategoryEditSheet from './CategoryEditSheet'
import { ArchivedCategories, ResetCategories, UnusedCategories } from './CategoryTools'
import BillSheet from './BillSheet'
import { formatCents, formatDay, formatSigned, ordinal, parseDollars, titleCase, spendingBar, isMaxed } from './format'
import { TransactionEditSheet } from './ReviewScreen'

// Sub-views of the Budget tab, from the URL hash:
//   #/budget            categories overview
//   (#/budget/inbox moved to Tasks, #/tasks/transactions)
//   #/transactions      every transaction and search, opened from Money (DECISIONS.md 171, 282; #/budget/all still works);
//                       ?type=, accountId=, accountType=, unconfirmed=1, debtId= and back= open it filtered (DECISIONS.md 259)
//   #/budget/c/<id>     one category's transactions
//   #/budget/plan       budget settings and the budget generator
//   (#/budget/categories is gone (DECISIONS.md 228): its links open the budget, or the category)
type View = { kind: 'overview' } | { kind: 'all' } | { kind: 'plan' } | { kind: 'category'; id: string }

function viewFromHash(): View {
  const parts = window.location.hash.replace(/^#\/?/, '').split('?')[0].split('/')
  if (parts[0] === 'transactions' || parts[1] === 'all') return { kind: 'all' }
  if (parts[1] === 'plan') return { kind: 'plan' }
  if (parts[1] === 'categories' && parts[2]) return { kind: 'category', id: parts[2] }
  if (parts[1] === 'c' && parts[2]) return { kind: 'category', id: parts[2] }
  return { kind: 'overview' }
}


export default function BudgetScreen({ demo = false, onChanged, onSignedOut }: { demo?: boolean; onChanged: () => void; onSignedOut: () => void }) {
  const [view, setView] = useState<View>(viewFromHash)
  const [summary, setSummary] = useState<BudgetSummary | null>(null)
  const [month, setMonth] = useState<Transaction[]>([])
  const [bills, setBills] = useState<RecurringItem[]>([])
  const [categories, setCategories] = useState<Category[]>([])
  // A bill tapped under its bill line: its own pop-up, right here (DECISIONS.md 228).
  const [bill, setBill] = useState<RecurringItem | null>(null)
  const [selected, setSelected] = useState<Transaction | null>(null)
  const [editing, setEditing] = useState<BudgetCategoryLine | null | undefined>(undefined) // null = adding a new one
  const [error, setError] = useState<string | null>(null)
  const searchFailed = useCallback((e: unknown) => setError(e instanceof Error ? e.message : 'Search failed.'), [])
  const [notice, setNotice] = useState<string | null>(null)
  // A message to show on the page a change sends you to (an archived category's page is gone, so back to the budget).
  const carried = useRef<string | null>(null)

  useEffect(() => {
    const onHash = () => {
      setView(viewFromHash())
      setNotice(carried.current)
      carried.current = null
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
  useResume(load)

  function saved(message: string) {
    setSelected(null)
    setEditing(undefined)
    setBill(null)
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
  // Reorder in the edit pop-up: says where it is now, since the list behind the pop-up is hidden (DECISIONS.md 254).
  async function move(l: BudgetCategoryLine, direction: -1 | 1): Promise<string> {
    if (!summary) return ''
    const group = summary.groups.find((g) => g.categories.some((c) => c.id === l.id))!
    const ids = group.categories.map((c) => c.id)
    const i = ids.indexOf(l.id)
    const j = i + direction
    const where = (k: number) => `${ordinal(k + 1)} of ${ids.length} in ${groupNames[group.type as Category['type']] ?? group.label}`
    if (j < 0 || j >= ids.length) return `${l.name} is already ${direction < 0 ? 'first' : 'last'}: ${where(i)}.`
    ;[ids[i], ids[j]] = [ids[j], ids[i]]
    const all = summary.groups.flatMap((g) => (g === group ? ids : g.categories.map((c) => c.id)))
    await budgetEditApi.reorder(all)
    await load()
    const below = j > 0 ? `, after ${group.categories.find((c) => c.id === ids[j - 1])!.name}` : ''
    return `Moved ${direction < 0 ? 'up' : 'down'}. ${l.name} is now ${where(j)}${below}.`
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
                : (line?.name ?? 'Category')
        }
        sub={view.kind === 'overview' && summary ? `${summary.budgetMonth.label} · ${formatDay(summary.budgetMonth.start)} – ${formatDay(summary.budgetMonth.end)}` : undefined}
        back={
          view.kind === 'overview'
            ? undefined
            : view.kind === 'all'
              ? backFromHash()
              : { href: '#/budget', label: 'Budget' }
        }
        action={
          view.kind === 'category' && line ? (
            <button type="button" onClick={() => setEditing(line)} className={headerActionCls}>
              Edit
            </button>
          ) : view.kind === 'overview' ? (
            // Budget settings is a gear at the top right (DECISIONS.md 299).
            <a href="#/budget/plan" aria-label="Budget settings" title="Budget settings" className="neu-sm grid size-10 shrink-0 place-items-center rounded-full text-ink-soft">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className="size-5">
                <path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z" />
                <circle cx="12" cy="12" r="3" />
              </svg>
            </a>
          ) : undefined
        }
      />

      {notice && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!summary && !error && <Loading shape="list" />}

      {summary && view.kind === 'overview' && (
        <Overview summary={summary} bills={bills} onAdd={() => setEditing(null)} onOpenBill={setBill} onDone={saved} />
      )}

      {/* Budget settings (DECISIONS.md 168): one card for this month, then the planner. With the automatic budget off,
          the planner's own income fields do that job. */}
      {/* The one savings/debt split first: Debt and Savings link here (DECISIONS.md 321). */}
      {view.kind === 'plan' && <SweepSetting />}
      {view.kind === 'plan' && summary && <PlannedExpenses today={summary.today} onChanged={() => saved('Saved.')} />}
      {view.kind === 'plan' && (
        <BudgetPlanner
          top={() =>
            summary && (
              <IncomeCard
                summary={summary}
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
          {/* Not in the demo (DECISIONS.md 309): its categories are already the bank's. */}
          {!demo && <ResetCategories onDone={saved} />}
        </div>
      )}

      {summary && view.kind === 'category' && line && (
        <>
          <CategoryHeader line={line} />
          {/* Each bill is its own line (DECISIONS.md 279): its bill is here, to change its name, due day and the rest. */}
          {line.type === 'FixedBill' && bills.some((b) => b.categoryId === line.id && b.status !== 'canceled') && (
            <div className="mt-3 rounded-2xl neu px-4 py-1">
              <ul className="divide-y divide-ink/[0.07]">
                {bills
                  .filter((b) => b.categoryId === line.id && b.status !== 'canceled')
                  .map((b) => (
                    <BillItemRow key={b.key} b={b} month={summary.budgetMonth} today={summary.today} onOpen={() => setBill(b)} />
                  ))}
              </ul>
            </div>
          )}
          <CategoryMonths key={line.id} line={line} month={month} onSelect={setSelected} onError={searchFailed} />
        </>
      )}
      {summary && view.kind === 'category' && !line && (
        <p className="mt-6 text-sm text-ink-soft">This category no longer exists.</p>
      )}


      {summary && view.kind === 'all' && (
        <SearchableList key={window.location.hash} month={month} categories={categories} onSelect={setSelected} onError={searchFailed} />
      )}

      {selected && (
        <TransactionEditSheet transaction={selected} categories={categories} onClose={() => setSelected(null)} onSaved={saved} />
      )}


      {bill && (
        <BillSheet
          item={bill}
          items={bills}
          categories={categories}
          onClose={() => setBill(null)}
          onDone={saved}
          onError={(e) => {
            setBill(null)
            setError(e instanceof Error ? e.message : 'Something went wrong.')
          }}
        />
      )}

      {editing !== undefined && (
        <CategoryEditSheet
          line={editing}
          categories={categories}
          usualLimitCents={editing ? categories.find((c) => c.id === editing.id)?.monthlyLimitCents : undefined}
          onClose={() => setEditing(undefined)}
          onSaved={(message) => {
            // Archived, deleted or merged away: its page is gone, so back to the budget.
            if (editing && /^(Archived|Deleted|Merged) /.test(message) && view.kind === 'category') {
              carried.current = message
              window.location.hash = '#/budget'
            }
            saved(message)
          }}
          onMove={editing ? (d) => move(editing, d) : undefined}
          auto={editing && summary?.auto ? { on: summary.auto.on, setByYou: summary.auto.setByYou.includes(editing.id) } : undefined}
        />
      )}
    </Page>
  )
}

function Overview({
  summary: s,
  bills,
  onAdd,
  onOpenBill,
  onDone,
}: {
  summary: BudgetSummary
  bills: RecurringItem[]
  onAdd: () => void
  onOpenBill: (b: RecurringItem) => void
  onDone: (message: string) => void
}) {
  // Home has Left to Spend and the month's numbers; here it's just the categories (DECISIONS.md 281).
  const trimmed = !!s.auto?.on && s.auto.shortCents > 0
  const short = Math.max(s.overplannedCents, trimmed ? s.auto!.shortCents : 0)
  return (
    <>
      {/* Adding a category sits right under the month, full width; Budget settings is the gear up top (DECISIONS.md 169, 187, 299). */}
      <button type="button" onClick={onAdd} className="neu-sm mt-4 w-full rounded-2xl py-2.5 text-center text-sm font-semibold">
        + Add a Spending Category
      </button>

      {short > 0 && (
        <div className="mt-5 space-y-3">
          {/* One note for a plan beyond the month's money, whether or not the automatic budget already trimmed it (DECISIONS.md 194). */}
          {short > 0 && (
            <Notice tone="danger" href="#/budget/plan">
              The plan is {formatCents(short)} more than this month’s money{trimmed ? ', even with spending trimmed' : ''}.
            </Notice>
          )}
        </div>
      )}

      {s.lastMonth && <LastMonthCard last={s.lastMonth} />}

      {/* What needs a category or a confirmation is on the Review tab's badge, not here (DECISIONS.md 171). */}
      {s.auto && !s.auto.on && <AutoBudgetOff />}

      <UnusedCategories version={s} onDone={onDone} />

      {s.groups.map((g) => (
        <Group key={g.type} group={g} bills={g.type === 'FixedBill' ? bills : []} month={s.budgetMonth} today={s.today} onOpenBill={onOpenBill} />
      ))}

      <ArchivedCategories version={s} onDone={onDone} />
    </>
  )
}

function Group({
  group: g,
  bills,
  month,
  today,
  onOpenBill,
}: {
  group: BudgetGroup
  bills: RecurringItem[]
  month: { start: string; end: string }
  today: string
  onOpenBill: (b: RecurringItem) => void
}) {
  // Debt and savings are run from the Plan tab, with every option there (DECISIONS.md 228): a row goes there.
  const planHref = g.type === 'DebtPayment' ? '#/debt' : g.type === 'SavingsTransfer' ? '#/savings' : null
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
        </button>
      </h2>
      {open && (
        <>
      {g.type === 'DebtPayment' && (
        <p className="px-1 text-xs text-ink-faint">Only what you pay beyond this month’s card purchases counts.</p>
      )}
      <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
        {g.categories.map((c) => {
          const items = live.filter((b) => b.categoryId === c.id)
          // One bill per line (DECISIONS.md 279): its due date sits at the line's top right (299), and the bill opens from the line's page.
          const only = items.length === 1 ? billStatus(items[0], month, today) : null
          return (
            <li key={c.id}>
              <a href={planHref ?? `#/budget/c/${c.id}`} className="block px-4 py-1.5">
                {c.partOfCardPayments ? (
                  <InterestRow line={c} />
                ) : (
                  <CategoryRow line={c} spending={spending} status={only} />
                )}
                {c.interestIncludedCents > 0 && (
                  <span className="mt-1 block text-xs text-ink-faint">Your card minimums, with about {formatCents(c.interestIncludedCents)} of interest in them</span>
                )}
              </a>
              {items.length > 1 && <BillItems lineId={c.id} items={items} month={month} today={today} onOpen={onOpenBill} />}
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
function BillItems({
  lineId,
  items,
  month,
  today,
  onOpen,
}: {
  lineId: string
  items: RecurringItem[]
  month: { start: string; end: string }
  today: string
  onOpen: (b: RecurringItem) => void
}) {
  const [open, setOpen] = useRemembered(`budget.items.${lineId}`, items.length <= 3)
  return (
    <div className="mb-1.5 ml-4 mr-4 border-l-2 border-ink/10 pl-3">
      {items.length > 3 && (
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex items-center gap-1 py-1 text-xs font-medium text-ink-soft">
          <Caret open={open} />
          {items.length} bills
        </button>
      )}
      {open && (
        <ul className="divide-y divide-ink/[0.07]">
          {items.map((b) => (
            <BillItemRow key={b.key} b={b} month={month} today={today} onOpen={() => onOpen(b)} />
          ))}
        </ul>
      )}
    </div>
  )
}

/** A bill's state this budget month, and its color: paid, due (or late), or next due after it. */
function billStatus(b: RecurringItem, month: { start: string; end: string }, today: string): [string, string] {
  const paidThisMonth = b.lastDate >= month.start && b.lastDate <= month.end
  return b.status === 'possiblyCanceled'
    ? [`Stopped? Last ${formatDay(b.lastDate)}`, 'text-ink-faint']
    : paidThisMonth
      ? [`Paid ${formatDay(b.lastDate)}`, 'text-good']
      : b.nextExpected < today
        ? [`Due ${formatDay(b.nextExpected)}: not seen yet`, 'text-gold-ink']
        : b.nextExpected <= month.end
          ? [`Due ${formatDay(b.nextExpected)}`, 'text-ink-soft']
          : [`Next ${formatDay(b.nextExpected)}`, 'text-ink-faint']
}

/** One bill or subscription: paid this budget month, due (or late), or next due after it. */
function BillItemRow({ b, month, today, onOpen }: { b: RecurringItem; month: { start: string; end: string }; today: string; onOpen: () => void }) {
  const [text, cls] = billStatus(b, month, today)
  return (
    <li>
      <button type="button" onClick={onOpen} className="flex w-full items-baseline justify-between gap-3 py-1.5 text-left text-sm">
        {/* The date right after the name, on one line (DECISIONS.md 265); it wraps under a name too long to share it. */}
        <span className="flex min-w-0 flex-wrap items-baseline gap-x-2">
          <span className="max-w-full truncate">{b.name}</span>
          <span className={`whitespace-nowrap text-xs ${cls}`}>{text}</span>
        </span>
        <span className="shrink-0 tabular-nums">
          {formatCents(b.amountCents)}
          {(b.status === 'priceUp' || b.status === 'priceDown') && (
            <span className="block text-right text-[11px] text-gold-ink">{b.status === 'priceUp' ? 'price went up' : 'price went down'}</span>
          )}
        </span>
      </button>
    </li>
  )
}

function CategoryRow({ line: c, spending, status }: { line: BudgetCategoryLine; spending: boolean; status?: [string, string] | null }) {
  // Bar width is display only: how much of the limit is used, capped at full.
  const used = c.limitCents > 0 ? Math.min(1, Math.max(0, c.spentCents / c.limitCents)) : c.spentCents > 0 ? 1 : 0
  // Spending: red once it's maxed (DECISIONS.md 280). Card payments and savings: green once they're paid in full.
  const barColor = spending
    ? spendingBar(c.health, c.spentCents, c.remainingCents)
    : c.health === 'over'
      ? 'bg-danger'
      : c.limitCents > 0 && c.remainingCents <= 0
        ? 'bg-good'
        : 'bg-gold'
  return (
    <>
      {/* Two lines (DECISIONS.md 265): the name with spent of limit right after it, then the bar with what's left at its
          end. All three on one line didn't fit a phone ("Credit card payment $200.00 of $2,000.00 $1,800.00 to go"). The
          amount wraps under a name too long to share its line. */}
      <div className="flex items-baseline gap-2">
        <div className="flex min-w-0 flex-1 flex-wrap items-baseline gap-x-2">
          <span className="max-w-full truncate text-sm font-medium">{c.name}</span>
          <span className="whitespace-nowrap text-xs tabular-nums text-ink-soft">
            {formatCents(c.spentCents)} of {formatCents(c.limitCents)}
          </span>
          {c.hasMonthOverride && <span className="rounded bg-ink/[0.06] px-1.5 py-0.5 text-[10px] font-medium whitespace-nowrap text-ink-soft">this month only</span>}
        </div>
        {/* A bill's due or paid date, top right, so the line stays two rows (DECISIONS.md 299). */}
        {status && <span className={`shrink-0 whitespace-nowrap text-xs ${status[1]}`}>{status[0]}</span>}
      </div>
      <div className="mt-1 flex items-center gap-3">
        <div className="h-2 min-w-0 flex-1 overflow-hidden rounded-full neu-inset" aria-hidden>
          <div className={`h-full rounded-full ${barColor}`} style={{ width: `${used * 100}%` }} />
        </div>
        {/* A set width, so the bars line up from row to row. */}
        <span className="min-w-26 shrink-0 text-right text-sm leading-5">
          <RemainingLabel line={c} spending={spending} />
        </span>
      </div>
      {c.partnerOffsetCents > 0 && (
        <p className="mt-0.5 text-xs tabular-nums text-ink-soft">
          A friend covers {formatCents(c.partnerOffsetCents)} · your share <span className="font-medium text-ink-soft">{formatCents(c.limitCents - c.partnerOffsetCents)}</span>
        </p>
      )}
    </>
  )
}

/**
 * Card interest under Debt (DECISIONS.md 254, 285): what the cards charged so far, and what each is expected to charge this
 * month from its balance and APR. The card payment above pays it (its minimums include the interest), so it isn't extra.
 */
function InterestRow({ line: c }: { line: BudgetCategoryLine }) {
  const expected = c.expectedInterest ?? []
  const total = expected.reduce((n, x) => n + x.cents, 0)
  return (
    <>
      <div className="flex items-baseline justify-between gap-3">
        <span className="truncate text-sm font-medium">{c.name}</span>
        <span className="shrink-0 text-sm tabular-nums text-ink-soft">
          {formatCents(c.spentCents)} of about {formatCents(total)}
        </span>
      </div>
      {expected.length > 0 ? (
        <ul className="mt-1 space-y-0.5 text-xs text-ink-faint">
          {expected.map((x) => (
            <li key={x.card} className="flex justify-between gap-3">
              <span className="truncate">
                {x.card} at {(x.aprBps / 100).toFixed(2).replace(/\.?0+$/, '')}%
              </span>
              <span className="shrink-0 tabular-nums">≈ {formatCents(x.cents)}</span>
            </li>
          ))}
          <li>Part of the card payment above, not extra.</li>
        </ul>
      ) : (
        <p className="mt-1 text-xs text-ink-faint">Add your cards’ APRs on the Debt tab to expect it here.</p>
      )}
    </>
  )
}

function RemainingLabel({ line: c, spending }: { line: BudgetCategoryLine; spending: boolean }) {
  if (c.remainingCents < 0)
    return <span className="shrink-0 text-sm font-semibold tabular-nums text-danger">{formatCents(-c.remainingCents)} over</span>
  if (spending && isMaxed(c.spentCents, c.remainingCents)) return <span className="shrink-0 text-sm font-semibold text-danger">Maxed</span>
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
    </section>
  )
}

/** The page a filtered link came from (DECISIONS.md 259), for the back button; else Money, where Transactions lives (282). */
function backFromHash(): { href: string; label: string } {
  const back = new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('back')
  const pages: Record<string, string> = { income: 'Income', debt: 'Debt', savings: 'Savings', debit: 'Debit', budget: 'Budget' }
  // From an account's page under More → Accounts (DECISIONS.md 319).
  if (back && /^more\/accounts\/[0-9a-f-]{36}$/.test(back)) return { href: `#/${back}`, label: 'Account' }
  return back && pages[back] ? { href: `#/${back}`, label: pages[back] } : { href: '#/payday', label: 'Money' }
}

/** The filters a link opens the page with: #/transactions?type=income, say (DECISIONS.md 259). */
function filtersFromHash(): SearchFilters {
  const p = new URLSearchParams(window.location.hash.split('?')[1] ?? '')
  const type = p.get('type') ?? ''
  return {
    ...noFilters,
    q: p.get('q') ?? '',
    categoryId: p.get('categoryId') ?? '',
    type: (typeNames.some((x) => x.id === type) ? type : '') as SearchFilters['type'],
    accountId: p.get('accountId') ?? '',
    accountType: p.get('accountType') ?? '',
    unconfirmed: p.get('unconfirmed') === '1' || p.get('unconfirmed') === 'true',
    debtId: p.get('debtId') ?? '',
  }
}

const typeNames: { id: Exclude<SearchFilters['type'], ''>; label: string }[] = [
  { id: 'spending', label: 'Spending' },
  { id: 'bill', label: 'Bills' },
  { id: 'income', label: 'Income' },
  { id: 'transfer', label: 'Transfers' },
  { id: 'interest', label: 'Interest' },
  { id: 'debt', label: 'Debt payments' },
  { id: 'savings', label: 'Savings' },
]
const accountTypeNames: Record<string, string> = { checking: 'All checking', savings: 'All savings', card: 'All cards' }

/**
 * This month's transactions (with arrows back through earlier months), or a search across all of them (DECISIONS.md 150,
 * 164, 259): words, a category, a kind, an account, an amount range, a date range and only the unconfirmed ones, all
 * applied together. Other pages link here with filters already set.
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
  const [f, setF] = useState<SearchFilters>(filtersFromHash)
  const [open, setOpen] = useState(false)
  const [accounts, setAccounts] = useState<Account[]>([])
  useEffect(() => {
    let live = true
    budgetApi
      .accounts()
      .then((a) => live && setAccounts(a))
      .catch(() => undefined) // the Account filter just stays short
    return () => {
      live = false
    }
  }, [])
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
    type: f.type,
    accountId: f.accountId,
    accountType: f.accountType,
    unconfirmed: f.unconfirmed,
    debtId: f.debtId,
  }
  const key = JSON.stringify(sent)
  const searching = key !== JSON.stringify(noFilters)
  const filterCount = [f.categoryId, f.type, f.accountId || f.accountType, f.debtId, f.unconfirmed, f.min || f.max, f.from || f.to].filter(Boolean).length
  const accountValue = f.accountId ? `id:${f.accountId}` : f.accountType ? `type:${f.accountType}` : ''
  const accountName = f.accountId
    ? (accounts.find((a) => a.id === f.accountId)?.name ?? 'One account')
    : (accountTypeNames[f.accountType] ?? 'Account')

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
          className={`shrink-0 rounded-xl px-3.5 text-sm font-semibold ${open || filterCount > 0 ? 'bg-accent shadow-neu-sm text-accent-ink' : 'neu-sm text-ink-soft'}`}
        >
          Filters{filterCount > 0 && ` · ${filterCount}`}
        </button>
      </div>

      {open && (
        <div id="search-filters" className="mt-3 space-y-3 rounded-2xl neu p-3">
          <label className="block">
            <span className="text-xs font-medium text-ink-soft">Type</span>
            <select value={f.type} onChange={(e) => set({ type: e.target.value as SearchFilters['type'] })} className={`mt-1 ${field}`}>
              <option value="">Any type</option>
              {typeNames.map((x) => (
                <option key={x.id} value={x.id}>
                  {x.label}
                </option>
              ))}
            </select>
          </label>

          <label className="block">
            <span className="text-xs font-medium text-ink-soft">Account</span>
            <select
              value={accountValue}
              onChange={(e) => {
                const v = e.target.value
                set({ accountId: v.startsWith('id:') ? v.slice(3) : '', accountType: v.startsWith('type:') ? v.slice(5) : '' })
              }}
              className={`mt-1 ${field}`}
            >
              <option value="">Any account</option>
              {Object.entries(accountTypeNames).map(([id, name]) => (
                <option key={id} value={`type:${id}`}>
                  {name}
                </option>
              ))}
              {accounts.length > 0 && (
                <optgroup label="One account">
                  {accounts.map((a) => (
                    <option key={a.id} value={`id:${a.id}`}>
                      {a.mask ? `${a.name} ••${a.mask}` : a.name}
                    </option>
                  ))}
                </optgroup>
              )}
            </select>
          </label>

          <label className="flex cursor-pointer items-center gap-3 px-1">
            <input type="checkbox" checked={f.unconfirmed} onChange={(e) => set({ unconfirmed: e.target.checked })} className="size-4 shrink-0" />
            <span className="text-sm">Only ones not confirmed yet</span>
          </label>

          <label className="block">
            <span className="text-xs font-medium text-ink-soft">Category</span>
            <select value={f.categoryId} onChange={(e) => set({ categoryId: e.target.value })} className={`mt-1 ${field}`}>
              <option value="">Any category</option>
              {groupOrder.map((type) => {
                const inType = categories.filter((c) => listedType(c) === type)
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
              <DateField label="From" placeholder="Start date" value={f.from} max={f.to || undefined} onChange={(v) => set({ from: v })} />
              <span className="text-ink-faint">to</span>
              <DateField label="To" placeholder="End date" value={f.to} min={f.from || undefined} onChange={(v) => set({ to: v })} />
            </div>
            {datesBackwards && <p className="mt-1 text-xs text-danger">The start date is after the end date.</p>}
            <div className="mt-2 flex flex-wrap gap-1.5">
              {datePresets().map((p) => (
                <button
                  key={p.label}
                  type="button"
                  onClick={() => set({ from: p.from, to: p.to })}
                  aria-pressed={f.from === p.from && f.to === p.to}
                  className={`rounded-full px-2.5 py-1 text-xs font-medium ${f.from === p.from && f.to === p.to ? 'bg-accent text-accent-ink' : 'bg-ink/[0.06] text-ink-soft'}`}
                >
                  {p.label}
                </button>
              ))}
            </div>
          </fieldset>

          <div className="flex gap-3">
            <button type="button" onClick={() => setF({ ...noFilters, q: f.q })} className="flex-1 rounded-2xl neu py-3 text-sm font-medium text-ink-soft">
              Clear filters
            </button>
            <button type="button" onClick={() => setOpen(false)} className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 text-sm font-semibold text-primary-ink">
              Done
            </button>
          </div>
        </div>
      )}

      {/* What's being searched, each removable, while the filters are folded away. */}
      {!open && filterCount > 0 && (
        <div className="mt-2 flex flex-wrap gap-1.5">
          {f.type && <Chip label={typeNames.find((x) => x.id === f.type)?.label ?? 'Type'} onRemove={() => set({ type: '' })} />}
          {(f.accountId || f.accountType) && <Chip label={accountName} onRemove={() => set({ accountId: '', accountType: '' })} />}
          {f.debtId && <Chip label="One card or loan" onRemove={() => set({ debtId: '' })} />}
          {f.unconfirmed && <Chip label="Not confirmed yet" onRemove={() => set({ unconfirmed: false })} />}
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
      {searching && results === null && <Loading label="Searching" shape="list" />}
      {searching && results && (
        <div className="mt-3 flex items-baseline justify-between gap-3 text-xs text-ink-soft" aria-live="polite">
          <p>
            <span className="font-semibold text-ink-soft">
              {results.total} {results.total === 1 ? 'match' : 'matches'}
            </span>
            {results.total > 0 && results.netCents !== 0 && ` · ${formatCents(Math.abs(results.netCents))} ${results.netCents < 0 ? 'back' : 'spent'}`}
            {results.total > results.items.length && ` · showing the newest ${results.items.length}`}
          </p>
          <button type="button" onClick={() => setF(noFilters)} className={`${smallBtn} shrink-0 text-danger`}>
            Clear all
          </button>
        </div>
      )}
      {!searching && shown === null && <Loading shape="list" />}
      {!searching && shown && (
        <TransactionList items={shown} empty={back === 0 ? 'No transactions this month yet. Import a CSV from More.' : `No transactions in ${label}.`} onSelect={onSelect} />
      )}
      {searching && results && <TransactionList items={results.items} empty="Nothing matches all of that. Try removing a filter." onSelect={onSelect} />}
    </>
  )
}

const noFilters: SearchFilters = { q: '', categoryId: '', min: '', max: '', from: '', to: '', type: '', accountId: '', accountType: '', unconfirmed: false, debtId: '' }
const groupOrder: Category['type'][] = ['Variable', 'FixedBill', 'DebtPayment', 'SavingsTransfer']
const groupNames: Record<Category['type'], string> = { Variable: 'Spending Categories', FixedBill: 'Bills', DebtPayment: 'Debt', SavingsTransfer: 'Savings' }

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
    <span className="inline-flex items-center gap-1 rounded-full bg-accent py-1 pr-1 pl-2.5 text-xs font-medium text-accent-ink">
      {label}
      <button type="button" onClick={onRemove} aria-label={`Remove ${label}`} className="rounded-full px-1.5 leading-none text-ink-faint">
        ×
      </button>
    </span>
  )
}

/**
 * A category's history chart and its transactions for the month picked in the chart (DECISIONS.md 234): this month's
 * from the budget, an earlier month's looked up by its dates.
 */
function CategoryMonths({
  line,
  month,
  onSelect,
  onError,
}: {
  line: BudgetCategoryLine
  month: Transaction[]
  onSelect: (t: Transaction) => void
  onError: (e: unknown) => void
}) {
  const [picked, setPicked] = useState<CategoryMonth | null>(null)
  const [earlier, setEarlier] = useState<{ key: string; items: Transaction[] } | null>(null)
  const past = picked && !picked.current ? picked : null
  useEffect(() => {
    if (!past) return
    let live = true
    budgetApi
      .search({ ...noFilters, categoryId: line.id, from: past.start, to: past.end })
      .then((r) => live && setEarlier({ key: past.month, items: r.items }))
      .catch((e) => live && onError(e))
    return () => {
      live = false
    }
  }, [past, line.id, month, onError])
  const label = past ? new Date(past.month + 'T12:00:00').toLocaleDateString('en-US', { month: 'long' }) : null
  const items = past ? (earlier && earlier.key === past.month ? earlier.items : null) : month.filter((t) => t.categoryId === line.id)

  return (
    <>
      <CategoryHistory categoryId={line.id} limitCents={line.limitCents} spending={line.type === 'Variable'} onPick={setPicked} />
      {label && <h2 className="mt-5 px-1 text-sm font-semibold text-ink-soft">{label}</h2>}
      {items === null ? (
        <p className="mt-4 px-1 text-sm text-ink-soft">Loading {label}…</p>
      ) : (
        <TransactionList
          items={items}
          empty={label ? `Nothing in this category in ${label}.` : 'Nothing in this category yet this month.'}
          onSelect={onSelect}
          className={label ? 'mt-2' : 'mt-4'}
        />
      )}
    </>
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
              <a href="#/tasks/payday" className={`${smallBtn} py-1! text-accent`}>
                Where to send it: Pay period tasks
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
            ['Money from friends that didn’t come', last.partnerShortfallCents ?? 0, -1],
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
 * Budget settings' one card (DECISIONS.md 168, 299): this month's income and partner money, money in vs planned, and any
 * shortfall, once.
 */
function IncomeCard({
  summary: s,
  onIncome,
}: {
  summary: BudgetSummary
  onIncome: (income: number, partner: number, automatic?: boolean) => void
}) {
  const over = s.unallocatedCents < 0
  const first = s.partnerName
  const partnerLabel = first ? `From ${first}` : 'From friends'
  const fixed = s.contributions ?? []
  const a = s.auto
  const moneyIn = s.incomeExpectedCents + s.partnerExpectedCents + (s.setAsideInCents ?? 0)
  const short = a?.on ? a.shortCents : Math.max(0, -s.unallocatedCents)

  return (
    <section aria-labelledby="income-h" className="mt-4 rounded-3xl neu p-4">
      {/* No Edit income button (DECISIONS.md 300): income comes from the paychecks. */}
      <h2 id="income-h" className="text-sm font-semibold">
        This month
      </h2>
      {a?.on && (
        <p className="mt-1 text-xs text-ink-soft">
          Updates itself as your pay, bills and spending change. Limits you set yourself stay.
        </p>
      )}

          <dl className="mt-3 space-y-1 text-sm">
            <div className="flex justify-between gap-3">
              <dt className="text-ink-soft">Your income</dt>
              <dd className="tabular-nums">
                {formatCents(s.incomeExpectedCents)} <span className="text-xs text-ink-soft">· {formatCents(s.incomeReceivedCents)} in so far</span>
              </dd>
            </div>
            {/* No note on where the income comes from or the last change (DECISIONS.md 299): only the way back to paychecks. */}
            {s.auto?.on && s.auto.incomeSetByYou && (
              <p className="text-xs text-ink-soft">
                You set this.{' '}
                <button type="button" onClick={() => onIncome(0, s.partnerExpectedCents, true)} className={`${smallBtnOn} py-1!`}>
                  Use your paychecks instead
                </button>
              </p>
            )}
            {/* Money set aside before this month for a big expense paid in it (DECISIONS.md 327). */}
            {(s.setAsideInCents ?? 0) > 0 && (
              <div className="flex justify-between gap-3">
                <dt className="text-ink-soft">Set aside earlier</dt>
                <dd className="tabular-nums">
                  {formatCents(s.setAsideInCents)} <span className="text-xs text-ink-soft">· in checking</span>
                </dd>
              </div>
            )}
            {fixed.length === 0 ? (
            <div className="flex justify-between gap-3">
              <dt className="text-ink-soft">{partnerLabel}</dt>
              <dd className="tabular-nums">
                {formatCents(s.partnerExpectedCents)} <span className="text-xs text-ink-soft">· {formatCents(s.partnerReceivedCents)} in so far</span>
              </dd>
            </div>
            ) : (
              fixed.map((f) => <ContributionRow key={f.arrangementId} f={f} who={f.friendName ?? 'Your friend'} />)
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
          {/* Fitted to the bank too (DECISIONS.md 233): what the accounts can't cover stays out of the plan. */}
          {a?.on && a.heldBackCents > 0 && (
            <p className="mt-3 rounded-lg neu-inset px-3 py-2 text-xs text-ink-soft">
              <span className="font-semibold tabular-nums">{formatCents(a.heldBackCents)}</span> of the month’s money is left out of the plan: your accounts can’t
              cover it this month, usually card charges from last month that this month’s pay still has to pay off. So the plan never spends more than
              you’ll have.
            </p>
          )}
          {short > 0 && (
            <p role="alert" className="mt-3 rounded-lg neu-inset px-3 py-2 text-xs text-danger">
              {a?.on
                ? `Even with spending squeezed as far as it sensibly goes, this month is ${formatCents(short)} short. Bills and minimums come first; the rest has to come from savings or extra pay.`
                : `This month plans ${formatCents(short)} more than comes in.`}
            </p>
          )}
          {!s.partnerName && (
            <p className="mt-2 text-xs text-ink-soft">
              <a href="#/friends" className={`${smallBtn} py-1! text-accent`}>
                Add a friend’s name on their transfers
              </a>{' '}
              to track what they send apart from your income.
            </p>
          )}
    </section>
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
          // Sent before the budget month started? Their friend page can count it here (DECISIONS.md 166).
          <a href={f.friendId ? `#/friends/${f.friendId}` : '#/friends'} className="block text-xs font-medium text-accent">
            Sent early? ›
          </a>
        )}
      </dd>
    </div>
  )
}

import { useCallback, useEffect, useMemo, useState } from 'react'
import { ask } from './confirm'
import {
  ApiError,
  budgetApi,
  budgetEditApi,
  recurringApi,
  partnerPlanApi,
  type Category,
  type ChargeBillChoices,
  type ReviewScope,
  type ReviewSummary,
  type Transaction,
  type TransactionContext,
  type TransactionChoices,
  type RecurringItem,
  type RecurringUpdate,
  listedType,
} from './api'
import { BillOnCard } from './BillsSection'
import { useBills } from './useBills'
import type { SheetPart } from './bills'
import { ErrorNotice, Loading, Page, PageHeader, reviewCard, reviewPrimary, reviewSecondary, smallBtn, smallBtnOn } from './ui'
import { formatCents, formatDay, formatSigned, ordinal, titleCase } from './format'

type Scope = 'todo' | 'earlier' | 'older'



/** Partner money first, then purchases with no category, then the app's least certain guesses, then likely ones. */
function rank(t: Transaction) {
  if (t.isPartnerTransfer) return 0
  if (!t.categoryId && t.kind !== 'PartnerContribution' && t.kind !== 'PartnerTransfer') return 1
  return t.unsure ? 2 : 3
}

/**
 * Review transactions, under Tasks (DECISIONS.md 87, 206, 259): every new transaction comes through here, with the
 * app's most likely answer up front. Cards go one at a time; the list confirms them one tap each; "Use most likely for
 * all" settles a whole part at once. Nothing here is settled until you say so (DECISIONS.md 80).
 */
export default function ReviewScreen({ onChanged, onSignedOut }: { onChanged: (count?: number) => void; onSignedOut: () => void }) {
  const [scope, setScope] = useState<Scope>('todo')
  const [mode, setMode] = useState<'cards' | 'list'>('cards')
  const [queue, setQueue] = useState<Transaction[] | null>(null)
  const [summary, setSummary] = useState<ReviewSummary | null>(null)
  const [todoCount, setTodoCount] = useState<number | null>(null)
  const [categories, setCategories] = useState<Category[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Your bills, for a charge you mark as a bill. There's no separate bill review (DECISIONS.md 248): every bill's
  // charge comes through here as a transaction first.
  const bills = useBills(onSignedOut, noop, () => setNotice(null))

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )

  const loadSide = useCallback(async () => {
    const [s, c] = await Promise.all([budgetApi.reviewSummary(), budgetApi.inboxCount()])
    setSummary(s)
    setTodoCount(c.count + c.review)
    return c.count + c.review
  }, [])
  const loadQueue = useCallback(
    async (which: Scope) => {
      const items = await budgetApi.review(which === 'todo' ? 'month' : which) // includes purchases with no category
      const unique = [...new Map(items.map((t) => [t.id, t])).values()]
      setQueue(unique.sort((a, b) => rank(a) - rank(b) || b.date.localeCompare(a.date)))
    },
    [],
  )

  const loadCategories = useCallback(async () => setCategories(await budgetApi.categories()), [])

  useEffect(() => {
    Promise.all([loadQueue(scope), loadSide(), loadCategories()]).catch(fail)
  }, [scope, loadQueue, loadSide, loadCategories, fail])


  /**
   * After one card: drop it locally (no jumpy reload), refresh the counts and the badge. Filing a transfer's other half,
   * or applying it to the merchant, may have settled other cards too, so then the list is fetched again.
   */
  async function done(id: string, message: string, reload = false) {
    setNotice(message)
    setError(null)
    setQueue((q) => q?.filter((t) => t.id !== id) ?? null)
    try {
      if (reload) await loadQueue(scope)
      onChanged(await loadSide())
    } catch (e) {
      fail(e)
    }
  }

  // "Use most likely for all" (DECISIONS.md 259), for the part you're looking at.
  async function settleAllMostLikely() {
    const n = queue?.length ?? 0
    if (!(await ask(`Use the most likely for all ${n}? Any without one go under “Other”. You can still change any of them later.`, { yes: 'Use most likely' })))
      return
    try {
      const r = await budgetApi.mostLikelyAll(reviewScope)
      setNotice(`Confirmed ${r.confirmed} with the most likely.`)
      onChanged()
      await Promise.all([loadQueue(scope), loadSide()])
    } catch (e) {
      fail(e)
    }
  }

  const counts = scope === 'todo' ? summary?.month : scope === 'earlier' ? summary?.earlier : summary?.older
  const reviewScope: ReviewScope = scope === 'todo' ? 'month' : scope
  const current = queue?.[0]
  // Earlier and Older only show up when something there is waiting (DECISIONS.md 149).
  const scopes = (['todo', 'earlier', 'older'] as const).filter(
    (x) => x === 'todo' || x === scope || (x === 'earlier' ? summary?.earlier.total : summary?.older.total),
  )

  return (
    <Page>
      <PageHeader title="Review transactions" back={{ href: '#/tasks', label: 'Tasks' }} />
      {/* The view switch sits on a row of its own under the title (DECISIONS.md 201), on the left like the filters below it (212). */}
      <div className="mt-3 flex">
        <div role="tablist" aria-label="View" className="flex rounded-full neu-inset p-1 text-xs font-medium">
          {(['cards', 'list'] as const).map((m) => (
            <button
              key={m}
              role="tab"
              aria-selected={mode === m}
              type="button"
              onClick={() => setMode(m)}
              className={`rounded-full px-4 py-1 ${mode === m ? 'neu-sm text-accent' : 'text-ink-soft'}`}
            >
              {m === 'cards' ? 'One by one' : 'List'}
            </button>
          ))}
        </div>
      </div>

      {/* Compact so a whole review card fits on a phone screen without scrolling (DECISIONS.md 161). */}
      {scopes.length > 1 && (
          <div className="mt-3 flex flex-wrap gap-2" role="tablist" aria-label="Which transactions">
            {scopes.map((s) => {
              const n = s === 'todo' ? todoCount : s === 'earlier' ? summary?.earlier.total : summary?.older.total
              return (
                <button
                  key={s}
                  role="tab"
                  aria-selected={scope === s}
                  type="button"
                  onClick={() => {
                    setScope(s)
                    setQueue(null)
                    setNotice(null)
                  }}
                  className={`shrink-0 rounded-full px-3 py-1.5 text-xs font-semibold ${scope === s ? 'bg-accent text-accent-ink' : 'bg-ink/[0.06] text-ink-soft'}`}
                >
                  {s === 'todo' ? 'This month' : s === 'earlier' ? 'Earlier' : 'Older'}
                  {n != null && n > 0 && <span className="ml-1 opacity-70">{n}</span>}
                </button>
              )
            })}
          </div>
      )}

      {(notice ?? bills.notice) && (
        <p role="status" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice ?? bills.notice}
        </p>
      )}
      {(error ?? bills.error) && <ErrorNotice message={(error ?? bills.error)!} onRetry={() => window.location.reload()} />}

      {scope !== 'todo' && summary && (
        <div className="mt-3 rounded-2xl neu-inset px-4 py-3 text-xs">
          <p className="text-ink-soft">
            {scope === 'earlier'
              ? `These months (since ${formatDay(summary.windowStart)}) shape your budget proposal. If you can’t remember one, the most likely is fine: what matters is that each is filed somewhere.`
              : `Before ${formatDay(summary.windowStart)}: these don’t affect your budget. There’s no need to remember them.`}
          </p>
        </div>
      )}

      {queue === null ? (
        <Loading />
      ) : queue.length === 0 ? (
        <div className="mt-3 rounded-3xl neu px-5 py-8 text-center">
          <p className="text-lg font-semibold">All caught up</p>
          <p className="mt-1 text-sm text-ink-soft">
            {scope === 'todo' ? 'Everything this month has a category you confirmed.' : scope === 'earlier' ? 'Nothing from these months is waiting.' : 'Nothing older is waiting.'}
          </p>
          {scope === 'todo' && (summary?.earlier.total ?? 0) > 0 && (
            <button type="button" onClick={() => setScope('earlier')} className={`${smallBtn} mt-4 text-accent`}>
              Review {summary!.earlier.total} from earlier months
            </button>
          )}
        </div>
      ) : (
        <>
          <div className="mt-3 flex min-h-8 items-center justify-between gap-3">
            <p className="text-xs text-ink-soft">
              <span className="font-semibold">{queue.length}</span> to go
              {counts && counts.unsure > 0 && <span className="text-ink-faint"> · {counts.unsure} the app wasn’t sure about</span>}
            </p>
            {queue.length > 1 && (
              <button type="button" onClick={() => void settleAllMostLikely()} className={`${smallBtnOn} shrink-0`}>
                Use most likely for all
              </button>
            )}
          </div>

          {mode === 'cards' && current && (
            <ReviewCard
              key={current.id}
              t={current}
              billItems={bills.s?.items ?? []}
              onBillUpdate={(u, m) => void bills.update(u, m)}
              onBillOpen={(i, part) => bills.openSheet(i, part)}
              onTrackAsBill={(i) => void bills.trackAsBill(i)}
              categories={categories}
              onDone={(message, reload) => void done(current.id, message, reload)}
              older={!!summary && current.date < summary.windowStart}
              onSkip={() => setQueue((q) => (q && q.length > 1 ? [...q.slice(1), q[0]] : q))}
              onCategoryCreated={loadCategories}
              onError={fail}
            />
          )}

          {mode === 'list' && (
            <ul className="mt-3 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
              {queue.slice(0, 200).map((t) => (
                <ListRow
                  key={t.id}
                  t={t}
                  label={mostLikely(t, categories)}
                  onOpen={() => {
                    setQueue((q) => (q ? [t, ...q.filter((x) => x.id !== t.id)] : q))
                    setMode('cards')
                  }}
                  onConfirm={async () => {
                    try {
                      await budgetApi.mostLikely([t.id])
                      await done(t.id, `Confirmed ${mostLikely(t, categories)}.`, t.isTransfer)
                    } catch (e) {
                      fail(e)
                    }
                  }}
                />
              ))}
            </ul>
          )}
          {mode === 'list' && queue.length > 200 && (
            <p className="mt-2 text-center text-xs text-ink-faint">Showing the first 200. Confirm some to see the rest.</p>
          )}
        </>
      )}
      {bills.sheet}
    </Page>
  )
}

/**
 * One transaction to review or edit (DECISIONS.md 259): the same card in both places. A switch picks what it is
 * (Spending, Bill, Income, Transfer or Interest), with the app's most likely answer already picked and one small green
 * button to take it as is. Review has "Skip for now" at the bottom; editing has Cancel.
 */
function ReviewCard({
  t,
  categories,
  billItems,
  onBillUpdate,
  onBillOpen,
  onTrackAsBill,
  older,
  editing = false,
  onDone,
  onSkip,
  onCategoryCreated,
  onError,
}: {
  t: Transaction
  categories: Category[]
  /** Your bills, to show the one this charge is filed to like the bill review card does (DECISIONS.md 245). */
  billItems: RecurringItem[]
  onBillUpdate: (u: RecurringUpdate, message: string) => void
  onBillOpen: (item: RecurringItem, part: SheetPart) => void
  onTrackAsBill: (item: RecurringItem) => void
  /** Before the months the budget is built from. */
  older?: boolean
  /** In a transaction's edit pop-up (DECISIONS.md 247): Cancel instead of "Skip for now" (onSkip cancels). */
  editing?: boolean
  onDone: (message: string, reload?: boolean) => void
  onSkip: () => void
  onCategoryCreated: () => Promise<void>
  onError: (e: unknown) => void
}) {
  const first = t.friendName ?? 'your friend' // which friend it's to or from (DECISIONS.md 211)
  const moneyIn = t.amountCents < 0
  const partnerIn = t.isPartnerTransfer && moneyIn
  const partnerOut = t.isPartnerTransfer && !moneyIn
  // Not confirmed yet: the app's answer is only its most likely one.
  const unconfirmed = t.needsReview || (!t.categoryId && !t.isTransfer && t.kind !== 'Income' && t.kind !== 'PartnerContribution' && t.kind !== 'Interest')
  const [pickedTab, setTab] = useState<Tab | null>(null)
  const tab = pickedTab ?? initialTab(t, categories)
  const [categoryId, setCategoryId] = useState<string | null>(t.categoryId ?? t.suggestedCategoryId)
  const [adding, setAdding] = useState(false)
  const [busy, setBusy] = useState(false)
  const [applyAll, setApplyAll] = useState(false)
  const [context, setContext] = useState<TransactionContext | null>(null)
  // Accounts, loans, debts, friends and bonuses for the dropdowns.
  const [choices, setChoices] = useState<TransactionChoices | null>(null)
  // Income: what kind, which friend (or a new one, and whether it's their fixed contribution), which bonus (or a new one).
  const [incomeAs, setIncomeAs] = useState<IncomeAs | null>(null)
  const [friendPick, setFriendPick] = useState<string | null>(null)
  const [friendName, setFriendName] = useState('')
  const [fixed, setFixed] = useState(false)
  const [fixedToward, setFixedToward] = useState('')
  const [bonusPick, setBonusPick] = useState<string | null>(null)
  const [bonusName, setBonusName] = useState('')
  // Transfer: the other account ("acct:<id>", "loan:<id>" or OUTSIDE) and, for savings with several goals, which goal.
  const [otherPick, setOtherPick] = useState<string | null>(null)
  const [goalPick, setGoalPick] = useState('')
  // Interest: "debt:<id>" or "savings:<id>".
  const [interestPick, setInterestPick] = useState<string | null>(null)
  // A bill (DECISIONS.md 225, 279): one of your bills ("bill:<key>"), a bill line with no bill yet ("line:<id>"), or a new
  // bill with a line of its own.
  const [billChoices, setBillChoices] = useState<ChargeBillChoices | null>(null)
  const [billPick, setBillPick] = useState('')
  const [billName, setBillName] = useState('')
  const [billCat, setBillCat] = useState(OWN)

  // A memory aid: how you filed this merchant before (DECISIONS.md 102).
  useEffect(() => {
    let live = true
    budgetApi.context(t.id).then((c) => live && setContext(c)).catch(() => undefined)
    budgetApi.choices().then((c) => live && setChoices(c)).catch(() => undefined)
    return () => {
      live = false
    }
  }, [t.id])

  const asBill = tab === 'bill'
  // Your bills, once you say it's a bill: the one it already belongs to picked, or a new bill when it's a repeating
  // charge that isn't one yet.
  useEffect(() => {
    if (!asBill || billChoices) return
    let live = true
    recurringApi
      .billChoices(t.id)
      .catch(() => ({ bills: [], lines: [], current: null, detected: null, suggested: null }) as ChargeBillChoices)
      .then((c) => {
        if (!live) return
        setBillChoices(c)
        // Already in a bill line that has no bill yet: that's its bill.
        const line = t.categoryId && c.lines.some((l) => l.id === t.categoryId) ? t.categoryId : null
        setBillPick(c.current ? `bill:${c.current}` : line ? `line:${line}` : c.detected || c.bills.length === 0 ? NEW : '')
        setBillCat(line ?? OWN)
        setBillName(c.suggested ?? titleCase(t.merchant))
      })
    return () => {
      live = false
    }
  }, [asBill, billChoices, t.id, t.categoryId, t.merchant])

  const selected = categories.find((c) => c.id === categoryId) ?? null
  const spendingCats = useMemo(() => categories.filter((c) => listedType(c) === 'Variable').sort((a, b) => a.sortOrder - b.sortOrder), [categories])
  const spendingPick = categoryId && spendingCats.some((c) => c.id === categoryId) ? categoryId : ''

  // Income: the most likely kind, until you pick one.
  const landedBonus = choices?.bonuses.find((b) => b.depositTransactionId === t.id)
  const income: IncomeAs | null =
    incomeAs ??
    (t.kind === 'PartnerContribution' || t.incomeType === 'friend' || partnerIn
      ? 'friend'
      : landedBonus
        ? 'bonus'
        : t.incomeType === 'paycheck'
          ? 'paycheck'
          : t.kind === 'Income'
            ? 'other'
            : null)
  const friends = choices?.friends ?? []
  const friend = friendPick ?? t.friendId ?? (friends.length === 1 ? friends[0].id : '')
  const pickedFriend = friends.find((f) => f.id === friend)
  const bonuses = choices?.bonuses.filter((b) => !b.depositTransactionId || b.depositTransactionId === t.id) ?? []
  const bonus = bonusPick ?? landedBonus?.id ?? (bonuses.length === 0 ? NEW : '')
  const pickedBonus = bonuses.find((b) => b.id === bonus)
  const fixedLines = useMemo(
    () => categories.filter((c) => c.type === 'Variable' || c.type === 'FixedBill').sort((a, b) => a.sortOrder - b.sortOrder),
    [categories],
  )
  const incomeReady =
    income === 'paycheck' ||
    income === 'other' ||
    (income === 'friend' && (friend === NEW ? friendName.trim().length > 0 : !!pickedFriend) && (!fixed || !!fixedToward)) ||
    (income === 'bonus' && (bonus === NEW ? bonusName.trim().length > 0 : !!pickedBonus))

  // Transfer: every other account of yours, loans you track by hand, or an account that isn't in Smooth Budget.
  const accounts = choices?.accounts.filter((a) => a.id !== t.accountId) ?? []
  const other =
    otherPick ??
    (t.transferAccountId && accounts.some((a) => a.id === t.transferAccountId)
      ? `acct:${t.transferAccountId}`
      : t.debtAccountId
        ? (accounts.find((a) => a.debtId === t.debtAccountId) && `acct:${accounts.find((a) => a.debtId === t.debtAccountId)!.id}`) ||
          (choices?.loans.some((l) => l.id === t.debtAccountId) ? `loan:${t.debtAccountId}` : '')
        : t.isTransfer && !t.transferAccountId && choices
          ? OUTSIDE
          : '')
  const otherAccount = other.startsWith('acct:') ? accounts.find((a) => `acct:${a.id}` === other) : undefined
  const otherLoan = other.startsWith('loan:') ? choices?.loans.find((l) => `loan:${l.id}` === other) : undefined
  const goals = otherAccount?.type === 'savings' && !moneyIn ? otherAccount.goals : []
  const goal = goalPick || (goals.length === 1 ? goals[0].id : '')
  const transferReady = (other === OUTSIDE || !!otherAccount || !!otherLoan) && (goals.length < 2 || !!goal)
  const otherName = other === OUTSIDE ? 'an account not in Smooth Budget' : (otherAccount?.name ?? otherLoan?.name ?? '')

  // Interest: the card or loan that charged it, or the savings account that paid it.
  const own = choices?.accounts.find((a) => a.id === t.accountId)
  const savingsAccounts = choices?.accounts.filter((a) => a.type === 'savings') ?? []
  const interestOn =
    interestPick ??
    (t.debtAccountId
      ? `debt:${t.debtAccountId}`
      : t.transferAccountId && savingsAccounts.some((a) => a.id === t.transferAccountId)
        ? `savings:${t.transferAccountId}`
        : own?.debtId
          ? `debt:${own.debtId}`
          : own?.type === 'savings'
            ? `savings:${own.id}`
            : choices?.debts.length === 1 && !moneyIn
              ? `debt:${choices.debts[0].id}`
              : '')
  const interestDebt = interestOn.startsWith('debt:') ? choices?.debts.find((d) => `debt:${d.id}` === interestOn) : undefined
  const interestSavings = interestOn.startsWith('savings:') ? savingsAccounts.find((a) => `savings:${a.id}` === interestOn) : undefined

  const pickedBill = billChoices?.bills.find((b) => `bill:${b.key}` === billPick)
  // A bill line with no bill yet (DECISIONS.md 279): this charge becomes its bill.
  const pickedLine = billChoices?.lines.find((l) => `line:${l.id}` === billPick)
  const billReady = billPick === NEW ? billName.trim().length > 0 : !!pickedBill || !!pickedLine
  // The bill picked, or (a new bill) the repeating charge it's part of.
  const billShown = billItems.find((i) => i.key === (pickedBill ? pickedBill.key : billPick === NEW || pickedLine ? billChoices?.detected : null))

  const ready =
    tab === 'spending'
      ? !!spendingPick
      : tab === 'bill'
        ? billReady
        : tab === 'income'
          ? incomeReady
          : tab === 'transfer'
            ? transferReady
            : !!interestDebt || !!interestSavings

  async function takeMostLikely() {
    setBusy(true)
    try {
      await budgetApi.mostLikely([t.id])
      onDone(`Used the most likely: ${mostLikely(t, categories)}.`, t.isTransfer)
    } catch (e) {
      onError(e)
      setBusy(false)
    }
  }

  /** Files it as you said; returns what to tell you, and whether other cards may have changed. */
  async function file(): Promise<[string, boolean]> {
    if (tab === 'income') {
      if (income === 'friend') {
        if (friend !== NEW && fixed && pickedFriend) {
          const day = Number(t.date.slice(8, 10))
          await partnerPlanApi.addContribution(pickedFriend.id, fixedToward, -t.amountCents, Math.max(1, day - 3), Math.min(31, day + 3))
        }
        const r = await budgetApi.markIncome(t.id, friend === NEW ? { as: 'friend', newFriendName: friendName.trim() } : { as: 'friend', friendId: friend })
        return [`Counted as money from ${r.friendName ?? 'your friend'}${fixed ? ', their fixed contribution each month' : ''}.`, true]
      }
      if (income === 'bonus') {
        await budgetApi.markIncome(t.id, bonus === NEW ? { as: 'bonus', newBonusName: bonusName.trim() } : { as: 'bonus', expectedIncomeId: bonus })
        return [`Counted as your ${bonus === NEW ? bonusName.trim() : pickedBonus!.name}.`, true]
      }
      await budgetApi.markIncome(t.id, { as: income! })
      return [income === 'paycheck' ? 'Counted as your paycheck, for the payday it landed on.' : 'Counted as other income: it adds to this month’s money.', true]
    }
    if (tab === 'transfer') {
      const r = await budgetApi.transfer(t.id, {
        accountId: otherAccount?.id ?? null,
        loanId: otherLoan?.id ?? null,
        goalId: goal || null,
      })
      const dir = moneyIn ? 'from' : 'to'
      const half = otherAccount
        ? r.otherHalfFound
          ? ` Its other half on ${otherAccount.name} is filed too.`
          : ` Its other half will be filed when it shows up on ${otherAccount.name}.`
        : ''
      return [`Transfer ${dir} ${otherName}.${half}`, true]
    }
    if (tab === 'interest') {
      await budgetApi.interest(t.id, interestDebt ? { debtId: interestDebt.id } : { accountId: interestSavings!.id })
      return [interestDebt ? `Interest on ${interestDebt.name}, in Card interest.` : `Interest from ${interestSavings!.name}: not counted as income.`, true]
    }
    if (tab === 'bill') {
      const r = await recurringApi.fileAsBill({
        transactionId: t.id,
        ...(pickedBill
          ? { billKey: pickedBill.key }
          : pickedLine
            ? { name: pickedLine.name, categoryId: pickedLine.id }
            : { name: billName.trim(), ...(billCat !== OWN ? { categoryId: billCat } : {}) }),
      })
      const moved = r.moved > 0 ? ` ${r.moved} other ${r.moved === 1 ? 'charge' : 'charges'} of this bill moved there too.` : ''
      if (billPick === NEW) await onCategoryCreated()
      return [`${pickedBill ? pickedBill.name : pickedLine ? `${pickedLine.name}: your bill there` : `New bill: ${billName.trim()}`}.${moved}`, true]
    }
    const r = await budgetApi.updateTransaction(t.id, spendingPick, false)
    const moved = r.billChargesMoved > 0 ? ` ${r.billChargesMoved} earlier ${r.billChargesMoved === 1 ? 'charge' : 'charges'} of this bill moved there too.` : ''
    const name = r.transaction.categoryName ?? selected?.name ?? 'Saved'
    return [
      partnerIn
        ? `Counted as paying you back for ${name}: it comes off that category.`
        : partnerOut
          ? `Counted as your share of ${name}.`
          : `${name}.${moved}`,
      r.billChargesMoved > 0 || t.isTransfer,
    ]
  }

  async function save() {
    setBusy(true)
    try {
      let [message, reload] = await file()
      // Every other one from this merchant you haven't confirmed, filed the same way (DECISIONS.md 259).
      if (applyAll && tab !== 'transfer') {
        const r = await budgetApi.applyToMerchant(t.id)
        if (r.applied > 0) {
          message += ` ${r.applied} more from ${titleCase(t.merchant)} filed the same way.`
          reload = true
        }
      }
      onDone(message, reload)
    } catch (e) {
      onError(e)
      setBusy(false)
    }
  }

  // The bank's description, unless it only repeats the store name.
  const showDescription = !!t.description && t.description.trim().toUpperCase() !== t.merchant.trim().toUpperCase()
  // How you filed it before says the same thing in more detail, so the one-line reason steps aside for it. Not for
  // income: why it looks like your bonus is news even from a payer you've seen before (DECISIONS.md 257).
  const reason = context && context.history.length > 0 && t.kind !== 'Income' ? null : t.categoryReason
  const notes = [
    showDescription ? t.description : null,
    reason,
    t.isPartnerTransfer && t.kind === 'PartnerTransfer' ? 'Not counted anywhere until you say what it was.' : null,
    older ? 'Too old to affect your budget.' : null,
  ].filter((x): x is string => !!x)
  const tabs = allTabs.filter((x) => x.id !== 'income' || moneyIn)
  const select = (filled: boolean) => `block w-full rounded-2xl neu-inset px-4 py-3 text-base ${filled ? 'font-medium' : 'text-ink-soft'}`
  const hint = (text: string) => <p className="mt-2 px-1 text-xs text-ink-faint">{text}</p>

  return (
    <article aria-label={`${editing ? 'Edit' : 'Review'} ${titleCase(t.merchant)}`} className={reviewCard}>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-base font-semibold">{titleCase(t.merchant)}</h2>
          <p className="mt-0.5 truncate text-xs text-ink-soft">
            {formatDay(t.date)} · {t.accountName}
          </p>
        </div>
        <p className={`shrink-0 text-lg font-semibold tabular-nums ${moneyIn ? 'text-good' : ''}`}>{formatSigned(t.amountCents)}</p>
      </div>
      {/* What it is, then one quiet line of why (DECISIONS.md 214). */}
      {(notes.length > 0 || (context && context.history.length > 0)) && (
        <p className="mt-2 break-words text-xs text-ink-soft">
          {context && context.history.length > 0 && (
            <>
              Filed as{' '}
              {context.history.map((h, i) => (
                <span key={h.categoryName}>
                  {i > 0 && (i === context.history.length - 1 ? ' and ' : ', ')}
                  <span className="font-semibold">{h.categoryName}</span> {h.count === 1 ? 'once' : `${h.count} times`}
                </span>
              ))}
              {notes.length > 0 ? ' · ' : '.'}
            </>
          )}
          {notes.join(' · ')}
        </p>
      )}

      {/* The app's most likely answer, in one tap (DECISIONS.md 259). */}
      {unconfirmed && (
        <button
          type="button"
          disabled={busy}
          onClick={() => void takeMostLikely()}
          className="mt-3 inline-flex max-w-full items-center gap-1 rounded-full bg-good/15 px-3 py-1.5 text-xs font-semibold text-good disabled:opacity-50"
        >
          <span className="truncate">Use most likely: {mostLikely(t, categories)}</span>
        </button>
      )}

      {/* What it is: the switch, then what that kind asks (DECISIONS.md 259). */}
      <fieldset className="mt-4">
        <legend className="sr-only">What was it?</legend>
        <div className="flex rounded-full neu-inset p-1">
          {tabs.map((x) => (
            <button
              key={x.id}
              type="button"
              aria-pressed={tab === x.id}
              onClick={() => {
                setTab(x.id)
                setAdding(false)
              }}
              className={`min-w-0 flex-1 truncate rounded-full py-1.5 text-xs font-semibold ${tab === x.id ? 'neu-sm text-accent' : 'text-ink-soft'}`}
            >
              {x.label}
            </button>
          ))}
        </div>
      </fieldset>

      {tab === 'spending' && (
        <div className="mt-3">
          <select
            aria-label="Spending category"
            value={spendingPick}
            onChange={(e) => {
              if (e.target.value === NEW) setAdding(true)
              else {
                setCategoryId(e.target.value)
                setAdding(false)
              }
            }}
            className={select(!!spendingPick)}
          >
            <option value="" disabled>
              Pick a spending category…
            </option>
            {spendingCats.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
            <option value={NEW}>+ New spending category…</option>
          </select>
          {adding && (
            <NewCategory
              type="Variable"
              suggestion={titleCase(t.merchant)}
              onCancel={() => setAdding(false)}
              onCreated={async (id) => {
                await onCategoryCreated()
                setCategoryId(id)
                setAdding(false)
              }}
              onError={onError}
            />
          )}
          {partnerIn
            ? hint(`${first} paying you back for something: it comes off that category, like a refund.`)
            : partnerOut
              ? hint(`Your share of something ${first} paid for.`)
              : moneyIn && hint('A refund: it comes off that category.')}
        </div>
      )}

      {/* A bill: one of your bills, or a new bill with its name and bill category (DECISIONS.md 225, 230). Its other charges follow it. */}
      {tab === 'bill' && (
        <div className="mt-3">
          {!billChoices ? (
            <p className="px-1 text-sm text-ink-soft">Finding your bills…</p>
          ) : (
            <>
              <select aria-label="Which bill" value={billPick} onChange={(e) => setBillPick(e.target.value)} className={select(!!billPick)}>
                <option value="" disabled>
                  Pick a bill…
                </option>
                {billChoices.bills.map((b) => (
                  <option key={b.key} value={`bill:${b.key}`}>
                    {b.name} · {formatCents(b.amountCents)}
                  </option>
                ))}
                {billChoices.lines.map((l) => (
                  <option key={l.id} value={`line:${l.id}`}>
                    {l.name}
                  </option>
                ))}
                <option value={NEW}>+ New bill…</option>
              </select>
              {billPick === NEW ? (
                <div className="mt-3 space-y-3">
                  <label className="block">
                    <span className="text-xs font-medium text-ink-soft">New bill’s name</span>
                    <input
                      value={billName}
                      maxLength={100}
                      onChange={(e) => setBillName(e.target.value)}
                      className="mt-1 block w-full rounded-2xl neu-inset px-4 py-3 text-base"
                    />
                  </label>
                  <p className="px-1 text-xs text-ink-faint">It gets its own line in your budget, and its earlier charges go there too.</p>
                </div>
              ) : (
                pickedBill && hint('Its charges stay together on that bill.')
              )}
              {/* The bill it's going to, as the bill review card shows it: price, what needs a look, charges. */}
              {billShown && (
                <BillOnCard
                  item={billShown}
                  onUpdate={onBillUpdate}
                  onOpen={(part = 'edit') => onBillOpen(billShown, part)}
                  onTrackAsBill={onTrackAsBill}
                />
              )}
            </>
          )}
        </div>
      )}

      {/* Income (DECISIONS.md 254, 259): a paycheck, a friend, a bonus, or other income. */}
      {tab === 'income' && (
        <div className="mt-3 space-y-3">
          <select
            aria-label="What kind of income"
            value={income ?? ''}
            onChange={(e) => setIncomeAs(e.target.value as IncomeAs)}
            className={select(!!income)}
          >
            <option value="" disabled>
              What kind of income?
            </option>
            <option value="paycheck">Paycheck</option>
            <option value="friend">Friend</option>
            <option value="bonus">Bonus</option>
            <option value="other">Other income</option>
          </select>
          {income === 'paycheck' && hint('Your pay from work. It counts as the check for the payday it landed on, and settles that payday’s tasks.')}
          {income === 'other' && hint('A rebate, a gift, anything else. It adds to this month’s money to spend.')}
          {income === 'friend' &&
            (!choices ? (
              <p className="px-1 text-sm text-ink-soft">Finding your friends…</p>
            ) : (
              <>
                <select
                  aria-label="Which friend sent it"
                  value={friend}
                  onChange={(e) => {
                    setFriendPick(e.target.value)
                    if (e.target.value === NEW) setFixed(false)
                  }}
                  className={select(!!friend)}
                >
                  <option value="" disabled>
                    Which friend sent it?
                  </option>
                  {friends.map((f) => (
                    <option key={f.id} value={f.id}>
                      {f.name}
                    </option>
                  ))}
                  <option value={NEW}>+ New friend…</option>
                </select>
                {friend === NEW && (
                  <label className="block">
                    <span className="text-xs font-medium text-ink-soft">Friend’s name</span>
                    <input
                      value={friendName}
                      maxLength={100}
                      onChange={(e) => setFriendName(e.target.value)}
                      className="mt-1 block w-full rounded-2xl neu-inset px-4 py-3 text-base"
                    />
                  </label>
                )}
                {pickedFriend && (
                  <label className="flex cursor-pointer items-start gap-3 px-1">
                    <input type="checkbox" checked={fixed} onChange={(e) => setFixed(e.target.checked)} className="mt-0.5 size-4 shrink-0" />
                    <span className="text-sm">
                      Their fixed contribution
                      <span className="block text-xs text-ink-faint">
                        {formatCents(-t.amountCents)} each month around the {ordinal(Number(t.date.slice(8, 10)))}.
                      </span>
                    </span>
                  </label>
                )}
                {pickedFriend && fixed && (
                  <select aria-label="What it goes toward" value={fixedToward} onChange={(e) => setFixedToward(e.target.value)} className={select(!!fixedToward)}>
                    <option value="" disabled>
                      What does it go toward?
                    </option>
                    {fixedLines.map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name}
                      </option>
                    ))}
                  </select>
                )}
                {hint('Money a friend sends. It counts toward what they send, not as your income.')}
              </>
            ))}
          {income === 'bonus' &&
            (!choices ? (
              <p className="px-1 text-sm text-ink-soft">Finding your bonuses…</p>
            ) : (
              <>
                <select aria-label="Which bonus" value={bonus} onChange={(e) => setBonusPick(e.target.value)} className={select(!!bonus)}>
                  <option value="" disabled>
                    Which bonus?
                  </option>
                  {bonuses.map((b) => (
                    <option key={b.id} value={b.id}>
                      {b.name} · {formatCents(b.amountCents)} · {formatDay(b.expectedDate)}
                    </option>
                  ))}
                  <option value={NEW}>+ New bonus…</option>
                </select>
                {bonus === NEW && (
                  <label className="block">
                    <span className="text-xs font-medium text-ink-soft">Bonus name</span>
                    <input
                      value={bonusName}
                      maxLength={100}
                      placeholder="e.g. Year-end bonus"
                      onChange={(e) => setBonusName(e.target.value)}
                      className="mt-1 block w-full rounded-2xl neu-inset px-4 py-3 text-base"
                    />
                  </label>
                )}
                {hint('It counts as that bonus, landed. It adds to this month’s money to spend.')}
              </>
            ))}
        </div>
      )}

      {/* A transfer (DECISIONS.md 259): the other account. Its other half there is filed with it. */}
      {tab === 'transfer' && (
        <div className="mt-3 space-y-3">
          {!choices ? (
            <p className="px-1 text-sm text-ink-soft">Finding your accounts…</p>
          ) : (
            <>
              <select
                aria-label={moneyIn ? 'Which account it came from' : 'Which account it went to'}
                value={other}
                onChange={(e) => {
                  setOtherPick(e.target.value)
                  setGoalPick('')
                }}
                className={select(!!other)}
              >
                <option value="" disabled>
                  {moneyIn ? 'Where did it come from?' : 'Where did it go?'}
                </option>
                {(['checking', 'savings', 'card'] as const).map((kind) => {
                  const list = accounts.filter((a) => a.type === kind)
                  return list.length === 0 ? null : (
                    <optgroup key={kind} label={kind === 'checking' ? 'Checking' : kind === 'savings' ? 'Savings' : 'Cards'}>
                      {list.map((a) => (
                        <option key={a.id} value={`acct:${a.id}`}>
                          {a.name}
                        </option>
                      ))}
                    </optgroup>
                  )
                })}
                {choices.loans.length > 0 && (
                  <optgroup label="Debts you track by hand">
                    {choices.loans.map((l) => (
                      <option key={l.id} value={`loan:${l.id}`}>
                        {l.name}
                      </option>
                    ))}
                  </optgroup>
                )}
                <option value={OUTSIDE}>An account not in Smooth Budget</option>
              </select>
              {goals.length > 1 && (
                <select aria-label="Which savings goal" value={goal} onChange={(e) => setGoalPick(e.target.value)} className={select(!!goal)}>
                  <option value="" disabled>
                    Which savings goal is it for?
                  </option>
                  {goals.map((g) => (
                    <option key={g.id} value={g.id}>
                      {g.name}
                    </option>
                  ))}
                </select>
              )}
              {hint(
                other === OUTSIDE
                  ? 'Not counted anywhere: not spending, not income.'
                  : otherLoan || otherAccount?.type === 'card'
                    ? moneyIn
                      ? 'Money between your accounts: not spending or income.'
                      : 'Counts as paying down that debt, not as spending.'
                    : otherAccount?.type === 'savings' && !moneyIn
                      ? 'Counts toward your savings, not as spending.'
                      : 'Money between your accounts: not spending or income.',
              )}
            </>
          )}
        </div>
      )}

      {/* Interest (DECISIONS.md 259): charged by a card or loan, or paid by a savings account. */}
      {tab === 'interest' && (
        <div className="mt-3">
          {!choices ? (
            <p className="px-1 text-sm text-ink-soft">Finding your accounts…</p>
          ) : (
            <>
              <select aria-label="Which account" value={interestOn} onChange={(e) => setInterestPick(e.target.value)} className={select(!!interestOn)}>
                <option value="" disabled>
                  {moneyIn ? 'Which account paid it?' : 'Which card or loan charged it?'}
                </option>
                {choices.debts.length > 0 && (
                  <optgroup label="Cards and loans">
                    {choices.debts.map((d) => (
                      <option key={d.id} value={`debt:${d.id}`}>
                        {d.name}
                      </option>
                    ))}
                  </optgroup>
                )}
                {savingsAccounts.length > 0 && (
                  <optgroup label="Savings">
                    {savingsAccounts.map((a) => (
                      <option key={a.id} value={`savings:${a.id}`}>
                        {a.name}
                      </option>
                    ))}
                  </optgroup>
                )}
              </select>
              {interestDebt
                ? hint('Goes to Card interest under Debt: planned in your budget, not spending. The Debt tab tracks it for that card or loan.')
                : interestSavings
                  ? hint('Interest your savings earned: not counted as income or spending.')
                  : null}
            </>
          )}
        </div>
      )}

      <button type="button" disabled={busy || !ready} onClick={() => void save()} className={`${reviewPrimary} mt-4`}>
        {busy ? 'Saving…' : 'Confirm'}
      </button>
      {/* Right under Confirm (DECISIONS.md 259): the others from this merchant you haven't confirmed, filed the same way. */}
      {tab !== 'transfer' && (
        <div className="mt-3 flex items-center justify-between gap-3 px-1">
          <span id={`apply-${t.id}`} className="min-w-0 text-xs text-ink-soft">
            Apply to all past transactions from {titleCase(t.merchant)}
          </span>
          <button
            type="button"
            role="switch"
            aria-checked={applyAll}
            aria-labelledby={`apply-${t.id}`}
            onClick={() => setApplyAll((x) => !x)}
            className={`relative h-6 w-10 shrink-0 rounded-full transition-colors ${applyAll ? 'bg-accent' : 'bg-ink/15'}`}
          >
            <span
              aria-hidden
              className={`absolute top-0.5 h-5 w-5 rounded-full shadow transition-all ${applyAll ? 'left-[1.125rem]' : 'left-0.5'}`}
              style={{ backgroundColor: '#fff' }}
            />
          </button>
        </div>
      )}
      {editing ? (
        <button type="button" onClick={onSkip} className={`${reviewSecondary} mt-3 w-full`}>
          Cancel
        </button>
      ) : (
        <div className="mt-3 flex justify-center">
          <button type="button" onClick={onSkip} className={`${smallBtn} text-ink-soft`}>
            Skip for now
          </button>
        </div>
      )}
    </article>
  )
}

const noop = () => {}

/**
 * A transaction's edit pop-up (from Home and Budget) is its Review card (DECISIONS.md 247): the same kinds, categories,
 * bills, debts and savings, with Cancel at the bottom. It replaced the old sheet and its "Transfer or card payment" box.
 */
export function TransactionEditSheet({
  transaction: t,
  categories: initial,
  onClose,
  onSaved,
}: {
  transaction: Transaction
  categories: Category[]
  onClose: () => void
  onSaved: (message: string) => void
}) {
  const [categories, setCategories] = useState(initial)
  const [error, setError] = useState<string | null>(null)
  const bills = useBills(noop, noop)
  return (
    <>
      <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
        <div
          role="dialog"
          aria-modal="true"
          aria-label={`Edit ${titleCase(t.merchant)}`}
          onClick={(e) => e.stopPropagation()}
          className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface px-3 pt-3 pb-[max(0.75rem,env(safe-area-inset-bottom))]"
        >
          {bills.notice && (
            <p role="status" className="mb-3 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
              {bills.notice}
            </p>
          )}
          {(error ?? bills.error) && (
            <p role="alert" className="mb-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
              {error ?? bills.error}
            </p>
          )}
          <ReviewCard
            t={t}
            categories={categories}
            billItems={bills.s?.items ?? []}
            onBillUpdate={(u, m) => void bills.update(u, m)}
            onBillOpen={(i, part) => bills.openSheet(i, part)}
            onTrackAsBill={(i) => void bills.trackAsBill(i)}
            editing
            onDone={(message) => onSaved(message)}
            onSkip={onClose}
            onCategoryCreated={async () => setCategories(await budgetApi.categories())}
            onError={(e) => setError(e instanceof Error ? e.message : 'Could not save.')}
          />
        </div>
      </div>
      {bills.sheet}
    </>
  )
}

/** What a transaction is: the switch at the top of its card (DECISIONS.md 259). */
type Tab = 'spending' | 'bill' | 'income' | 'transfer' | 'interest'
const allTabs: { id: Tab; label: string }[] = [
  { id: 'spending', label: 'Spending' },
  { id: 'bill', label: 'Bill' },
  { id: 'income', label: 'Income' },
  { id: 'transfer', label: 'Transfer' },
  { id: 'interest', label: 'Interest' },
]
type IncomeAs = 'paycheck' | 'friend' | 'bonus' | 'other'

const isCardInterest = (name: string | null) => name?.trim().toLowerCase() === 'card interest'

/** The switch's starting place: what the app has it as now (its most likely, until you confirm it). */
function initialTab(t: Transaction, categories: Category[]): Tab {
  if (t.kind === 'Interest' || isCardInterest(t.categoryName)) return 'interest'
  if (t.isTransfer) return 'transfer'
  if (t.amountCents < 0 && (t.kind === 'Income' || t.kind === 'PartnerContribution')) return 'income'
  if (t.isPartnerTransfer && t.amountCents < 0 && !t.suggestedCategoryId) return 'income'
  const type = categories.find((c) => c.id === (t.categoryId ?? t.suggestedCategoryId))?.type
  if (type === 'FixedBill') return 'bill'
  if (type === 'DebtPayment' || type === 'SavingsTransfer') return 'transfer'
  return 'spending'
}

/** The app's most likely answer, in words: what "Use most likely" keeps (DECISIONS.md 259). */
function mostLikely(t: Transaction, categories: Category[]): string {
  if (t.kind === 'Interest') return t.transferAccountName ? `Interest from ${t.transferAccountName}` : 'Interest'
  if (isCardInterest(t.categoryName)) return t.debtAccountName ? `Interest on ${t.debtAccountName}` : 'Card interest'
  if (t.isTransfer)
    return t.transferAccountName ? `Transfer ${t.amountCents > 0 ? 'to' : 'from'} ${t.transferAccountName}` : t.categoryName ?? 'Transfer'
  const friend = t.friendName ?? 'a friend'
  if (t.kind === 'PartnerTransfer') {
    const suggested = categories.find((c) => c.id === t.suggestedCategoryId)?.name
    return suggested ?? (t.amountCents < 0 ? `From ${friend}` : 'Other')
  }
  if (t.kind === 'PartnerContribution') return `From ${friend}`
  if (t.kind === 'Income') return t.incomeType === 'paycheck' ? 'Paycheck' : 'Other income'
  return t.categoryName ?? 'Other'
}


/** The dropdown's last choice: make a new one instead. */
const NEW = '__new'
/** A transfer to or from an account that isn't in Smooth Budget: counted nowhere. */
const OUTSIDE = '__outside'
/** A new bill's own new line, named after it. */
const OWN = '__own'

/** The app's most likely answer, as a question: "Groceries?" */
function StatusTag({ label }: { label: string }) {
  return <span className="inline-block truncate rounded-full bg-ink/[0.06] px-2.5 py-0.5 text-xs font-semibold text-gold-ink">{label}?</span>
}

function NewCategory({
  type,
  suggestion,
  onCancel,
  onCreated,
  onError,
}: {
  type: Category['type']
  suggestion: string
  onCancel: () => void
  onCreated: (id: string) => Promise<void>
  onError: (e: unknown) => void
}) {
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  // A bill gets its own line named after the merchant unless you type something else.
  const fallback = type === 'FixedBill' ? suggestion : ''
  const kind = type === 'FixedBill' ? 'bill' : type === 'DebtPayment' ? 'debt' : type === 'SavingsTransfer' ? 'savings' : 'spending'
  return (
    <div className="mt-3 rounded-xl neu-inset p-3">
      <label className="block">
        <span className="text-xs text-ink-soft">New {kind} category</span>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={fallback || 'e.g. Household'}
          maxLength={100}
          className="mt-1 block w-full rounded-xl neu-inset px-3 py-2 text-base"
        />
      </label>
      <div className="mt-3 flex gap-3">
        <button type="button" onClick={onCancel} className="flex-1 rounded-2xl neu py-2 text-sm font-medium text-danger">
          Cancel
        </button>
        <button
          type="button"
          disabled={busy || !(name.trim() || fallback)}
          onClick={async () => {
            setBusy(true)
            try {
              const r = await budgetEditApi.createCategory(name.trim() || fallback, type, 0)
              await onCreated(r.id)
            } catch (e) {
              onError(e)
              setBusy(false)
            }
          }}
          className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 text-sm font-semibold text-primary-ink disabled:opacity-40"
        >
          Add
        </button>
      </div>
    </div>
  )
}

function ListRow({ t, label, onOpen, onConfirm }: { t: Transaction; label: string; onOpen: () => void; onConfirm: () => void }) {
  return (
    <li className="flex items-center gap-2 pr-2">
      <button type="button" onClick={onOpen} className="min-w-0 flex-1 px-4 py-3 text-left">
        <div className="flex items-baseline justify-between gap-3">
          <span className="truncate text-sm font-medium">{titleCase(t.merchant)}</span>
          <span className={`shrink-0 text-sm tabular-nums ${t.amountCents < 0 ? 'text-good' : ''}`}>{formatSigned(t.amountCents)}</span>
        </div>
        <p className="mt-0.5 flex items-center gap-2 truncate text-xs text-ink-soft">
          {formatDay(t.date)} <StatusTag label={label} />
        </p>
      </button>
      <button
        type="button"
        onClick={onConfirm}
        aria-label={`Confirm ${label} for ${titleCase(t.merchant)}`}
        className="shrink-0 rounded-full neu-inset px-3 py-1.5 text-xs font-semibold text-good"
      >
        Confirm
      </button>
    </li>
  )
}

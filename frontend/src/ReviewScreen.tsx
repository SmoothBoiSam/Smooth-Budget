import { useCallback, useEffect, useMemo, useState } from 'react'
import { ask } from './confirm'
import {
  ApiError,
  budgetApi,
  budgetEditApi,
  recurringApi,
  type Category,
  type ChargeBillChoices,
  type TowardChoices,
  type ReviewScope,
  type ReviewSummary,
  type Transaction,
  type TransactionContext,
  type RecurringItem,
  type RecurringUpdate,
  listedType,
  needsLook,
} from './api'
import BillsSection, { Alert, BillCard, BillOnCard } from './BillsSection'
import { useBills } from './useBills'
import type { SheetPart } from './bills'
import { CardLink, ErrorNotice, Loading, Page, PageHeader, reviewCard, reviewPrimary, reviewSecondary, smallBtn, smallBtnOn } from './ui'
import { formatCents, formatDay, formatSigned, titleCase } from './format'

type Scope = 'todo' | 'earlier' | 'older'

const partFromHash = () => (window.location.hash.startsWith('#/tasks/bills') ? 'bills' : 'transactions')


/** Partner money first, then purchases with no category, then the app's least certain guesses, then likely ones. */
function rank(t: Transaction) {
  if (t.isPartnerTransfer) return 0
  if (!t.categoryId && t.kind !== 'PartnerContribution' && t.kind !== 'PartnerTransfer') return 1
  return t.unsure ? 2 : 3
}

/**
 * Review transactions and Review bills, under Tasks (DECISIONS.md 87, 206): one place to categorize and confirm everything the app sorted on its own.
 * Cards go one at a time with the app's guess up front; the list is for confirming the likely ones in bulk.
 * Nothing here is settled until the user says so (DECISIONS.md 80).
 */
export default function ReviewScreen({
  onChanged,
  onSignedOut,
  onBillsCount,
}: {
  onChanged: (count?: number) => void
  onSignedOut: () => void
  onBillsCount: (n: number) => void
}) {
  const [part, setPart] = useState<'transactions' | 'bills'>(partFromHash)
  const [scope, setScope] = useState<Scope>('todo')
  const [mode, setMode] = useState<'cards' | 'list'>('cards')
  const [queue, setQueue] = useState<Transaction[] | null>(null)
  const [summary, setSummary] = useState<ReviewSummary | null>(null)
  const [todoCount, setTodoCount] = useState<number | null>(null)
  const [categories, setCategories] = useState<Category[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Bills that need a look, in the same review (DECISIONS.md 245); skipped ones wait until you ask for them again.
  const bills = useBills(onSignedOut, onBillsCount, () => setNotice(null))
  const [skippedBills, setSkippedBills] = useState<string[]>([])

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


  // A link to #/tasks/bills (a bill line on the Budget tab, a notification) switches pages even while one is open.
  useEffect(() => {
    const onHash = () => setPart(partFromHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  /**
   * After one card: drop it locally (no jumpy reload), refresh the counts and the badge. A new merchant rule may have
   * filed other cards still waiting, so then the list is fetched again to show them with the rule's category.
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

  async function bulk(action: () => Promise<{ confirmed: number }>) {
    try {
      const r = await action()
      setNotice(`Confirmed ${r.confirmed} ${r.confirmed === 1 ? 'purchase' : 'purchases'}.`)
      onChanged()
      await Promise.all([loadQueue(scope), loadSide()])
    } catch (e) {
      fail(e)
    }
  }

  const counts = scope === 'todo' ? summary?.month : scope === 'earlier' ? summary?.earlier : summary?.older
  const likely = counts?.likely ?? 0
  const reviewScope: ReviewScope = scope === 'todo' ? 'month' : scope
  const current = queue?.[0]
  const billsWaiting = scope === 'todo' ? (bills.s?.items.filter(needsLook).filter((i) => !skippedBills.includes(i.key)) ?? []) : []
  // Earlier and Older only show up when something there is waiting (DECISIONS.md 149).
  const scopes = (['todo', 'earlier', 'older'] as const).filter(
    (x) => x === 'todo' || x === scope || (x === 'earlier' ? summary?.earlier.total : summary?.older.total),
  )

  return (
    <Page>
      {/* One review for transactions and the bills that need a look (DECISIONS.md 245); All bills is its own page under it. */}
      <PageHeader
        title={part === 'bills' ? 'All bills' : 'Review transactions'}
        back={part === 'bills' ? { href: '#/tasks/transactions', label: 'Review' } : { href: '#/tasks', label: 'Tasks' }}
      />
      {/* The view switch sits on a row of its own under the title (DECISIONS.md 201), on the left like the filters below it (212). */}
      {part === 'transactions' && (
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
      )}

      {part === 'bills' ? (
        <BillsSection onSignedOut={onSignedOut} onCount={onBillsCount} />
      ) : (
      <>
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
              ? `These months (since ${formatDay(summary.windowStart)}) shape your budget proposal. If you can’t remember one, the app’s best guess is fine: what matters is that each has some category.`
              : `Before ${formatDay(summary.windowStart)}: these don’t affect your budget. There’s no need to remember them.`}
          </p>
          {(counts?.total ?? 0) > 0 && (
            <button
              type="button"
              onClick={async () => {
                const n = counts!.total
                if (!(await ask(`Use the app’s best guess for all ${n}? Any without a guess go under “Other”. You can still change any of them later.`, { yes: 'Use best guesses' }))) return
                try {
                  const r = await budgetApi.bestGuessAll(reviewScope)
                  setNotice(`Settled ${r.settled} with the app’s best guess.`)
                  onChanged()
                  await Promise.all([loadQueue(scope), loadSide()])
                } catch (e) {
                  fail(e)
                }
              }}
              className="mt-2 rounded-full bg-primary shadow-neu-sm px-3 py-1.5 text-xs font-semibold text-primary-ink"
            >
              I can’t remember these: use best guesses ({counts!.total})
            </button>
          )}
        </div>
      )}

      {queue === null ? (
        <Loading />
      ) : queue.length === 0 && billsWaiting.length === 0 ? (
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
          {scope === 'todo' && skippedBills.length > 0 && (
            <button type="button" onClick={() => setSkippedBills([])} className={`${smallBtn} mt-4 ml-2 text-accent`}>
              Show {skippedBills.length} skipped {skippedBills.length === 1 ? 'bill' : 'bills'}
            </button>
          )}
        </div>
      ) : (
        <>
          <div className="mt-3 flex min-h-8 items-center justify-between gap-3">
            <p className="text-xs text-ink-soft">
              <span className="font-semibold">{queue.length + billsWaiting.length}</span> to go
              {billsWaiting.length > 0 && (
                <span className="text-ink-faint">
                  {' '}
                  · {billsWaiting.length} {billsWaiting.length === 1 ? 'bill' : 'bills'}
                </span>
              )}
              {counts && counts.unsure > 0 && <span className="text-ink-faint"> · {counts.unsure} the app wasn’t sure about</span>}
            </p>
            {likely > 1 && (
              <button
                type="button"
                onClick={() => void bulk(() => budgetApi.confirmLikely(reviewScope))}
                className={`${smallBtnOn} shrink-0`}
              >
                Confirm {likely} likely
              </button>
            )}
          </div>

          {/* Bills that need a look come first (DECISIONS.md 245), each as the bill review card it was. */}
          {mode === 'cards' && billsWaiting[0] && (
            <BillCard
              key={billsWaiting[0].key}
              item={billsWaiting[0]}
              onUpdate={(u, m) => void bills.update(u, m)}
              onOpen={(part = 'edit') => bills.openSheet(billsWaiting[0], part)}
              onTrackAsBill={(i) => void bills.trackAsBill(i)}
              onSkip={() => {
                setNotice(null)
                bills.setNotice(null)
                setSkippedBills((k) => [...k, billsWaiting[0].key])
              }}
            />
          )}
          {mode === 'list' && billsWaiting.length > 0 && (
            <section aria-labelledby="bills-h" className="mt-3">
              <h2 id="bills-h" className="px-1 text-xs font-semibold text-ink-soft">
                Bills that need a look
              </h2>
              <ul className="mt-2 space-y-2">
                {billsWaiting.map((i) => (
                  <li key={i.key}>
                    <Alert
                      item={i}
                      onUpdate={(u, m) => void bills.update(u, m)}
                      onOpen={() => bills.openSheet(i, 'edit')}
                      onTrackAsBill={(x) => void bills.trackAsBill(x)}
                      onNotABill={() => bills.openSheet(i, 'notBill')}
                    />
                  </li>
                ))}
              </ul>
            </section>
          )}

          {mode === 'cards' && !billsWaiting[0] && current && (
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

          {mode === 'list' && queue.length > 0 && (
            <ul className="mt-3 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
              {queue.slice(0, 200).map((t) => (
                <ListRow
                  key={t.id}
                  t={t}
                  onOpen={() => {
                    setQueue((q) => (q ? [t, ...q.filter((x) => x.id !== t.id)] : q))
                    setMode('cards')
                  }}
                  onConfirm={async () => {
                    try {
                      await budgetApi.confirmGuesses([t.id])
                      await done(t.id, `Confirmed ${t.categoryName ?? 'it'}.`)
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
      {/* Every bill, with what each costs (DECISIONS.md 245). */}
      <CardLink href="#/tasks/bills">All bills</CardLink>
      {bills.sheet}
      </>
      )}
    </Page>
  )
}

/**
 * One transaction to confirm or change, with the app's guess up front. Money between you and your partner has no
 * category until you give it one (DECISIONS.md 100): from her it's paying you back for something (that category,
 * like a refund) or her money; to her it's your share of something (that category, as spending).
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
  /** In a transaction's edit pop-up (DECISIONS.md 247): Cancel instead of "I don't remember" and "Skip for now" (onSkip cancels). */
  editing?: boolean
  onDone: (message: string, reload?: boolean) => void
  onSkip: () => void
  onCategoryCreated: () => Promise<void>
  onError: (e: unknown) => void
}) {
  const first = t.friendName ?? 'your friend' // which friend it's to or from (DECISIONS.md 211)
  const partnerIn = t.isPartnerTransfer && t.amountCents < 0
  const partnerOut = t.isPartnerTransfer && t.amountCents > 0
  const pick = t.categoryId ?? t.suggestedCategoryId
  const [mode, setMode] = useState<'payback' | 'contribution' | null>(
    !partnerIn ? null : t.kind === 'PartnerContribution' ? 'contribution' : t.suggestedCategoryId ? 'payback' : null,
  )
  const [categoryId, setCategoryId] = useState<string | null>(pick)
  // The kind you tapped, or else the picked category's kind (categories can arrive after the card does).
  const [pickedGroup, setGroup] = useState<Category['type'] | null>(null)
  const [rule, setRule] = useState(false)
  const [adding, setAdding] = useState(false)
  const [busy, setBusy] = useState(false)
  const [context, setContext] = useState<TransactionContext | null>(null)
  // A bill (DECISIONS.md 225): one of your bills ("bill:<key>"), one of your bill lines ("line:<id>"), or a new bill.
  const [choices, setChoices] = useState<ChargeBillChoices | null>(null)
  const [billPick, setBillPick] = useState('')
  const [billName, setBillName] = useState('')
  // A new bill's bill category: one you have, or OWN (a new one named after the bill).
  const [billCat, setBillCat] = useState(OWN)
  // Debt or savings money goes toward one of your debts or savings, not a category (DECISIONS.md 227).
  const [toward, setToward] = useState<TowardChoices | null>(null)
  const [towardPick, setTowardPick] = useState('')
  // Debt money is a payment toward a card or loan, or interest charged on one (DECISIONS.md 232).
  const [debtWhat, setDebtWhat] = useState<'payment' | 'interest' | null>(null)
  // Money between your own accounts with no category (a payment arriving on a card, say) isn't counted. Editing one asks
  // first whether it really is a transfer (DECISIONS.md 247); the old checkbox is gone.
  const transferOnly = editing && t.isTransfer && !t.categoryId && !t.isPartnerTransfer
  const [keepTransfer, setKeepTransfer] = useState(true)

  // A memory aid: how you filed this merchant before (DECISIONS.md 102).
  useEffect(() => {
    let live = true
    budgetApi.context(t.id).then((c) => live && setContext(c)).catch(() => undefined)
    return () => {
      live = false
    }
  }, [t.id])

  async function dontRemember() {
    setBusy(true)
    try {
      const r = await budgetApi.bestGuess(t.id)
      const guessed = t.categoryId ?? t.suggestedCategoryId
      onDone(
        !r.categoryName
          ? `Counted as money from ${first}.`
          : guessed
            ? `Kept the best guess: ${r.categoryName}.`
            : `Filed under ${r.categoryName}. You can change it any time.`,
      )
    } catch (e) {
      onError(e)
      setBusy(false)
    }
  }

  const selected = categories.find((c) => c.id === categoryId) ?? null
  const group = pickedGroup ?? (selected ? listedType(selected) : 'Variable')
  const needsCategory = partnerIn ? mode === 'payback' : true
  // Money between friends keeps the plain category picker: it's paying back or a share of something, not a bill.
  const asBill = needsCategory && group === 'FixedBill' && !t.isPartnerTransfer

  // Your bills, once you say it's a bill: the one it already belongs to picked, or a new bill when it's a repeating
  // charge that isn't one yet.
  useEffect(() => {
    if (!asBill || choices) return
    let live = true
    recurringApi
      .billChoices(t.id)
      .catch(() => ({ bills: [], lines: [], current: null, detected: null, suggested: null }) as ChargeBillChoices)
      .then((c) => {
        if (!live) return
        setChoices(c)
        // Already in a bill category but not one of your bills yet: a new bill, in that category.
        const line = t.categoryId && c.lines.some((l) => l.id === t.categoryId) ? t.categoryId : null
        setBillPick(c.current ? `bill:${c.current}` : line || c.detected || c.bills.length === 0 ? NEW : '')
        setBillCat(line ?? OWN)
        setBillName(c.suggested ?? titleCase(t.merchant))
      })
    return () => {
      live = false
    }
  }, [asBill, choices, t.id, t.categoryId, t.merchant])
  const towardKind = needsCategory && !t.isPartnerTransfer ? (group === 'DebtPayment' ? 'debt' : group === 'SavingsTransfer' ? 'savings' : null) : null
  const towardList = towardKind === 'debt' ? toward?.debts : towardKind === 'savings' ? toward?.savings : undefined
  useEffect(() => {
    if (!towardKind || toward) return
    let live = true
    recurringApi
      .towardChoices()
      .catch(() => ({ debts: [], savings: [] }) as TowardChoices)
      .then((c) => live && setToward(c))
    return () => {
      live = false
    }
  }, [towardKind, toward])
  // A charge on the card itself, or one worded like interest, is interest; money out of checking is a payment.
  const onDebt = toward?.debts.find((d) => d.accountId === t.accountId)
  const interestLike = t.amountCents > 0 && (!!onDebt || /interest|finance charge/i.test(`${t.merchant} ${t.description}`))
  const interest = towardKind === 'debt' && (debtWhat ?? (interestLike ? 'interest' : 'payment')) === 'interest'
  // With only one debt or one savings account, it's that one; interest starts on the card it was charged on.
  const pickedToward =
    towardList?.find((x) => x.id === towardPick) ?? (interest && onDebt ? onDebt : towardList?.length === 1 ? towardList[0] : undefined)

  // The account picker shows once the choices are in and there's at least one (else the category picker stays).
  const towardPicker = !!towardKind && (!toward || (!!towardList && towardList.length > 0))

  const pickedBill = choices?.bills.find((b) => `bill:${b.key}` === billPick)
  const billReady = billPick === NEW ? billName.trim().length > 0 : !!pickedBill
  // The bill picked, or (a new bill) the repeating charge it's part of.
  const billShown = billItems.find((i) => i.key === (pickedBill ? pickedBill.key : billPick === NEW ? choices?.detected : null))
  // Every category in the chosen group at once, in your budget's order (DECISIONS.md 145).
  const inGroup = useMemo(() => categories.filter((c) => listedType(c) === group).sort((a, b) => a.sortOrder - b.sortOrder), [categories, group])

  function chooseGroup(g: Category['type']) {
    setGroup(g)
    setAdding(false)
    const list = categories.filter((c) => listedType(c) === g)
    // Most people have one card payment or one savings line: pick it for them.
    // Card interest listed with the debt lines doesn't count: picking Debt still lands on your one card payment line.
    const own = list.filter((c) => c.type === g)
    setCategoryId(list.some((c) => c.id === categoryId) ? categoryId : own.length === 1 ? own[0].id : null)
  }

  async function save() {
    setBusy(true)
    try {
      if (partnerIn && mode === 'contribution') {
        await budgetApi.updateTransaction(t.id, null, false, null, { partnerPurpose: 'contribution' })
        return onDone(`Counted as money from ${first}.`)
      }
      if (asBill) {
        const r = await recurringApi.fileAsBill({
          transactionId: t.id,
          ...(pickedBill ? { billKey: pickedBill.key } : { name: billName.trim(), ...(billCat !== OWN ? { categoryId: billCat } : {}) }),
        })
        const moved = r.moved > 0 ? ` ${r.moved} other ${r.moved === 1 ? 'charge' : 'charges'} of this bill moved there too.` : ''
        if (billPick === NEW) await onCategoryCreated()
        return onDone(`${pickedBill ? pickedBill.name : `New bill: ${billName.trim()}, in ${r.categoryName}`}.${moved}`, true)
      }
      if (towardKind && towardList && towardList.length > 0) {
        if (!pickedToward) return
        const r = await recurringApi.fileToward(t.id, interest ? 'interest' : towardKind, pickedToward.id)
        return onDone(interest ? `Interest on ${r.towardName}, in ${r.categoryName}.` : `Toward ${r.towardName}.`)
      }
      if (!categoryId) return
      // A card payment or a move to savings stays a transfer (never spending) when it's filed as debt or savings.
      const stillTransfer = t.isTransfer && !!selected && (selected.type === 'DebtPayment' || selected.type === 'SavingsTransfer')
      const r = await budgetApi.updateTransaction(t.id, categoryId, stillTransfer, rule && !t.isPartnerTransfer && !stillTransfer ? t.merchant : null)
      const moved = r.billChargesMoved > 0 ? ` ${r.billChargesMoved} earlier ${r.billChargesMoved === 1 ? 'charge' : 'charges'} of this bill moved there too.` : ''
      const others = r.ruleAppliedTo > 0 ? ` ${r.ruleAppliedTo} more from ${titleCase(t.merchant)} ${r.ruleAppliedTo === 1 ? 'is' : 'are'} filed there too.` : ''
      onDone(
        partnerIn
          ? `Counted as paying you back for ${selected?.name}: it comes off that category.`
          : partnerOut
            ? `Counted as your share of ${selected?.name}.`
            : `${selected?.name ?? 'Saved'}${rule ? `. Future ${titleCase(t.merchant)} purchases go there too, for you to confirm` : ''}.${others}${moved}`,
        r.ruleAppliedTo > 0 || r.billChargesMoved > 0,
      )
    } catch (e) {
      onError(e)
      setBusy(false)
    }
  }

  // The bank's description, unless it only repeats the store name.
  const showDescription = !!t.description && t.description.trim().toUpperCase() !== t.merchant.trim().toUpperCase()
  // How you filed it before says the same thing in more detail, so the one-line reason steps aside for it.
  const reason = context && context.history.length > 0 ? null : t.categoryReason
  const notes = [
    showDescription ? t.description : null,
    reason,
    t.isPartnerTransfer && t.kind === 'PartnerTransfer' ? 'Not counted anywhere until you say what it was for.' : null,
    older ? 'Too old to affect your budget.' : null,
  ].filter((x): x is string => !!x)

  const header = (
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <h2 className="truncate text-base font-semibold">{titleCase(t.merchant)}</h2>
        <p className="mt-0.5 truncate text-xs text-ink-soft">
          {formatDay(t.date)} · {t.accountName}
        </p>
      </div>
      <p className={`shrink-0 text-lg font-semibold tabular-nums ${t.amountCents < 0 ? 'text-good' : ''}`}>{formatSigned(t.amountCents)}</p>
    </div>
  )

  if (transferOnly && keepTransfer)
    return (
      <article aria-label={`Edit ${titleCase(t.merchant)}`} className={reviewCard}>
        {header}
        <p className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-ink-soft">
          Money moved between your own accounts, like a payment arriving on a card, so it isn’t counted as spending or income.
        </p>
        <button type="button" onClick={() => setKeepTransfer(false)} className={`${reviewSecondary} mt-4 w-full`}>
          It’s not a transfer
        </button>
        <button type="button" onClick={onSkip} className={`${reviewSecondary} mt-3 w-full`}>
          Cancel
        </button>
      </article>
    )

  return (
    <article aria-label={`${editing ? 'Edit' : 'Review'} ${titleCase(t.merchant)}`} className={reviewCard}>
      {/* What it is, then one quiet line of why (DECISIONS.md 214). The guess shows as the picked category below. */}
      {header}
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

      {partnerIn && (
        <fieldset className="mt-4">
          <legend className="text-xs font-medium text-ink-soft">What was this from {first}?</legend>
          <div className="mt-2 grid gap-3">
            {(
              [
                ['payback', 'Paying you back for something', 'Pick what it was for below. It comes off that category, like a refund.'],
                ['contribution', `${first}’s contribution or just money`, `Counts as money from ${first}, not your income.`],
              ] as const
            ).map(([value, label, hint]) => (
              <label
                key={value}
                className={`flex cursor-pointer gap-3 rounded-xl px-4 py-3 ${mode === value ? 'bg-accent shadow-neu-sm text-accent-ink' : 'neu'}`}
              >
                <input type="radio" name={`purpose-${t.id}`} checked={mode === value} onChange={() => setMode(value)} className="sr-only" />
                <span>
                  <span className="block text-sm font-semibold">{label}</span>
                  <span className={`block text-xs ${mode === value ? 'text-accent-ink/85' : 'text-ink-soft'}`}>{hint}</span>
                </span>
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {needsCategory && (
        <>
          {/* The kind, as a switch like the view switch, then a dropdown of its categories (DECISIONS.md 145, 214). */}
          <fieldset className="mt-4">
            <legend className={partnerIn || partnerOut ? 'mb-2 text-xs font-medium text-ink-soft' : 'sr-only'}>
              {partnerIn ? 'It paid you back for' : partnerOut ? `Your share of what ${first} paid for` : 'What was it?'}
            </legend>
            <div className="flex rounded-full neu-inset p-1">
              {groups.map((g) => (
                <button
                  key={g.type}
                  type="button"
                  aria-pressed={group === g.type}
                  aria-label={g.label}
                  onClick={() => chooseGroup(g.type)}
                  className={`flex-1 rounded-full py-1.5 text-xs font-semibold ${group === g.type ? 'neu-sm text-accent' : 'text-ink-soft'}`}
                >
                  {g.short}
                </button>
              ))}
            </div>
          </fieldset>

          {/* A bill: one of your bills, or a new bill with its name and bill category (DECISIONS.md 225, 230). Its other charges follow it. */}
          {asBill && (
            <div className="mt-3">
              {!choices ? (
                <p className="px-1 text-sm text-ink-soft">Finding your bills…</p>
              ) : (
                <>
                  <select
                    aria-label="Which bill"
                    value={billPick}
                    onChange={(e) => setBillPick(e.target.value)}
                    className={`block w-full rounded-2xl neu-inset px-4 py-3 text-base ${billPick ? 'font-medium' : 'text-ink-soft'}`}
                  >
                    <option value="" disabled>
                      Pick a bill…
                    </option>
                    {choices.bills.map((b) => (
                      <option key={b.key} value={`bill:${b.key}`}>
                        {b.name} · {formatCents(b.amountCents)}
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
                      <label className="block">
                        <span className="text-xs font-medium text-ink-soft">Bill Category</span>
                        <select
                          value={billCat}
                          onChange={(e) => setBillCat(e.target.value)}
                          className="mt-1 block w-full rounded-2xl neu-inset px-4 py-3 text-base"
                        >
                          {choices.lines.map((l) => (
                            <option key={l.id} value={l.id}>
                              {l.name}
                            </option>
                          ))}
                          <option value={OWN}>+ Its own new bill category{billName.trim() ? ` (${billName.trim()})` : ''}</option>
                        </select>
                        <span className="mt-1 block text-xs text-ink-faint">Its earlier charges go there too.</span>
                      </label>
                    </div>
                  ) : (
                    pickedBill && <p className="mt-2 px-1 text-xs text-ink-faint">Its charges stay together on that bill.</p>
                  )}
                  {/* The bill it's going to, as the bill review card shows it: price, what needs a look, charges, more options. */}
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

          {/* Debt or savings: which one it went toward (DECISIONS.md 227). With none set up yet, the category picker stays. */}
          {towardPicker && (
            <div className="mt-3">
              {!toward ? (
                <p className="px-1 text-sm text-ink-soft">Finding your {towardKind === 'debt' ? 'debts' : 'savings'}…</p>
              ) : (
                <>
                  {towardKind === 'debt' && (
                    <div role="radiogroup" aria-label="What it was" className="mb-3 flex rounded-full neu-inset p-1">
                      {(['payment', 'interest'] as const).map((w) => (
                        <button
                          key={w}
                          type="button"
                          role="radio"
                          aria-checked={(interest ? 'interest' : 'payment') === w}
                          onClick={() => setDebtWhat(w)}
                          className={`flex-1 rounded-full py-1.5 text-xs font-semibold ${(interest ? 'interest' : 'payment') === w ? 'neu-sm text-accent' : 'text-ink-soft'}`}
                        >
                          {w === 'payment' ? 'A payment' : 'Interest charged'}
                        </button>
                      ))}
                    </div>
                  )}
                  <select
                    aria-label={towardKind === 'debt' ? (interest ? 'Which card or loan charged it' : 'Which debt') : 'Which savings goal'}
                    value={pickedToward?.id ?? ''}
                    onChange={(e) => setTowardPick(e.target.value)}
                    className={`block w-full rounded-2xl neu-inset px-4 py-3 text-base ${pickedToward ? 'font-medium' : 'text-ink-soft'}`}
                  >
                    <option value="" disabled>
                      {towardKind === 'debt' ? (interest ? 'Which card or loan charged it?' : 'Which debt did it pay?') : 'Which savings goal is it for?'}
                    </option>
                    {towardList!.map((x) => (
                      <option key={x.id} value={x.id}>
                        {x.name}
                      </option>
                    ))}
                  </select>
                  <p className="mt-2 px-1 text-xs text-ink-faint">
                    {towardKind === 'debt'
                      ? interest
                        ? 'Goes to Card interest under Debt: planned in your budget, not spending. The Debt tab tracks it for that card.'
                        : 'Counts as paying down that debt, not as spending.'
                      : 'Counts toward that goal, not as spending.'}
                  </p>
                </>
              )}
            </div>
          )}

          {/* One dropdown for the kind's categories, with "New category…" at the end (DECISIONS.md 214). */}
          {!asBill && !towardPicker && (
          <div className="mt-3">
            <select
              aria-label={groups.find((g) => g.type === group)!.pick}
              value={categoryId && inGroup.some((c) => c.id === categoryId) ? categoryId : ''}
              onChange={(e) => {
                if (e.target.value === NEW) setAdding(true)
                else {
                  setCategoryId(e.target.value)
                  setAdding(false)
                }
              }}
              className={`block w-full rounded-2xl neu-inset px-4 py-3 text-base ${categoryId && inGroup.some((c) => c.id === categoryId) ? 'font-medium' : 'text-ink-soft'}`}
            >
              <option value="" disabled>
                {groups.find((g) => g.type === group)!.pick}…
              </option>
              {inGroup.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
              <option value={NEW}>{group === 'Variable' ? '+ New spending category…' : '+ New category…'}</option>
            </select>
            {adding && (
              <NewCategory
                type={group}
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
          </div>
          )}
        </>
      )}

      {needsCategory && selected && !t.isPartnerTransfer && !asBill && !towardKind && (
        <label className="mt-4 flex cursor-pointer items-start gap-3">
          <input type="checkbox" checked={rule} onChange={(e) => setRule(e.target.checked)} className="mt-0.5 size-4 shrink-0" />
          <span className="text-sm">
            Always {selected.name} for {titleCase(t.merchant)}
            <span className="block text-xs text-ink-faint">Leave off for stores where it varies.</span>
          </span>
        </label>
      )}

      <button
        type="button"
        disabled={busy || (partnerIn && !mode) || (asBill ? !billReady : towardPicker ? !pickedToward : needsCategory && !categoryId)}
        onClick={() => void save()}
        className={`${reviewPrimary} mt-4`}
      >
        {busy
          ? 'Saving…'
          : partnerIn && !mode
            ? 'Say what it was for'
            : !needsCategory
              ? '✓ Confirm'
              : towardPicker
                ? pickedToward
                  ? interest
                    ? `✓ Interest on ${pickedToward.name}`
                    : `✓ Toward ${pickedToward.name}`
                  : towardKind === 'debt'
                    ? 'Pick a debt'
                    : 'Pick a goal'
                : asBill
                ? billPick === NEW
                  ? `✓ New bill${billName.trim() ? `: ${billName.trim()}` : ''}`
                  : pickedBill
                    ? `✓ ${pickedBill.name}${pickedBill.key === choices?.current ? ' is right' : ''}`
                    : 'Pick a bill'
              : selected
                ? `✓ ${selected.name}${categoryId === t.categoryId && !t.isPartnerTransfer ? ' is right' : ''}`
                : 'Pick a category'}
      </button>
      {editing ? (
        <button type="button" onClick={onSkip} className={`${reviewSecondary} mt-3 w-full`}>
          Cancel
        </button>
      ) : (
        <div className="mt-3 grid grid-cols-2 gap-3">
          <button type="button" onClick={() => void dontRemember()} disabled={busy} className={reviewSecondary}>
            I don’t remember
          </button>
          <button type="button" onClick={onSkip} className={reviewSecondary}>
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

/** The four kinds of transaction (DECISIONS.md 145); each one's categories show when you pick it. */
const groups: { type: Category['type']; label: string; short: string; pick: string }[] = [
  { type: 'Variable', label: 'Spending', short: 'Spending', pick: 'Pick a spending category' },
  { type: 'FixedBill', label: 'Bill', short: 'Bill', pick: 'Pick a bill' },
  { type: 'DebtPayment', label: 'Debt payment', short: 'Debt', pick: 'Pick a debt' },
  { type: 'SavingsTransfer', label: 'Savings transfer', short: 'Savings', pick: 'Pick a savings line' },
]
/** The dropdown's last choice: make a new category instead. */
const NEW = '__new'
/** A new bill's own new bill category, named after it. */
const OWN = '__own'

/** "No category" in red, or the app's guess as a question in orange: "Groceries?" */
function StatusTag({ t }: { t: Transaction }) {
  const name = t.categoryName ?? (t.kind === 'PartnerContribution' ? 'Money from your partner' : null)
  return name ? (
    <span className="inline-block rounded-full bg-ink/[0.06] px-2.5 py-0.5 text-xs font-semibold text-gold-ink">{name}?</span>
  ) : (
    <span className="inline-block rounded-full bg-ink/[0.06] px-2.5 py-0.5 text-xs font-semibold text-danger">No category</span>
  )
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
  const kind = groups.find((g) => g.type === type)!.label.toLowerCase()
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

function ListRow({ t, onOpen, onConfirm }: { t: Transaction; onOpen: () => void; onConfirm: () => void }) {
  const label = t.categoryName ?? (t.kind === 'PartnerContribution' ? 'Contribution' : null)
  return (
    <li className="flex items-center gap-2 pr-2">
      <button type="button" onClick={onOpen} className="min-w-0 flex-1 px-4 py-3 text-left">
        <div className="flex items-baseline justify-between gap-3">
          <span className="truncate text-sm font-medium">{titleCase(t.merchant)}</span>
          <span className={`shrink-0 text-sm tabular-nums ${t.amountCents < 0 ? 'text-good' : ''}`}>{formatSigned(t.amountCents)}</span>
        </div>
        <p className="mt-0.5 flex items-center gap-2 truncate text-xs text-ink-soft">
          {formatDay(t.date)} <StatusTag t={t} />
        </p>
      </button>
      {label && !t.isPartnerTransfer ? (
        <button
          type="button"
          onClick={onConfirm}
          aria-label={`${label} is right for ${titleCase(t.merchant)}`}
          className="shrink-0 rounded-full neu-inset px-3 py-1.5 text-xs font-semibold text-good"
        >
          ✓ Right
        </button>
      ) : (
        <button type="button" onClick={onOpen} className="shrink-0 rounded-full neu-sm px-3 py-1.5 text-xs font-semibold text-ink-soft">
          Sort
        </button>
      )}
    </li>
  )
}

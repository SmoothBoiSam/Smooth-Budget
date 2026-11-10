import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  ApiError,
  budgetApi,
  budgetEditApi,
  type Category,
  type ReviewScope,
  type ReviewSummary,
  type Transaction,
  type TransactionContext,
  listedType,
} from './api'
import BillsSection from './BillsSection'
import { CountBubble, ErrorNotice, Loading, Page, PageHeader, reviewCard, reviewPrimary, reviewSecondary, smallBtn, smallBtnOn } from './ui'
import { formatDay, formatSigned, titleCase } from './format'

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
  billsCount,
  onBillsCount,
}: {
  onChanged: (count?: number) => void
  onSignedOut: () => void
  billsCount: number
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
  // Earlier and Older only show up when something there is waiting (DECISIONS.md 149).
  const scopes = (['todo', 'earlier', 'older'] as const).filter(
    (x) => x === 'todo' || x === scope || (x === 'earlier' ? summary?.earlier.total : summary?.older.total),
  )

  return (
    <Page>
      {/* Two pages under Tasks (DECISIONS.md 206): transactions to confirm, and bills the app found or that changed. */}
      <PageHeader
        title={part === 'bills' ? 'Review bills' : 'Review transactions'}
        back={{ href: '#/tasks', label: 'Tasks' }}
        action={(part === 'bills' ? billsCount : (todoCount ?? 0)) > 0 ? <CountBubble n={part === 'bills' ? billsCount : (todoCount ?? 0)} /> : undefined}
      />
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

      {part === 'bills' ? (
        <BillsSection onSignedOut={onSignedOut} onCount={onBillsCount} mode={mode} />
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

      {notice && (
        <p role="status" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => window.location.reload()} />}

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
                if (!window.confirm(`Use the app’s best guess for all ${n}? Any without a guess go under “Other”. You can still change any of them later.`)) return
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

          {mode === 'cards' && current && (
            <ReviewCard
              key={current.id}
              t={current}
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
  older,
  onDone,
  onSkip,
  onCategoryCreated,
  onError,
}: {
  t: Transaction
  categories: Category[]
  /** Before the months the budget is built from. */
  older?: boolean
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
      if (!categoryId) return
      const r = await budgetApi.updateTransaction(t.id, categoryId, false, rule && !t.isPartnerTransfer ? t.merchant : null)
      const moved = r.billChargesMoved > 0 ? ` ${r.billChargesMoved} earlier ${r.billChargesMoved === 1 ? 'charge' : 'charges'} of this bill moved there too.` : ''
      const others = r.ruleAppliedTo > 0 ? ` ${r.ruleAppliedTo} more from ${titleCase(t.merchant)} ${r.ruleAppliedTo === 1 ? 'is' : 'are'} filed there now, waiting for your ✓.` : ''
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

  return (
    <article aria-label={`Review ${titleCase(t.merchant)}`} className={reviewCard}>
      {/* What it is, then one quiet line of why (DECISIONS.md 214). The guess shows as the picked category below. */}
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-base font-semibold">{titleCase(t.merchant)}</h2>
          <p className="mt-0.5 truncate text-xs text-ink-soft">
            {formatDay(t.date)} · {t.accountName}
          </p>
        </div>
        <p className={`shrink-0 text-lg font-semibold tabular-nums ${t.amountCents < 0 ? 'text-good' : ''}`}>{formatSigned(t.amountCents)}</p>
      </div>
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

          {/* One dropdown for the kind's categories, with "New category…" at the end (DECISIONS.md 214). */}
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
              <option value={NEW}>+ New category…</option>
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
        </>
      )}

      {needsCategory && selected && !t.isPartnerTransfer && (
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
        disabled={busy || (partnerIn && !mode) || (needsCategory && !categoryId)}
        onClick={() => void save()}
        className={`${reviewPrimary} mt-4`}
      >
        {busy
          ? 'Saving…'
          : partnerIn && !mode
            ? 'Say what it was for'
            : !needsCategory
              ? '✓ Confirm'
              : selected
                ? `✓ ${selected.name}${categoryId === t.categoryId && !t.isPartnerTransfer ? ' is right' : ''}`
                : 'Pick a category'}
      </button>
      <div className="mt-3 grid grid-cols-2 gap-3">
        <button type="button" onClick={() => void dontRemember()} disabled={busy} className={reviewSecondary}>
          I don’t remember
        </button>
        <button type="button" onClick={onSkip} className={reviewSecondary}>
          Skip for now
        </button>
      </div>
    </article>
  )
}

/** The four kinds of transaction (DECISIONS.md 145); each one's categories show when you pick it. */
const groups: { type: Category['type']; label: string; short: string; pick: string }[] = [
  { type: 'Variable', label: 'Spending', short: 'Spending', pick: 'Pick a category' },
  { type: 'FixedBill', label: 'Bill', short: 'Bill', pick: 'Pick a bill' },
  { type: 'DebtPayment', label: 'Debt payment', short: 'Debt', pick: 'Pick a debt' },
  { type: 'SavingsTransfer', label: 'Savings transfer', short: 'Savings', pick: 'Pick a savings line' },
]
/** The dropdown's last choice: make a new category instead. */
const NEW = '__new'

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

import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  ApiError,
  budgetApi,
  budgetEditApi,
  partnerApi,
  type Category,
  type ReviewScope,
  type ReviewSummary,
  type Transaction,
  type TransactionContext,
} from './api'
import BillsSection from './BillsSection'
import { ErrorNotice, Loading, Page, PageHeader } from './ui'
import { formatDay, formatSigned, titleCase } from './format'

type Scope = 'todo' | 'earlier' | 'older'

const partFromHash = () => (window.location.hash.startsWith('#/review/bills') ? 'bills' : 'transactions')


/** Partner money first, then purchases with no category, then the app's least certain guesses, then likely ones. */
function rank(t: Transaction) {
  if (t.isPartnerTransfer) return 0
  if (!t.categoryId && t.kind !== 'PartnerContribution' && t.kind !== 'PartnerTransfer') return 1
  return t.unsure ? 2 : 3
}

/**
 * The Review tab (DECISIONS.md 87): one place to categorize and confirm everything the app sorted on its own.
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
  const [partnerName, setPartnerName] = useState<string | null>(null)
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

  useEffect(() => {
    partnerApi
      .get()
      .then((p) => setPartnerName(p.partnerName))
      .catch(fail)
  }, [fail])

  // A link to #/review/bills (a bill line on the Budget tab, a notification) switches parts even while Review is open.
  useEffect(() => {
    const onHash = () => setPart(partFromHash())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  function goTo(p: 'transactions' | 'bills') {
    setPart(p)
    window.history.replaceState(null, '', p === 'bills' ? '#/review/bills' : '#/review')
  }

  /** After one card: drop it locally (no jumpy reload), refresh the counts and the badge. */
  async function done(id: string, message: string) {
    setNotice(message)
    setError(null)
    setQueue((q) => q?.filter((t) => t.id !== id) ?? null)
    try {
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
      {/* The view switch sits beside the title so the scope chips get a row of their own and always fit (DECISIONS.md 167). It applies to both parts. */}
      <PageHeader
        title="Review"
        action={
        <div role="tablist" aria-label="View" className="flex shrink-0 rounded-full neu-inset p-1 text-xs font-medium">
          {(['cards', 'list'] as const).map((m) => (
            <button
              key={m}
              role="tab"
              aria-selected={mode === m}
              type="button"
              onClick={() => setMode(m)}
              className={`rounded-full px-3 py-1 ${mode === m ? 'neu-sm text-ink' : 'text-ink-soft'}`}
            >
              {m === 'cards' ? 'One by one' : 'List'}
            </button>
          ))}
        </div>
        }
      />
      {/* Two parts (DECISIONS.md 152): transactions to confirm, and bills the app found or that changed. */}
      <div role="tablist" aria-label="What to review" className="mt-2 grid grid-cols-2 gap-1 rounded-2xl neu-inset p-1 text-sm font-semibold">
        {(['transactions', 'bills'] as const).map((p) => {
          const n = p === 'transactions' ? todoCount : billsCount
          return (
            <button
              key={p}
              role="tab"
              type="button"
              aria-selected={part === p}
              onClick={() => goTo(p)}
              className={`flex items-center justify-center gap-1.5 rounded-lg py-1.5 ${part === p ? 'neu-sm text-ink' : 'text-ink-soft'}`}
            >
              {p === 'transactions' ? 'Transactions' : 'Bills'}
              {n != null && n > 0 && (
                <span className="inline-flex min-w-5 items-center justify-center rounded-full px-1.5 text-[11px] leading-5" style={{ backgroundColor: '#DC2626', color: '#fff' }}>
                  {n > 99 ? '99+' : n}
                </span>
              )}
            </button>
          )
        })}
      </div>

      {part === 'bills' ? (
        <BillsSection onSignedOut={onSignedOut} onCount={onBillsCount} mode={mode} />
      ) : (
      <>
      {/* Compact so a whole review card fits on a phone screen without scrolling (DECISIONS.md 161). */}
      {scopes.length > 1 && (
          <div className="mt-3 flex flex-wrap gap-1.5" role="tablist" aria-label="Which transactions">
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
                  className={`shrink-0 rounded-full px-2.5 py-1 text-xs font-medium ${scope === s ? 'bg-primary shadow-neu-sm text-primary-ink' : 'neu text-ink-soft'}`}
                >
                  {s === 'todo' ? 'This month' : s === 'earlier' ? 'Earlier' : 'Older'}
                  {n != null && n > 0 && <span className="ml-1 opacity-70">{n}</span>}
                </button>
              )
            })}
          </div>
      )}

      {notice && (
        <p role="status" className="mt-2 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => window.location.reload()} />}

      {scope !== 'todo' && summary && (
        <div className="mt-2 rounded-2xl neu px-3 py-2 text-xs">
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
        <div className="mt-8 rounded-3xl neu px-5 py-8 text-center">
          <p className="text-lg font-semibold">All caught up</p>
          <p className="mt-1 text-sm text-ink-soft">
            {scope === 'todo' ? 'Everything this month has a category you confirmed.' : scope === 'earlier' ? 'Nothing from these months is waiting.' : 'Nothing older is waiting.'}
          </p>
          {scope === 'todo' && (summary?.earlier.total ?? 0) > 0 && (
            <button type="button" onClick={() => setScope('earlier')} className="mt-4 text-sm font-medium text-ink-soft underline">
              Review {summary!.earlier.total} from earlier months
            </button>
          )}
        </div>
      ) : (
        <>
          <div className="mt-2 flex items-baseline justify-between gap-3">
            <p className="text-xs text-ink-soft">
              <span className="font-semibold">{queue.length}</span> to go
              {counts && counts.unsure > 0 && <span className="text-ink-faint"> · {counts.unsure} the app wasn’t sure about</span>}
            </p>
            {likely > 1 && (
              <button
                type="button"
                onClick={() => void bulk(() => budgetApi.confirmLikely(reviewScope))}
                className="shrink-0 text-xs font-medium text-ink-soft underline"
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
              partnerName={partnerName}
              onDone={(message) => void done(current.id, message)}
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
  partnerName,
  older,
  onDone,
  onSkip,
  onCategoryCreated,
  onError,
}: {
  t: Transaction
  categories: Category[]
  partnerName: string | null
  /** Before the months the budget is built from. */
  older?: boolean
  onDone: (message: string) => void
  onSkip: () => void
  onCategoryCreated: () => Promise<void>
  onError: (e: unknown) => void
}) {
  const first = partnerName ? titleCase(partnerName.split(' ')[0]) : 'your partner'
  const partnerIn = t.isPartnerTransfer && t.amountCents < 0
  const partnerOut = t.isPartnerTransfer && t.amountCents > 0
  const pick = t.categoryId ?? t.suggestedCategoryId
  const [mode, setMode] = useState<'payback' | 'contribution' | null>(
    !partnerIn ? null : t.kind === 'PartnerContribution' ? 'contribution' : t.suggestedCategoryId ? 'payback' : null,
  )
  const [categoryId, setCategoryId] = useState<string | null>(pick)
  const [group, setGroup] = useState<Category['type']>(categories.find((c) => c.id === pick)?.type ?? 'Variable')
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
  const needsCategory = partnerIn ? mode === 'payback' : true
  // Every category in the chosen group at once, in your budget's order (DECISIONS.md 145).
  const inGroup = useMemo(() => categories.filter((c) => c.type === group).sort((a, b) => a.sortOrder - b.sortOrder), [categories, group])

  function chooseGroup(g: Category['type']) {
    setGroup(g)
    setAdding(false)
    const list = categories.filter((c) => c.type === g)
    // Most people have one card payment or one savings line: pick it for them.
    setCategoryId(list.some((c) => c.id === categoryId) ? categoryId : list.length === 1 ? list[0].id : null)
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
      onDone(
        partnerIn
          ? `Counted as paying you back for ${selected?.name}: it comes off that category.`
          : partnerOut
            ? `Counted as your share of ${selected?.name}.`
            : `${selected?.name ?? 'Saved'}${rule ? `. Future ${titleCase(t.merchant)} purchases go there too, for you to confirm` : ''}.${moved}`,
      )
    } catch (e) {
      onError(e)
      setBusy(false)
    }
  }

  // The bank's description, unless it only repeats the store name.
  const showDescription = !!t.description && t.description.trim().toUpperCase() !== t.merchant.trim().toUpperCase()
  // The history box below says the same thing in more detail, so the one-line reason steps aside for it.
  const reason = context && context.history.length > 0 ? null : t.categoryReason

  return (
    <article aria-label={`Review ${titleCase(t.merchant)}`} className="animate-rise-in mt-2 rounded-3xl neu p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-base font-semibold">{titleCase(t.merchant)}</h2>
          <p className="truncate text-xs text-ink-soft">
            {formatDay(t.date)} · {t.accountName}
          </p>
        </div>
        <div className="shrink-0 text-right">
          <p className={`text-lg font-semibold tabular-nums ${t.amountCents < 0 ? 'text-good' : ''}`}>{formatSigned(t.amountCents)}</p>
          <StatusTag t={t} />
        </div>
      </div>
      {(showDescription || reason) && (
        <p className="mt-1.5 break-words text-xs text-ink-soft">
          {showDescription && <span className="text-ink-faint">{t.description}</span>}
          {showDescription && reason && ' · '}
          {reason}
        </p>
      )}
      {t.isPartnerTransfer && t.kind === 'PartnerTransfer' && (
        <p className="mt-1.5 text-xs text-ink-soft">Not counted anywhere until you say what it was for.</p>
      )}
      {older && <p className="mt-1.5 text-xs text-ink-soft">Too old to affect your budget.</p>}
      {context && context.history.length > 0 && (
        <p className="mt-2 rounded-lg neu-inset px-3 py-1.5 text-xs text-ink-soft">
          You’ve filed {titleCase(t.merchant)} as{' '}
          {context.history.map((h, i) => (
            <span key={h.categoryName}>
              {i > 0 && (i === context.history.length - 1 ? ' and ' : ', ')}
              <span className="font-medium text-ink-soft">{h.categoryName}</span> {h.count === 1 ? 'once' : `${h.count} times`}
            </span>
          ))}
          .
        </p>
      )}

      {partnerIn && (
        <fieldset className="mt-3">
          <legend className="text-sm font-medium text-ink-soft">What was this from {first}?</legend>
          <div className="mt-2 grid gap-2">
            {(
              [
                ['payback', 'Paying you back for something', 'Pick what it was for below. It comes off that category, like a refund.'],
                ['contribution', `${first}’s contribution or just money`, `Counts as money from ${first}, not your income.`],
              ] as const
            ).map(([value, label, hint]) => (
              <label
                key={value}
                className={`flex cursor-pointer gap-3 rounded-xl px-4 py-3 ${mode === value ? 'bg-primary shadow-neu-sm text-primary-ink' : 'neu'}`}
              >
                <input type="radio" name={`purpose-${t.id}`} checked={mode === value} onChange={() => setMode(value)} className="sr-only" />
                <span>
                  <span className="block text-sm font-semibold">{label}</span>
                  <span className={`block text-xs ${mode === value ? 'text-ink-faint' : 'text-ink-soft'}`}>{hint}</span>
                </span>
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {needsCategory && (
        <>
          <fieldset className="mt-3">
            <legend className="text-xs font-medium text-ink-soft">
              {partnerIn ? 'It paid you back for' : partnerOut ? `Your share of what ${first} paid for` : 'What was it?'}
            </legend>
            {/* One row of four, so the categories start higher up. */}
            <div className="mt-1.5 grid grid-cols-4 gap-1.5">
              {groups.map((g) => (
                <button
                  key={g.type}
                  type="button"
                  aria-pressed={group === g.type}
                  aria-label={g.label}
                  onClick={() => chooseGroup(g.type)}
                  className={`rounded-lg px-1 py-2 text-xs font-semibold ${group === g.type ? 'bg-primary shadow-neu-sm text-primary-ink' : 'neu text-ink-soft'}`}
                >
                  {g.short}
                </button>
              ))}
            </div>
          </fieldset>

          <fieldset className="mt-3">
            <legend className="text-xs font-medium text-ink-soft">{groups.find((g) => g.type === group)!.pick}</legend>
            <div className="mt-1.5 flex flex-wrap gap-1.5">
              {inGroup.map((c) => (
                <button
                  key={c.id}
                  type="button"
                  aria-pressed={c.id === categoryId}
                  onClick={() => setCategoryId(c.id)}
                  className={`rounded-full px-3 py-1 text-sm ${c.id === categoryId ? 'bg-primary shadow-neu-sm font-semibold text-primary-ink' : 'neu text-ink-soft'}`}
                >
                  {c.name}
                </button>
              ))}
              <button
                type="button"
                onClick={() => setAdding(true)}
                className="neu-sm rounded-full px-3 py-1 text-sm font-semibold text-primary"
              >
                + New category
              </button>
            </div>
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
          </fieldset>
        </>
      )}

      {needsCategory && selected && !t.isPartnerTransfer && (
        <label className="mt-3 flex items-center gap-2.5 rounded-lg neu-inset px-3 py-2">
          <input type="checkbox" checked={rule} onChange={(e) => setRule(e.target.checked)} className="size-4 shrink-0" />
          <span className="text-xs">
            Always {selected.name} for {titleCase(t.merchant)}
            <span className="text-ink-soft"> (leave off where it varies, like a supercenter)</span>
          </span>
        </label>
      )}

      <button
        type="button"
        disabled={busy || (partnerIn && !mode) || (needsCategory && !categoryId)}
        onClick={() => void save()}
        className="mt-3 w-full rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-40 text-sm"
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
      <div className="mt-2 grid grid-cols-2 gap-2">
        <button type="button" onClick={() => void dontRemember()} disabled={busy} className="rounded-2xl neu py-2 text-sm font-medium text-ink-soft">
          I don’t remember
        </button>
        <button type="button" onClick={onSkip} className="rounded-2xl neu py-2 text-sm font-medium text-ink-soft">
          Skip for now
        </button>
      </div>
    </article>
  )
}

/** The four kinds of transaction (DECISIONS.md 145); each one's categories show when you pick it. */
const groups: { type: Category['type']; label: string; short: string; pick: string }[] = [
  { type: 'Variable', label: 'Spending', short: 'Spending', pick: 'Spending category' },
  { type: 'FixedBill', label: 'Bill', short: 'Bill', pick: 'Which bill' },
  { type: 'DebtPayment', label: 'Debt payment', short: 'Debt', pick: 'Which debt' },
  { type: 'SavingsTransfer', label: 'Savings transfer', short: 'Savings', pick: 'Which savings' },
]

/** "No category" in red, or the app's guess as a question in orange: "Groceries?" */
function StatusTag({ t }: { t: Transaction }) {
  const name = t.categoryName ?? (t.kind === 'PartnerContribution' ? 'Money from your partner' : null)
  return name ? (
    <span className="inline-block rounded-full neu-inset px-2.5 py-0.5 text-xs font-semibold text-gold-ink">{name}?</span>
  ) : (
    <span className="inline-block rounded-full neu-inset px-2.5 py-0.5 text-xs font-semibold text-danger">No category</span>
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
      <div className="mt-3 flex gap-2">
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

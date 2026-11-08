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
import { ErrorNotice, Loading } from './ui'
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
    <div className="mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">
      <h1 className="text-2xl font-bold tracking-tight">Review</h1>
      {/* Two parts (DECISIONS.md 152): transactions to confirm, and bills the app found or that changed. */}
      <div role="tablist" aria-label="What to review" className="mt-3 grid grid-cols-2 gap-1 rounded-xl bg-slate-200/70 p-1 text-sm font-semibold">
        {(['transactions', 'bills'] as const).map((p) => {
          const n = p === 'transactions' ? todoCount : billsCount
          return (
            <button
              key={p}
              role="tab"
              type="button"
              aria-selected={part === p}
              onClick={() => goTo(p)}
              className={`flex items-center justify-center gap-1.5 rounded-lg py-2 ${part === p ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-600'}`}
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
        <BillsSection onSignedOut={onSignedOut} onCount={onBillsCount} />
      ) : (
      <>
      <header className="mt-4 flex items-baseline justify-between gap-3">
        <p className="text-sm text-slate-500">
          New purchases are sorted for you right away. Confirm each one or change it.{' '}
          <a href="#/budget/plan" className="font-medium text-slate-700 underline">
            Find categories from my history
          </a>
        </p>
        <div role="tablist" aria-label="View" className="flex shrink-0 rounded-full bg-slate-200/70 p-0.5 text-xs font-medium">
          {(['cards', 'list'] as const).map((m) => (
            <button
              key={m}
              role="tab"
              aria-selected={mode === m}
              type="button"
              onClick={() => setMode(m)}
              className={`rounded-full px-3 py-1 ${mode === m ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-600'}`}
            >
              {m === 'cards' ? 'One by one' : 'List'}
            </button>
          ))}
        </div>
      </header>

      {scopes.length > 1 && (
      <div className="mt-4 flex gap-2" role="tablist" aria-label="Which transactions">
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
              className={`rounded-full px-3 py-1.5 text-sm font-medium ring-1 ${scope === s ? 'bg-slate-900 text-white ring-slate-900' : 'bg-white text-slate-700 ring-slate-200'}`}
            >
              {s === 'todo' ? 'This month' : s === 'earlier' ? 'Earlier' : 'Older'}
              {n != null && n > 0 && <span className="ml-1.5 opacity-70">{n}</span>}
            </button>
          )
        })}
      </div>
      )}


      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => window.location.reload()} />}

      {scope !== 'todo' && summary && (
        <div className="mt-4 rounded-xl bg-white px-4 py-3 text-sm ring-1 ring-slate-200">
          <p className="text-slate-600">
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
              className="mt-2 rounded-full bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white"
            >
              I can’t remember these: use best guesses ({counts!.total})
            </button>
          )}
        </div>
      )}

      {queue === null ? (
        <Loading />
      ) : queue.length === 0 ? (
        <div className="mt-8 rounded-2xl bg-white px-5 py-8 text-center ring-1 ring-slate-200">
          <p className="text-lg font-semibold">All caught up</p>
          <p className="mt-1 text-sm text-slate-500">
            {scope === 'todo' ? 'Everything this month has a category you confirmed.' : scope === 'earlier' ? 'Nothing from these months is waiting.' : 'Nothing older is waiting.'}
          </p>
          {scope === 'todo' && (summary?.earlier.total ?? 0) > 0 && (
            <button type="button" onClick={() => setScope('earlier')} className="mt-4 text-sm font-medium text-slate-700 underline">
              Review {summary!.earlier.total} from earlier months
            </button>
          )}
        </div>
      ) : (
        <>
          <div className="mt-5 flex items-baseline justify-between gap-3">
            <p className="text-sm text-slate-600">
              <span className="font-semibold">{queue.length}</span> to go
              {counts && counts.unsure > 0 && <span className="text-slate-400"> · {counts.unsure} the app wasn’t sure about</span>}
            </p>
            {likely > 1 && (
              <button
                type="button"
                onClick={() => void bulk(() => budgetApi.confirmLikely(reviewScope))}
                className="shrink-0 text-xs font-medium text-slate-700 underline"
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
            <ul className="mt-3 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
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
            <p className="mt-2 text-center text-xs text-slate-400">Showing the first 200. Confirm some to see the rest.</p>
          )}
        </>
      )}
      </>
      )}
    </div>
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
      await budgetApi.updateTransaction(t.id, categoryId, false, rule && !t.isPartnerTransfer ? t.merchant : null)
      onDone(
        partnerIn
          ? `Counted as paying you back for ${selected?.name}: it comes off that category.`
          : partnerOut
            ? `Counted as your share of ${selected?.name}.`
            : `${selected?.name ?? 'Saved'}${rule ? `. Future ${titleCase(t.merchant)} purchases go there too, for you to confirm` : ''}.`,
      )
    } catch (e) {
      onError(e)
      setBusy(false)
    }
  }

  return (
    <article aria-label={`Review ${titleCase(t.merchant)}`} className="mt-3 rounded-2xl bg-white p-5 shadow-sm ring-1 ring-slate-200">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-lg font-semibold">{titleCase(t.merchant)}</h2>
          <p className="text-sm text-slate-500">
            {formatDay(t.date)} · {t.accountName}
          </p>
        </div>
        <p className={`text-xl font-semibold tabular-nums ${t.amountCents < 0 ? 'text-emerald-600' : ''}`}>{formatSigned(t.amountCents)}</p>
      </div>
      <div className="mt-2">
        <StatusTag t={t} />
      </div>
      {t.description !== t.merchant && <p className="mt-2 break-words text-xs text-slate-400">{t.description}</p>}
      {t.categoryReason && <p className="mt-2 text-xs text-slate-500">{t.categoryReason}</p>}
      {t.isPartnerTransfer && t.kind === 'PartnerTransfer' && (
        <p className="mt-2 text-xs text-slate-500">Not counted anywhere until you say what it was for.</p>
      )}
      {older && <p className="mt-2 text-xs text-slate-500">This is older than the months your budget is built from, so it doesn’t affect your budget.</p>}
      {context && context.history.length > 0 && (
        <p className="mt-3 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600">
          You’ve filed {titleCase(t.merchant)} as{' '}
          {context.history.map((h, i) => (
            <span key={h.categoryName}>
              {i > 0 && (i === context.history.length - 1 ? ' and ' : ', ')}
              <span className="font-medium text-slate-700">{h.categoryName}</span> {h.count === 1 ? 'once' : `${h.count} times`}
            </span>
          ))}
          .
        </p>
      )}

      {partnerIn && (
        <fieldset className="mt-4">
          <legend className="text-sm font-medium text-slate-700">What was this from {first}?</legend>
          <div className="mt-2 grid gap-2">
            {(
              [
                ['payback', 'Paying you back for something', 'Pick what it was for below. It comes off that category, like a refund.'],
                ['contribution', `${first}’s contribution or just money`, `Counts as money from ${first}, not your income.`],
              ] as const
            ).map(([value, label, hint]) => (
              <label
                key={value}
                className={`flex cursor-pointer gap-3 rounded-xl px-4 py-3 ring-1 ${mode === value ? 'bg-slate-900 text-white ring-slate-900' : 'bg-white ring-slate-200'}`}
              >
                <input type="radio" name={`purpose-${t.id}`} checked={mode === value} onChange={() => setMode(value)} className="sr-only" />
                <span>
                  <span className="block text-sm font-semibold">{label}</span>
                  <span className={`block text-xs ${mode === value ? 'text-slate-300' : 'text-slate-500'}`}>{hint}</span>
                </span>
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {needsCategory && (
        <>
          <fieldset className="mt-4">
            <legend className="text-sm font-medium text-slate-700">
              {partnerIn ? 'It paid you back for' : partnerOut ? `Your share of what ${first} paid for` : 'What was it?'}
            </legend>
            <div className="mt-2 grid grid-cols-2 gap-2">
              {groups.map((g) => (
                <button
                  key={g.type}
                  type="button"
                  aria-pressed={group === g.type}
                  onClick={() => chooseGroup(g.type)}
                  className={`rounded-xl px-3 py-2.5 text-sm font-semibold ring-1 ${group === g.type ? 'bg-slate-900 text-white ring-slate-900' : 'bg-white text-slate-700 ring-slate-200'}`}
                >
                  {g.label}
                </button>
              ))}
            </div>
          </fieldset>

          <fieldset className="mt-4">
            <legend className="text-sm font-medium text-slate-700">{groups.find((g) => g.type === group)!.pick}</legend>
            <div className="mt-2 flex flex-wrap gap-2">
              {inGroup.map((c) => (
                <button
                  key={c.id}
                  type="button"
                  aria-pressed={c.id === categoryId}
                  onClick={() => setCategoryId(c.id)}
                  className={`rounded-full px-3 py-1.5 text-sm ring-1 ${c.id === categoryId ? 'bg-slate-900 font-semibold text-white ring-slate-900' : 'bg-white text-slate-700 ring-slate-200'}`}
                >
                  {c.name}
                </button>
              ))}
              <button
                type="button"
                onClick={() => setAdding(true)}
                className="rounded-full px-3 py-1.5 text-sm font-semibold"
                // Green so it stands out from the categories; fixed colors so the dark theme doesn't swap them.
                style={{ backgroundColor: '#16a34a', color: '#fff' }}
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
        <label className="mt-4 flex items-center gap-3 rounded-xl bg-slate-50 px-4 py-3">
          <input type="checkbox" checked={rule} onChange={(e) => setRule(e.target.checked)} className="size-5" />
          <span className="text-sm">
            Always {selected.name} for {titleCase(t.merchant)}
            <span className="block text-xs text-slate-500">Leave this off for stores where it varies, like a supercenter.</span>
          </span>
        </label>
      )}

      <button
        type="button"
        disabled={busy || (partnerIn && !mode) || (needsCategory && !categoryId)}
        onClick={() => void save()}
        className="mt-5 w-full rounded-xl bg-slate-900 py-3.5 text-base font-semibold text-white disabled:opacity-40"
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
      <div className="mt-2 grid gap-2">
        <button type="button" onClick={() => void dontRemember()} disabled={busy} className="rounded-xl bg-white py-2.5 text-sm font-medium text-slate-600 ring-1 ring-slate-200">
          I don’t remember
        </button>
        <button type="button" onClick={onSkip} className="rounded-xl bg-white py-2.5 text-sm font-medium text-slate-600 ring-1 ring-slate-200">
          Skip for now
        </button>
      </div>
    </article>
  )
}

/** The four kinds of transaction (DECISIONS.md 145); each one's categories show when you pick it. */
const groups: { type: Category['type']; label: string; pick: string }[] = [
  { type: 'Variable', label: 'Spending', pick: 'Spending category' },
  { type: 'FixedBill', label: 'Bill', pick: 'Which bill' },
  { type: 'DebtPayment', label: 'Debt payment', pick: 'Which debt' },
  { type: 'SavingsTransfer', label: 'Savings transfer', pick: 'Which savings' },
]

/** "No category" in red, or the app's guess as a question in orange: "Groceries?" */
function StatusTag({ t }: { t: Transaction }) {
  const name = t.categoryName ?? (t.kind === 'PartnerContribution' ? 'Money from your partner' : null)
  return name ? (
    <span className="inline-block rounded-full bg-orange-50 px-2.5 py-0.5 text-xs font-semibold text-orange-700 ring-1 ring-orange-200">{name}?</span>
  ) : (
    <span className="inline-block rounded-full bg-red-50 px-2.5 py-0.5 text-xs font-semibold text-red-700 ring-1 ring-red-100">No category</span>
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
    <div className="mt-3 rounded-xl bg-slate-50 p-3">
      <label className="block">
        <span className="text-xs text-slate-500">New {kind} category</span>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={fallback || 'e.g. Household'}
          maxLength={100}
          className="mt-1 block w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-base"
        />
      </label>
      <div className="mt-3 flex gap-2">
        <button type="button" onClick={onCancel} className="flex-1 rounded-lg bg-white py-2 text-sm font-medium ring-1 ring-slate-200">
          Cancel
        </button>
        <button
          type="button"
          disabled={busy || !(name.trim() || fallback)}
          onClick={async () => {
            setBusy(true)
            try {
              const r = await budgetEditApi.createCategory(name.trim() || fallback, type, 0, false)
              await onCreated(r.id)
            } catch (e) {
              onError(e)
              setBusy(false)
            }
          }}
          className="flex-1 rounded-lg bg-slate-900 py-2 text-sm font-semibold text-white disabled:opacity-40"
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
      <button type="button" onClick={onOpen} className="min-w-0 flex-1 px-4 py-3 text-left hover:bg-slate-50">
        <div className="flex items-baseline justify-between gap-3">
          <span className="truncate text-sm font-medium">{titleCase(t.merchant)}</span>
          <span className={`shrink-0 text-sm tabular-nums ${t.amountCents < 0 ? 'text-emerald-600' : ''}`}>{formatSigned(t.amountCents)}</span>
        </div>
        <p className="mt-0.5 flex items-center gap-2 truncate text-xs text-slate-500">
          {formatDay(t.date)} <StatusTag t={t} />
        </p>
      </button>
      {label && !t.isPartnerTransfer ? (
        <button
          type="button"
          onClick={onConfirm}
          aria-label={`${label} is right for ${titleCase(t.merchant)}`}
          className="shrink-0 rounded-full bg-emerald-50 px-3 py-1.5 text-xs font-semibold text-emerald-800 ring-1 ring-emerald-200"
        >
          ✓ Right
        </button>
      ) : (
        <button type="button" onClick={onOpen} className="shrink-0 rounded-full bg-slate-100 px-3 py-1.5 text-xs font-semibold text-slate-700">
          Sort
        </button>
      )}
    </li>
  )
}

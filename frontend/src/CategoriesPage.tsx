import { useEffect, useState, type FormEvent } from 'react'
import { budgetEditApi, priorityLabels, type ArchivedCategory, type Category } from './api'
import { formatCents, formatDay } from './format'

const groups: { type: Category['type']; label: string }[] = [
  { type: 'Variable', label: 'Spending' },
  { type: 'FixedBill', label: 'Bills' },
  { type: 'DebtPayment', label: 'Debt payments' },
  { type: 'SavingsTransfer', label: 'Savings' },
]

/** "Oct 3, 2025": an archived category can be over a year old. */
function lastUsedText(c: ArchivedCategory) {
  if (!c.lastUsed) return 'Never used'
  const [y, m, d] = c.lastUsed.split('-').map(Number)
  const thisYear = new Date().getFullYear() === y
  return `Last used ${thisYear ? formatDay(c.lastUsed) : new Date(y, m - 1, d).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}`
}

/**
 * Budget → Categories (DECISIONS.md 169, 180): add a category, archive one you don't use (it can come back), or
 * delete one. Deleting replaces "Combine categories": you pick where its transactions go, or send them to Review.
 * Categories nothing has been filed under in about 3 months are suggested for archiving at the top.
 */
export default function CategoriesPage({
  categories,
  onAdd,
  onDeleted,
}: {
  categories: Category[]
  onAdd: () => void
  onDeleted: (message: string) => void
}) {
  const [deleting, setDeleting] = useState<Category | null>(null)
  const [resetting, setResetting] = useState(false)
  const [unused, setUnused] = useState<ArchivedCategory[]>([])
  const [archived, setArchived] = useState<ArchivedCategory[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Fresh whenever the categories change (an archive, restore, add or delete reloads them).
  useEffect(() => {
    let live = true
    Promise.all([budgetEditApi.unusedCategories(), budgetEditApi.archivedCategories()])
      .then(([u, a]) => {
        if (!live) return
        setUnused(u)
        setArchived(a)
      })
      .catch(() => {}) // the lists are extra: the page works without them
    return () => {
      live = false
    }
  }, [categories])

  async function act(id: string, run: () => Promise<string>) {
    setBusy(id)
    setError(null)
    try {
      onDeleted(await run())
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.')
    } finally {
      setBusy(null)
    }
  }

  const archive = (c: { id: string; name: string }) =>
    act(c.id, async () => {
      const r = await budgetEditApi.archiveCategory(c.id)
      const moved = r.releasedToReview > 0 ? ` ${r.releasedToReview} recent ${r.releasedToReview === 1 ? 'transaction filed under it waits' : 'transactions filed under it wait'} in Review for another category.` : ''
      return `Archived ${r.name}. It’s under Archived below if you need it again.${moved}`
    })
  const restore = (c: ArchivedCategory) =>
    act(c.id, async () => `${(await budgetEditApi.restoreCategory(c.id)).name} is back in your budget.`)
  const keep = (c: ArchivedCategory) =>
    act(c.id, async () => {
      await budgetEditApi.keepCategory(c.id)
      return `Keeping ${c.name}. It won’t be suggested again for a few months.`
    })

  return (
    <div className="pb-6">
      <button type="button" onClick={onAdd} className="mt-4 w-full rounded-2xl bg-coral shadow-neu-sm py-3 text-sm font-semibold text-coral-ink">
        + Add a category
      </button>

      {error && (
        <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {error}
        </p>
      )}

      {unused.length > 0 && (
        <section className="mt-4 rounded-xl neu-inset p-4" aria-labelledby="unused-h">
          <h2 id="unused-h" className="text-sm font-semibold text-gold-ink">
            Not used in about 3 months
          </h2>
          <p className="mt-0.5 text-xs text-gold-ink">Archive one to take its limit out of your budget. Its history stays, and you can bring it back.</p>
          <ul className="mt-2 space-y-2">
            {unused.map((c) => (
              <li key={c.id} className="flex items-center gap-2 rounded-xl neu-inset px-3 py-2">
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium">{c.name}</span>
                  <span className="block truncate text-xs text-ink-soft tabular-nums">
                    {formatCents(c.monthlyLimitCents)} a month · {lastUsedText(c)}
                  </span>
                </span>
                <button type="button" disabled={busy !== null} onClick={() => void keep(c)} className="shrink-0 rounded-full px-2.5 py-1.5 text-xs font-medium text-ink-soft disabled:opacity-40">
                  Keep
                </button>
                <button type="button" disabled={busy !== null} onClick={() => void archive(c)} className="shrink-0 rounded-full bg-coral shadow-neu-sm px-2.5 py-1.5 text-xs font-semibold text-coral-ink disabled:opacity-40">
                  Archive
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      {groups.map((g) => {
        const list = categories.filter((c) => c.type === g.type).sort((a, b) => a.sortOrder - b.sortOrder)
        if (list.length === 0) return null
        return (
          <section key={g.type} className="mt-6" aria-labelledby={`cats-${g.type}`}>
            <h2 id={`cats-${g.type}`} className="px-1 text-sm font-semibold text-ink-soft">
              {g.label}
            </h2>
            <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
              {list.map((c) => (
                <li key={c.id} className="flex items-center gap-3 px-4 py-2.5">
                  <a href={`#/budget/c/${c.id}`} className="min-w-0 flex-1">
                    <span className="block truncate text-sm font-medium">{c.name}</span>
                    <span className="text-xs text-ink-soft tabular-nums">
                      {c.type === 'Variable' && `${priorityLabels[c.priority]} · `}
                      {formatCents(c.monthlyLimitCents)} a month
                    </span>
                  </a>
                  <button
                    type="button"
                    disabled={busy !== null}
                    onClick={() => void archive(c)}
                    aria-label={`Archive ${c.name}`}
                    className="shrink-0 rounded-full px-3 py-1.5 text-xs font-medium text-ink-soft disabled:opacity-40"
                  >
                    {busy === c.id ? '…' : 'Archive'}
                  </button>
                  <button
                    type="button"
                    onClick={() => setDeleting(c)}
                    aria-label={`Delete ${c.name}`}
                    className="shrink-0 rounded-full px-3 py-1.5 text-xs font-medium text-danger"
                  >
                    Delete
                  </button>
                </li>
              ))}
            </ul>
          </section>
        )
      })}

      {archived.length > 0 && (
        <details className="mt-6">
          <summary className="cursor-pointer text-sm font-semibold text-ink-soft">Archived ({archived.length})</summary>
          <p className="mt-1 text-xs text-ink-soft">Out of your budget, with their history kept. Restore one to use it again.</p>
          <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
            {archived.map((c) => (
              <li key={c.id} className="flex items-center gap-3 px-4 py-2.5">
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium">{c.name}</span>
                  <span className="block truncate text-xs text-ink-soft">{lastUsedText(c)}</span>
                </span>
                <button
                  type="button"
                  disabled={busy !== null}
                  onClick={() => void restore(c)}
                  aria-label={`Restore ${c.name}`}
                  className="shrink-0 rounded-full px-3 py-1.5 text-xs font-medium text-ink-soft disabled:opacity-40"
                >
                  {busy === c.id ? '…' : 'Restore'}
                </button>
              </li>
            ))}
          </ul>
        </details>
      )}

      {/* Moved here from More → Bank connections (DECISIONS.md 176). Fixed colors so the dark theme keeps them. */}
      <section className="mt-8 rounded-2xl neu p-4" aria-labelledby="reset-cats-h">
        <h2 id="reset-cats-h" className="px-1 text-sm font-semibold text-ink-soft">
          Start categories over
        </h2>
        <p className="mt-1 text-xs text-ink-soft">Re-files every transaction from your bank’s categories, replacing your own picks.</p>
        <button
          type="button"
          onClick={() => setResetting(true)}
          className="neu mt-3 w-full rounded-2xl py-3 text-sm font-semibold text-danger"
        >
          Refresh categories from bank
        </button>
      </section>

      {resetting && (
        <ResetSheet
          onClose={() => setResetting(false)}
          onDone={(m) => {
            setResetting(false)
            onDeleted(m)
          }}
        />
      )}

      {deleting && (
        <DeleteSheet
          category={deleting}
          others={categories.filter((c) => c.id !== deleting.id)}
          onClose={() => setDeleting(null)}
          onDeleted={(m) => {
            setDeleting(null)
            onDeleted(m)
          }}
        />
      )}
    </div>
  )
}

/** The caution before "Refresh categories from bank": what it does, what stays, and that it can't be undone. */
function ResetSheet({ onClose, onDone }: { onClose: () => void; onDone: (message: string) => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function reset() {
    setBusy(true)
    setError(null)
    try {
      const r = await budgetEditApi.resetCategoriesFromBank()
      const waiting = r.filed + r.guesses
      onDone(
        [
          `Started over: ${waiting} ${waiting === 1 ? 'transaction has' : 'transactions have'} a new category to confirm in Review`,
          r.stillUncategorized > 0 ? `, and ${r.stillUncategorized} need${r.stillUncategorized === 1 ? 's' : ''} one picked.` : '.',
          ...r.bankNotes.map((n) => ` ${n}`),
        ].join(''),
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not refresh the categories.')
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={busy ? undefined : onClose}>
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="reset-h"
        aria-describedby="reset-d"
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <p className="text-xs font-semibold tracking-wide uppercase" style={{ color: '#B45309' }}>
          Caution
        </p>
        <h2 id="reset-h" className="mt-1 text-lg font-semibold">
          Reset all categories?
        </h2>
        <div id="reset-d" className="mt-2 space-y-2 text-sm text-ink-soft">
          <p>
            This clears the category on <strong>every</strong> transaction, including the ones you picked yourself, downloads what your bank says
            about each one again, and files them all from the bank’s info and the app’s guesses.
          </p>
          <p>Everything it files waits in Review for you to confirm. Ones it can’t place wait there with no category.</p>
          <p>Your merchant rules, transfers, and what you said about money from your partner stay as they are.</p>
          <p className="font-semibold text-danger">
            This can’t be undone.
          </p>
        </div>

        {error && (
          <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {error}
          </p>
        )}
        <div className="mt-5 grid grid-cols-2 gap-3">
          <button type="button" disabled={busy} onClick={onClose} className="rounded-xl neu-sm py-3 text-sm font-semibold text-ink-soft disabled:opacity-40">
            Cancel
          </button>
          <button type="button" disabled={busy} onClick={reset} className="rounded-2xl bg-danger py-3 text-sm font-semibold text-coral-ink shadow-neu-sm disabled:opacity-60">
            {busy ? 'Refreshing…' : 'Yes, reset all'}
          </button>
        </div>
      </div>
    </div>
  )
}

const REVIEW = 'review'

function DeleteSheet({
  category: c,
  others,
  onClose,
  onDeleted,
}: {
  category: Category
  others: Category[]
  onClose: () => void
  onDeleted: (message: string) => void
}) {
  const [to, setTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const target = others.find((o) => o.id === to)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (to === '') return
    setBusy(true)
    setError(null)
    try {
      const r = await budgetEditApi.deleteCategory(c.id, to === REVIEW ? undefined : to)
      onDeleted(
        r.movedTo
          ? `Deleted ${r.name}. ${r.movedTransactions} ${r.movedTransactions === 1 ? 'transaction' : 'transactions'} moved to ${r.movedTo}.`
          : `Deleted ${r.name}. ${r.toReview} ${r.toReview === 1 ? 'transaction is' : 'transactions are'} in Review for a new category.`,
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not delete it.')
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="delete-cat-h"
        onSubmit={submit}
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="delete-cat-h" className="text-lg font-semibold">
          Delete {c.name}?
        </h2>
        <p className="mt-1 text-sm text-ink-soft">Where should the transactions filed under it go? This can’t be undone.</p>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-ink-soft">Send its transactions to</span>
          <select value={to} onChange={(e) => setTo(e.target.value)} className="mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base">
            <option value="" disabled>
              Choose…
            </option>
            <option value={REVIEW}>Nowhere: send them to Review</option>
            {groups.map((g) => {
              const list = others.filter((o) => o.type === g.type).sort((a, b) => a.sortOrder - b.sortOrder)
              return list.length === 0 ? null : (
                <optgroup key={g.type} label={g.label}>
                  {list.map((o) => (
                    <option key={o.id} value={o.id}>
                      {o.name}
                    </option>
                  ))}
                </optgroup>
              )
            })}
          </select>
          <span className="mt-1 block text-xs text-ink-soft">
            {to === REVIEW
              ? 'They lose their category and wait in Review, with the app’s best guess where it has one, for you to pick a new one.'
              : target
                ? `They move to ${target.name}, and new ones like them go there too. ${target.name} keeps its own limit.`
                : 'Pick a category, or send them to Review to file one at a time.'}
          </span>
        </label>

        {error && (
          <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {error}
          </p>
        )}
        <div className="mt-5 grid grid-cols-2 gap-3">
          <button type="button" onClick={onClose} className="rounded-xl neu-sm py-3 text-sm font-semibold text-ink-soft">
            Cancel
          </button>
          <button type="submit" disabled={to === '' || busy} className="rounded-2xl bg-danger py-3 text-sm font-semibold text-coral-ink shadow-neu-sm disabled:opacity-40">
            {busy ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </form>
    </div>
  )
}

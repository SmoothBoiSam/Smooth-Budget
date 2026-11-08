import { useState, type FormEvent } from 'react'
import { budgetEditApi, type Category } from './api'
import { formatCents } from './format'

const groups: { type: Category['type']; label: string }[] = [
  { type: 'Variable', label: 'Spending' },
  { type: 'FixedBill', label: 'Bills' },
  { type: 'DebtPayment', label: 'Debt payments' },
  { type: 'SavingsTransfer', label: 'Savings' },
]

/**
 * Budget → Categories (DECISIONS.md 169): add a category, or delete one. Deleting replaces "Combine categories": you
 * pick where its transactions go, or send them to Review to file one by one.
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

  return (
    <div className="pb-6">
      <button type="button" onClick={onAdd} className="mt-4 w-full rounded-xl bg-slate-900 py-3 text-sm font-semibold text-white">
        + Add a category
      </button>

      {groups.map((g) => {
        const list = categories.filter((c) => c.type === g.type).sort((a, b) => a.sortOrder - b.sortOrder)
        if (list.length === 0) return null
        return (
          <section key={g.type} className="mt-6" aria-labelledby={`cats-${g.type}`}>
            <h2 id={`cats-${g.type}`} className="text-sm font-semibold text-slate-700">
              {g.label}
            </h2>
            <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
              {list.map((c) => (
                <li key={c.id} className="flex items-center gap-3 px-4 py-2.5">
                  <a href={`#/budget/c/${c.id}`} className="min-w-0 flex-1">
                    <span className="block truncate text-sm font-medium">{c.name}</span>
                    <span className="text-xs text-slate-500 tabular-nums">{formatCents(c.monthlyLimitCents)} a month</span>
                  </a>
                  <button
                    type="button"
                    onClick={() => setDeleting(c)}
                    aria-label={`Delete ${c.name}`}
                    className="shrink-0 rounded-full px-3 py-1.5 text-xs font-medium text-red-600 ring-1 ring-red-200"
                  >
                    Delete
                  </button>
                </li>
              ))}
            </ul>
          </section>
        )
      })}

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
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-white p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="delete-cat-h" className="text-lg font-semibold">
          Delete {c.name}?
        </h2>
        <p className="mt-1 text-sm text-slate-500">Where should the transactions filed under it go? This can’t be undone.</p>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Send its transactions to</span>
          <select value={to} onChange={(e) => setTo(e.target.value)} className="mt-1 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-base">
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
          <span className="mt-1 block text-xs text-slate-500">
            {to === REVIEW
              ? 'They lose their category and wait in Review, with the app’s best guess where it has one, for you to pick a new one.'
              : target
                ? `They move to ${target.name}, and new ones like them go there too. ${target.name} keeps its own limit.`
                : 'Pick a category, or send them to Review to file one at a time.'}
          </span>
        </label>

        {error && (
          <p role="alert" className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </p>
        )}
        <div className="mt-5 grid grid-cols-2 gap-3">
          <button type="button" onClick={onClose} className="rounded-xl bg-slate-100 py-3 text-sm font-semibold text-slate-700">
            Cancel
          </button>
          <button type="submit" disabled={to === '' || busy} className="rounded-xl py-3 text-sm font-semibold disabled:opacity-40" style={{ backgroundColor: '#DC2626', color: '#fff' }}>
            {busy ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </form>
    </div>
  )
}

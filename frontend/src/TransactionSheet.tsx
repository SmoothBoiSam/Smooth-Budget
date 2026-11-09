import { useState, type FormEvent } from 'react'
import { budgetApi, type Category, type Transaction, listedType } from './api'
import { formatDay, formatSigned, titleCase } from './format'

const groupLabels: Record<Category['type'], string> = {
  Variable: 'Spending',
  FixedBill: 'Bills',
  DebtPayment: 'Debt',
  SavingsTransfer: 'Savings',
}

/** Bottom sheet to recategorize one transaction, optionally creating a merchant rule. */
export default function TransactionSheet({
  transaction: t,
  categories,
  onClose,
  onSaved,
}: {
  transaction: Transaction
  categories: Category[]
  onClose: () => void
  onSaved: (message: string) => void
}) {
  const [categoryId, setCategoryId] = useState(t.categoryId ?? '')
  const [isTransfer, setIsTransfer] = useState(t.isTransfer)
  const [makeRule, setMakeRule] = useState(false)
  const [pattern, setPattern] = useState(t.merchant)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function save(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const rule = makeRule && !isTransfer && categoryId ? pattern : null
      const r = await budgetApi.updateTransaction(t.id, categoryId || null, isTransfer, rule)
      const moved = r.billChargesMoved > 0 ? ` ${r.billChargesMoved} earlier ${r.billChargesMoved === 1 ? 'charge' : 'charges'} of this bill moved there too.` : ''
      onSaved(
        (rule
          ? `Saved. Rule created for "${rule.toUpperCase()}"${r.ruleAppliedTo > 0 ? ` and applied to ${r.ruleAppliedTo} more` : ''}.`
          : 'Saved.') + moved,
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save.')
      setBusy(false)
    }
  }

  const grouped = (Object.keys(groupLabels) as Category['type'][]).map((type) => ({
    type,
    items: categories.filter((c) => listedType(c) === type),
  }))

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="sheet-title"
        onSubmit={save}
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 id="sheet-title" className="truncate text-lg font-semibold">
              {titleCase(t.merchant)}
            </h2>
            <p className="text-sm text-ink-soft">
              {formatDay(t.date)} · {t.accountName}
            </p>
          </div>
          <p className={`text-lg font-semibold tabular-nums ${t.amountCents < 0 ? 'text-good' : ''}`}>{formatSigned(t.amountCents)}</p>
        </div>
        <p className="mt-2 break-words text-xs text-ink-faint">
          {t.description}
          {t.bankCategory && ` · Bank category: ${t.bankCategory}`}
        </p>
        {t.categoryReason && t.categoryName && (
          <p className={`mt-2 rounded-lg px-3 py-2 text-xs ${t.needsReview ? 'neu-inset text-gold-ink' : 'neu-inset text-ink-soft'}`}>
            {t.needsReview ? `${t.unsure ? "Not sure" : "Not confirmed yet"}. Sorted into ${t.categoryName}: ` : `${t.categoryName}: `}
            {t.categoryReason}
          </p>
        )}

        <label className="mt-5 flex items-center gap-3 rounded-xl neu-inset px-4 py-3">
          <input type="checkbox" checked={isTransfer} onChange={(e) => setIsTransfer(e.target.checked)} className="size-5" />
          <span className="text-sm">
            Transfer or card payment
            <span className="block text-xs text-ink-soft">Never counted as spending</span>
          </span>
        </label>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-ink-soft">Category</span>
          <select
            value={categoryId}
            onChange={(e) => setCategoryId(e.target.value)}
            className="mt-1 block w-full rounded-xl neu-inset px-4 py-3 text-base"
          >
            <option value="">No category</option>
            {grouped
              .filter((g) => g.items.length > 0)
              .map((g) => (
                <optgroup key={g.type} label={groupLabels[g.type]}>
                  {g.items.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.name}
                    </option>
                  ))}
                </optgroup>
              ))}
          </select>
        </label>

        {!isTransfer && categoryId && (
          <div className="mt-4 rounded-xl neu-inset px-4 py-3">
            <label className="flex items-center gap-3">
              <input type="checkbox" checked={makeRule} onChange={(e) => setMakeRule(e.target.checked)} className="size-5" />
              <span className="text-sm">Always use this category for this merchant</span>
            </label>
            {makeRule && (
              <label className="mt-3 block">
                <span className="text-xs text-ink-soft">Merchants containing</span>
                <input
                  value={pattern}
                  onChange={(e) => setPattern(e.target.value)}
                  required
                  className="mt-1 block w-full rounded-2xl neu-inset px-4 py-3 text-sm uppercase"
                />
                <span className="mt-1 block text-xs text-ink-soft">Shorten it (e.g. just the store name) to match every location.</span>
              </label>
            )}
          </div>
        )}

        {error && (
          <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {error}
          </p>
        )}

        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-2xl neu py-3 font-semibold text-danger text-sm">
            Cancel
          </button>
          <button type="submit" disabled={busy} className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-50 text-sm">
            {busy ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>
    </div>
  )
}

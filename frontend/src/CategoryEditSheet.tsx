import { useState, type FormEvent } from 'react'
import { budgetEditApi, type BudgetCategoryLine, type Category } from './api'
import { centsToInput, parseDollars } from './format'

const typeLabels: Record<Category['type'], string> = {
  Variable: 'Spending (counts toward Left to Spend)',
  FixedBill: 'Bill or subscription',
  DebtPayment: 'Debt payment',
  SavingsTransfer: 'Savings',
}

/**
 * Add or edit a category (SPEC 4.3). Editing a limit asks whether it's for this month only or from now on.
 * Move up/down reorders within its group; Hide removes it from the budget without deleting history.
 */
export default function CategoryEditSheet({
  line,
  usualLimitCents,
  onClose,
  onSaved,
  onMove,
}: {
  line: BudgetCategoryLine | null // null = new category
  usualLimitCents?: number
  onClose: () => void
  onSaved: (message: string) => void
  onMove?: (direction: -1 | 1) => Promise<void>
}) {
  const isNew = line === null
  const [name, setName] = useState(line?.name ?? '')
  const [type, setType] = useState<Category['type']>(line?.type ?? 'Variable')
  const [limit, setLimit] = useState(line ? centsToInput(line.limitCents) : '')
  const [discretionary, setDiscretionary] = useState(line?.isDiscretionary ?? false)
  const [applyTo, setApplyTo] = useState<'month' | 'permanent'>(line?.hasMonthOverride ? 'month' : 'permanent')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const limitCents = parseDollars(limit)
  const limitChanged = !isNew && limitCents !== null && limitCents !== line.limitCents

  async function save(e: FormEvent, hide = false) {
    e.preventDefault()
    if (limitCents === null) return setError('Enter the limit in dollars, like 450 or 450.00.')
    setBusy(true)
    setError(null)
    try {
      if (isNew) {
        await budgetEditApi.createCategory(name, type, limitCents, type === 'Variable' && discretionary)
        onSaved(`Added ${name.trim()}.`)
      } else {
        await budgetEditApi.updateCategory(line.id, {
          name,
          type,
          limitCents,
          isDiscretionary: type === 'Variable' && discretionary,
          isHidden: hide,
          applyTo,
        })
        onSaved(hide ? `${name.trim()} is hidden.` : applyTo === 'month' && limitChanged ? `Saved for this month only.` : 'Saved.')
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save.')
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="cat-sheet-h"
        onSubmit={save}
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-white p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="cat-sheet-h" className="text-lg font-semibold">
          {isNew ? 'Add a category' : `Edit ${line.name}`}
        </h2>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Name</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            required
            maxLength={100}
            className="mt-1 block w-full rounded-xl border border-slate-300 bg-white px-4 py-3 text-base"
          />
        </label>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Type</span>
          <select
            value={type}
            onChange={(e) => setType(e.target.value as Category['type'])}
            className="mt-1 block w-full rounded-xl border border-slate-300 bg-white px-4 py-3 text-base"
          >
            {(Object.keys(typeLabels) as Category['type'][]).map((t) => (
              <option key={t} value={t}>
                {typeLabels[t]}
              </option>
            ))}
          </select>
        </label>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Monthly limit</span>
          <div className="mt-1 flex items-center rounded-xl border border-slate-300 bg-white px-4">
            <span className="text-slate-500">$</span>
            <input
              value={limit}
              onChange={(e) => setLimit(e.target.value)}
              inputMode="decimal"
              required
              aria-invalid={limitCents === null && limit !== ''}
              className="block w-full bg-transparent px-2 py-3 text-base outline-none"
            />
          </div>
          {!isNew && line.hasMonthOverride && usualLimitCents !== undefined && (
            <span className="mt-1 block text-xs text-slate-500">This month only. Usually ${centsToInput(usualLimitCents)}.</span>
          )}
        </label>

        {!isNew && (
          <fieldset className="mt-3">
            <legend className="sr-only">Apply the limit to</legend>
            <div className="grid grid-cols-2 gap-1 rounded-lg bg-slate-100 p-1">
              {(['month', 'permanent'] as const).map((v) => (
                <button
                  key={v}
                  type="button"
                  role="radio"
                  aria-checked={applyTo === v}
                  onClick={() => setApplyTo(v)}
                  className={`rounded-md py-2 text-sm font-medium ${applyTo === v ? 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-200' : 'text-slate-500'}`}
                >
                  {v === 'month' ? 'This month only' : 'From now on'}
                </button>
              ))}
            </div>
          </fieldset>
        )}

        {type === 'Variable' && (
          <label className="mt-4 flex items-center gap-3 rounded-xl bg-slate-50 px-4 py-3">
            <input type="checkbox" checked={discretionary} onChange={(e) => setDiscretionary(e.target.checked)} className="size-5" />
            <span className="text-sm">
              Nice-to-have spending
              <span className="block text-xs text-slate-500">Like eating out or shopping: trimmed first when money is tight</span>
            </span>
          </label>
        )}

        {error && (
          <p role="alert" className="mt-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </p>
        )}

        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-xl bg-slate-100 py-3 font-semibold text-slate-700">
            Cancel
          </button>
          <button type="submit" disabled={busy} className="flex-1 rounded-xl bg-slate-900 py-3 font-semibold text-white disabled:opacity-50">
            {busy ? 'Saving…' : isNew ? 'Add' : 'Save'}
          </button>
        </div>

        {!isNew && (
          <div className="mt-4 flex items-center justify-between text-sm">
            {onMove ? (
              <div className="flex gap-4">
                <button type="button" disabled={busy} onClick={() => onMove(-1)} className="font-medium text-slate-600">
                  ↑ Move up
                </button>
                <button type="button" disabled={busy} onClick={() => onMove(1)} className="font-medium text-slate-600">
                  ↓ Move down
                </button>
              </div>
            ) : (
              <span />
            )}
            <button
              type="button"
              disabled={busy}
              onClick={(e) => {
                if (window.confirm(`Hide ${line.name}? It leaves the budget; its past transactions stay.`)) void save(e as unknown as FormEvent, true)
              }}
              className="font-medium text-red-600"
            >
              Hide category
            </button>
          </div>
        )}
      </form>
    </div>
  )
}

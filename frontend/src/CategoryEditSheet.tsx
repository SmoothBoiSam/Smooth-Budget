import { useState, type FormEvent } from 'react'
import { budgetEditApi, priorityLabels, type BudgetCategoryLine, type Category, type Priority } from './api'
import { centsToInput, parseDollars } from './format'

const typeLabels: Record<Category['type'], string> = {
  Variable: 'Spending (counts toward Left to Spend)',
  FixedBill: 'Bill',
  DebtPayment: 'Debt payment',
  SavingsTransfer: 'Savings',
}

/**
 * Add or edit a category (SPEC 4.3). Editing a limit asks whether it's for this month only or from now on.
 * Move up/down reorders within its group; Archive removes it from the budget without deleting history.
 */
export default function CategoryEditSheet({
  line,
  usualLimitCents,
  onClose,
  onSaved,
  onMove,
  auto,
}: {
  line: BudgetCategoryLine | null // null = new category
  usualLimitCents?: number
  /** With the automatic budget on: whether this limit is yours (set by you) or the budget's. */
  auto?: { on: boolean; setByYou: boolean }
  onClose: () => void
  onSaved: (message: string) => void
  onMove?: (direction: -1 | 1) => Promise<void>
}) {
  const isNew = line === null
  const [name, setName] = useState(line?.name ?? '')
  const [type, setType] = useState<Category['type']>(line?.type ?? 'Variable')
  const [limit, setLimit] = useState(line ? centsToInput(line.limitCents) : '')
  const [priority, setPriority] = useState<Priority>(line?.priority ?? 'Medium')
  const [applyTo, setApplyTo] = useState<'month' | 'permanent'>(line?.hasMonthOverride ? 'month' : 'permanent')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const limitCents = parseDollars(limit)
  const limitChanged = !isNew && limitCents !== null && limitCents !== line.limitCents

  async function backToAutomatic() {
    if (!line) return
    setBusy(true)
    setError(null)
    try {
      await budgetEditApi.updateCategory(line.id, {
        name: line.name, type: line.type, limitCents: line.limitCents, priority: line.priority, isHidden: false, applyTo: 'permanent', automatic: true,
      })
      onSaved(`${line.name} is automatic again.`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save.')
      setBusy(false)
    }
  }

  /** Archive (DECISIONS.md 180): out of the budget, history kept, back from Budget → Categories → Archived. */
  async function archive() {
    if (!line) return
    setBusy(true)
    setError(null)
    try {
      const r = await budgetEditApi.archiveCategory(line.id)
      const moved = r.releasedToReview > 0 ? ` ${r.releasedToReview} recent transactions filed under it wait in Review for another category.` : ''
      onSaved(`${r.name} is archived.${moved}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not archive it.')
      setBusy(false)
    }
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    if (limitCents === null) return setError('Enter the limit in dollars, like 450 or 450.00.')
    setBusy(true)
    setError(null)
    try {
      if (isNew) {
        await budgetEditApi.createCategory(name, type, limitCents, type === 'Variable' ? priority : 'Medium')
        onSaved(`Added ${name.trim()}.`)
      } else {
        await budgetEditApi.updateCategory(line.id, {
          name,
          type,
          limitCents,
          priority: type === 'Variable' ? priority : 'Medium',
          isHidden: false,
          applyTo,
        })
        onSaved(applyTo === 'month' && limitChanged ? `Saved for this month only.` : 'Saved.')
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

        {!isNew && auto?.on && (
          <p className="mt-2 text-xs text-slate-500">
            {auto.setByYou ? (
              <>
                You set this limit, so the automatic budget leaves it alone.{' '}
                <button type="button" disabled={busy} onClick={() => void backToAutomatic()} className="font-medium text-sky-700 underline">
                  Back to automatic
                </button>
              </>
            ) : (
              'Set by the automatic budget. Change it and it becomes yours: the budget works around it.'
            )}
          </p>
        )}

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
          <fieldset className="mt-4">
            <legend className="text-sm font-medium text-slate-700">Priority</legend>
            <div className="mt-2 grid grid-cols-3 gap-1 rounded-lg bg-slate-100 p-1">
              {(['High', 'Medium', 'Low'] as const).map((p) => (
                <button
                  key={p}
                  type="button"
                  role="radio"
                  aria-checked={priority === p}
                  onClick={() => setPriority(p)}
                  className={`rounded-md px-1 py-2 text-sm font-medium ${priority === p ? 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-200' : 'text-slate-500'}`}
                >
                  {priorityLabels[p]}
                </button>
              ))}
            </div>
            <p className="mt-2 text-xs text-slate-500">
              When money is short, Nice to have is trimmed first, then Important, then Must have (never below 80% of usual).
            </p>
          </fieldset>
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
              onClick={() => {
                if (window.confirm(`Archive ${line.name}? It leaves the budget; its history stays, and you can restore it from Budget → Categories.`)) void archive()
              }}
              className="font-medium text-red-600"
            >
              Archive category
            </button>
          </div>
        )}
      </form>
    </div>
  )
}

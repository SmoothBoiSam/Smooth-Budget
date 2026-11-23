import { useState, type FormEvent } from 'react'
import { ask } from './confirm'
import { budgetEditApi, listedType, priorityLabels, type BudgetCategoryLine, type Category, type Priority } from './api'
import { DeleteCategorySheet } from './CategoryTools'
import { centsToInput, formatCents, parseDollars } from './format'
import { Chevron, sheetCls, smallBtn } from './ui'

type Part = 'edit' | 'merge' | 'delete'

/**
 * A category's pop-up (SPEC 4.3, DECISIONS.md 228): everything you can do to one category in one place. Edit its
 * name, limit (this month only or from now on, or back to automatic) and priority; switch a spending category and a
 * bill line; move it up or down; merge it into another; archive it (history kept, restorable from the bottom of the
 * Budget tab); or delete it, choosing where its transactions go. Adding one makes a spending category: bill lines come
 * from bills, and debt and savings are run from the Plan tab.
 */
export default function CategoryEditSheet({
  line,
  categories,
  usualLimitCents,
  onClose,
  onSaved,
  onMove,
  auto,
}: {
  line: BudgetCategoryLine | null // null = new category
  /** Every category, to merge into or move transactions to. */
  categories: Category[]
  usualLimitCents?: number
  /** With the automatic budget on: whether this limit is yours (set by you) or the budget's. */
  auto?: { on: boolean; setByYou: boolean }
  onClose: () => void
  onSaved: (message: string) => void
  /** Reorder: resolves to where it is now, said in the pop-up. */
  onMove?: (direction: -1 | 1) => Promise<string>
}) {
  const isNew = line === null
  const [part, setPart] = useState<Part>('edit')
  const [name, setName] = useState(line?.name ?? '')
  const [type, setType] = useState<Category['type']>(line?.type ?? 'Variable')
  const [limit, setLimit] = useState(line ? centsToInput(line.limitCents) : '')
  const [priority, setPriority] = useState<Priority>(line?.priority ?? 'Medium')
  const [applyTo, setApplyTo] = useState<'month' | 'permanent'>(line?.hasMonthOverride ? 'month' : 'permanent')
  const [into, setInto] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [moved, setMoved] = useState<string | null>(null)
  async function reorder(direction: -1 | 1) {
    if (!onMove) return
    setBusy(true)
    setError(null)
    try {
      setMoved(await onMove(direction))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not move it.')
    } finally {
      setBusy(false)
    }
  }

  const limitCents = parseDollars(limit)
  const limitChanged = !isNew && limitCents !== null && limitCents !== line.limitCents
  const self = line ? categories.find((c) => c.id === line.id) : undefined
  // Spending and bill lines can switch; Card interest and debt and savings lines keep their kind.
  const switchable = isNew || (self ? listedType(self) === 'Variable' || listedType(self) === 'FixedBill' : false)
  const others = categories.filter((c) => c.id !== line?.id)
  // Merging stays within its kind: spending into spending, a bill line into a bill line.
  const mergeable = self ? others.filter((c) => listedType(c) === listedType(self)).sort((a, b) => a.sortOrder - b.sortOrder) : []
  const target = mergeable.find((c) => c.id === into)

  async function run(action: () => Promise<string>, failed: string) {
    setBusy(true)
    setError(null)
    try {
      onSaved(await action())
    } catch (err) {
      setError(err instanceof Error ? err.message : failed)
      setBusy(false)
    }
  }

  const backToAutomatic = () =>
    line &&
    run(async () => {
      await budgetEditApi.updateCategory(line.id, {
        name: line.name, type: line.type, limitCents: line.limitCents, priority: line.priority, isHidden: false, applyTo: 'permanent', automatic: true,
      })
      return `${line.name} is automatic again.`
    }, 'Could not save.')

  /** Archive (DECISIONS.md 180): out of the budget, history kept, back from Archived at the bottom of the Budget tab. */
  async function archive() {
    if (!line) return
    if (!(await ask(`Archive ${line.name}? It leaves your budget; its history stays, and you can restore it from Archived at the bottom of the Budget tab.`, { yes: 'Archive', danger: true })))
      return
    await run(async () => {
      const r = await budgetEditApi.archiveCategory(line.id)
      const moved = r.releasedToReview > 0 ? ` ${r.releasedToReview} recent transactions filed under it wait in Tasks for another category.` : ''
      return `Archived ${r.name}.${moved}`
    }, 'Could not archive it.')
  }

  /** Merge (DECISIONS.md 144): its transactions, rules and limit join the other category, and this one is gone. */
  const merge = () =>
    line &&
    target &&
    run(async () => {
      const r = await budgetEditApi.mergeCategories([line.id], target.id)
      return `Merged ${line.name} into ${r.name}: ${r.movedTransactions} ${r.movedTransactions === 1 ? 'transaction' : 'transactions'} moved, and the limits added together.`
    }, 'Could not merge them.')

  async function save(e: FormEvent) {
    e.preventDefault()
    if (limitCents === null) return setError('Enter the limit in dollars, like 450 or 450.00.')
    await run(async () => {
      if (isNew) {
        await budgetEditApi.createCategory(name, type, limitCents, type === 'Variable' ? priority : 'Medium')
        return `Added ${name.trim()}.`
      }
      await budgetEditApi.updateCategory(line.id, {
        name,
        type,
        limitCents,
        priority: type === 'Variable' ? priority : 'Medium',
        isHidden: false,
        applyTo,
      })
      return applyTo === 'month' && limitChanged ? `Saved for this month only.` : 'Saved.'
    }, 'Could not save.')
  }

  if (part === 'delete' && line && self)
    return <DeleteCategorySheet category={self} others={others} onClose={() => setPart('edit')} onDeleted={onSaved} />

  const action = 'flex w-full items-center justify-between gap-3 px-4 py-3 text-left text-sm font-medium disabled:opacity-50'
  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form role="dialog" aria-modal="true" aria-labelledby="cat-sheet-h" onSubmit={save} onClick={(e) => e.stopPropagation()} className={sheetCls}>
        <h2 id="cat-sheet-h" className="text-lg font-semibold">
          {isNew ? 'Add a Spending Category' : `Edit ${line.name}`}
        </h2>

        {part === 'merge' && line && (
          <section className="mt-4">
            <button type="button" onClick={() => setPart('edit')} className={`${smallBtn} mb-3 text-danger`}>
              ‹ Back
            </button>
            <p className="text-sm font-semibold">Merge {line.name} into…</p>
            <p className="mt-0.5 text-xs text-ink-soft">
              Its transactions, store rules and limit join the category you pick, and {line.name} goes away. New purchases like its old ones go there
              too. This can’t be undone.
            </p>
            {mergeable.length === 0 ? (
              <p className="mt-3 text-sm text-ink-soft">There’s no other {listedType(self!) === 'FixedBill' ? 'bill' : 'spending category'} to merge it into.</p>
            ) : (
              <>
                <select value={into} onChange={(e) => setInto(e.target.value)} aria-label="Merge into" className="mt-3 block w-full rounded-xl neu-inset px-3 py-2.5 text-base">
                  <option value="" disabled>
                    {listedType(self!) === 'FixedBill' ? 'Pick a bill…' : 'Pick a spending category…'}
                  </option>
                  {mergeable.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.name} · {formatCents(c.monthlyLimitCents)} a month
                    </option>
                  ))}
                </select>
                {error && (
                  <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
                    {error}
                  </p>
                )}
                <button
                  type="button"
                  disabled={!target || busy}
                  onClick={() => void merge()}
                  className="mt-4 w-full rounded-2xl bg-danger py-3 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-50"
                >
                  {busy ? 'Merging…' : target ? `Merge into ${target.name}` : 'Merge'}
                </button>
              </>
            )}
          </section>
        )}

        {part === 'edit' && (
          <>
            <label className="mt-4 block">
              <span className="text-sm font-medium text-ink-soft">Name</span>
              <input value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} className="mt-1 block w-full rounded-xl neu-inset px-4 py-3 text-base" />
            </label>

            {!isNew && switchable && (
              <fieldset className="mt-4">
                <legend className="text-sm font-medium text-ink-soft">It’s a</legend>
                <div className="mt-1 grid grid-cols-2 gap-1.5 rounded-lg neu-inset p-1">
                  {(
                    [
                      ['Variable', 'Spending Category'],
                      ['FixedBill', 'Bill'],
                    ] as const
                  ).map(([t, label]) => (
                    <button
                      key={t}
                      type="button"
                      role="radio"
                      aria-checked={type === t}
                      onClick={() => setType(t)}
                      className={`rounded-md py-2 text-sm font-medium ${type === t ? 'neu-sm text-accent' : 'text-ink-soft'}`}
                    >
                      {label}
                    </button>
                  ))}
                </div>
                <p className="mt-1 text-xs text-ink-soft">
                  {type === 'Variable' ? 'Counts toward Left to Spend.' : 'Holds bills: planned up front, not part of Left to Spend.'}
                </p>
              </fieldset>
            )}

            <label className="mt-4 block">
              <span className="text-sm font-medium text-ink-soft">Monthly limit</span>
              <div className="mt-1 flex items-center rounded-xl neu-inset px-4">
                <span className="text-ink-soft">$</span>
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
                <span className="mt-1 block text-xs text-ink-soft">This month only. Usually ${centsToInput(usualLimitCents)}.</span>
              )}
            </label>

            {!isNew && auto?.on && (
              <p className="mt-2 text-xs text-ink-soft">
                {auto.setByYou ? (
                  <>
                    You set this limit, so the automatic budget leaves it alone.{' '}
                    <button type="button" disabled={busy} onClick={() => void backToAutomatic()} className={`${smallBtn} py-1! text-danger`}>
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
                <div className="grid grid-cols-2 gap-1.5 rounded-lg neu-inset p-1">
                  {(['month', 'permanent'] as const).map((v) => (
                    <button
                      key={v}
                      type="button"
                      role="radio"
                      aria-checked={applyTo === v}
                      onClick={() => setApplyTo(v)}
                      className={`rounded-md py-2 text-sm font-medium ${applyTo === v ? 'neu-sm text-accent' : 'text-ink-soft'}`}
                    >
                      {v === 'month' ? 'This month only' : 'From now on'}
                    </button>
                  ))}
                </div>
              </fieldset>
            )}

            {type === 'Variable' && (
              <fieldset className="mt-4">
                <legend className="text-sm font-medium text-ink-soft">Priority</legend>
                <div className="mt-2 grid grid-cols-3 gap-1.5 rounded-lg neu-inset p-1">
                  {(['High', 'Medium', 'Low'] as const).map((p) => (
                    <button
                      key={p}
                      type="button"
                      role="radio"
                      aria-checked={priority === p}
                      onClick={() => setPriority(p)}
                      className={`rounded-md px-1 py-2 text-sm font-medium ${priority === p ? 'neu-sm text-accent' : 'text-ink-soft'}`}
                    >
                      {priorityLabels[p]}
                    </button>
                  ))}
                </div>
                <p className="mt-2 text-xs text-ink-soft">
                  When money is short, Nice to have is trimmed first, then Important, then Must have.
                </p>
              </fieldset>
            )}

            {error && (
              <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
                {error}
              </p>
            )}


            {!isNew && (
              <>
                <p className="mt-6 px-1 text-sm font-semibold text-ink-soft">Options</p>
                <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
                  {onMove && (
                    <li className="px-4 py-2">
                      <div className="flex items-center justify-between gap-3">
                        <span className="text-sm font-medium">Reorder</span>
                        <span className="flex gap-1">
                          <button type="button" disabled={busy} onClick={() => void reorder(-1)} className={`${smallBtn} py-1!`} aria-label="Move up">
                            ↑ Up
                          </button>
                          <button type="button" disabled={busy} onClick={() => void reorder(1)} className={`${smallBtn} py-1!`} aria-label="Move down">
                            ↓ Down
                          </button>
                        </span>
                      </div>
                      {moved && (
                        <p key={moved} role="status" className="mt-1 text-xs text-good">
                          ✓ {moved}
                        </p>
                      )}
                    </li>
                  )}
                  {self && (
                    <li>
                      <button type="button" disabled={busy} onClick={() => setPart('merge')} className={action}>
                        <span>
                          Merge into another…
                          <span className="block text-xs font-normal text-ink-soft">Combine it with another {listedType(self) === 'FixedBill' ? 'bill' : 'spending category'}: transactions and limits together.</span>
                        </span>
                        <Chevron />
                      </button>
                    </li>
                  )}
                  <li>
                    <button type="button" disabled={busy} onClick={() => void archive()} className={action}>
                      <span>
                        Archive
                        <span className="block text-xs font-normal text-ink-soft">Out of your budget, history kept. You can restore it.</span>
                      </span>
                      <Chevron />
                    </button>
                  </li>
                  {self && (
                    <li>
                      <button type="button" disabled={busy} onClick={() => setPart('delete')} className={`${action} text-danger`}>
                        <span>
                          Delete…
                          <span className="block text-xs font-normal text-ink-soft">Gone for good. You pick where its transactions go.</span>
                        </span>
                        <Chevron />
                      </button>
                    </li>
                  )}
                </ul>
              </>
            )}
            <div className="mt-5 flex gap-3">
              <button type="button" onClick={onClose} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-danger">
                Cancel
              </button>
              <button type="submit" disabled={busy} className="flex-1 rounded-2xl bg-primary py-3 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-50">
                {busy ? 'Saving…' : isNew ? 'Add' : 'Save'}
              </button>
            </div>
          </>
        )}
      </form>
    </div>
  )
}

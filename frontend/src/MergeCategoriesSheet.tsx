import { useState, type FormEvent } from 'react'
import { budgetEditApi, type Category } from './api'
import { formatCents } from './format'

const groupLabels: { type: Category['type']; label: string }[] = [
  { type: 'Variable', label: 'Spending' },
  { type: 'FixedBill', label: 'Bills' },
  { type: 'DebtPayment', label: 'Debt payments' },
  { type: 'SavingsTransfer', label: 'Savings' },
]

/**
 * Combine categories into one (DECISIONS.md 144): pick the ones to fold together and name the result. The name can be
 * new ("Food"), one of the picked ones, or a category you already have (the others go into it). Their transactions,
 * rules and limits move over, and the app keeps filing their kind of spending there.
 */
export default function MergeCategoriesSheet({
  categories,
  onClose,
  onMerged,
}: {
  categories: Category[]
  onClose: () => void
  onMerged: (message: string) => void
}) {
  const [picked, setPicked] = useState<string[]>([])
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const trimmed = name.trim()
  // A name you already use (and didn't tick): the ticked ones go into that category.
  const existing = categories.find((c) => c.name.toLowerCase() === trimmed.toLowerCase())
  const into = existing && !picked.includes(existing.id) ? existing : undefined
  const chosen = categories.filter((c) => picked.includes(c.id))
  const total = chosen.reduce((sum, c) => sum + c.monthlyLimitCents, into?.monthlyLimitCents ?? 0)
  const enough = into ? chosen.length >= 1 : chosen.length >= 2 || (chosen.length === 1 && existing === undefined)
  const ready = trimmed.length > 0 && enough

  function toggle(id: string) {
    setPicked((p) => (p.includes(id) ? p.filter((x) => x !== id) : [...p, id]))
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!ready) return
    const names = chosen.map((c) => c.name).join(', ')
    if (!window.confirm(`Combine ${names} into ${into?.name ?? trimmed}? Their transactions move over. This can't be undone.`)) return
    setBusy(true)
    setError(null)
    try {
      const r = await budgetEditApi.mergeCategories(picked, into ? { intoId: into.id } : { newName: trimmed })
      onMerged(`Combined into ${r.name}${r.movedTransactions > 0 ? `: ${r.movedTransactions} transactions moved` : ''}.`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not combine them.')
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="merge-sheet-h"
        onSubmit={submit}
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-white p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="merge-sheet-h" className="text-lg font-semibold">
          Combine categories
        </h2>
        <p className="mt-1 text-sm text-slate-500">Tick the ones to fold together, then name the category they become.</p>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Combine into</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={100}
            placeholder="A new name, like Food"
            className="mt-1 block w-full rounded-xl border border-slate-300 bg-white px-4 py-3 text-base"
          />
          {into && <span className="mt-1 block text-xs text-slate-500">You already have {into.name}: the ones you tick go into it.</span>}
        </label>

        {groupLabels.map((g) => {
          const list = categories.filter((c) => c.type === g.type && c.id !== into?.id)
          if (list.length === 0) return null
          return (
            <fieldset key={g.type} className="mt-4">
              <legend className="text-xs font-semibold tracking-wide text-slate-500 uppercase">{g.label}</legend>
              <ul className="mt-1 divide-y divide-slate-100 rounded-xl ring-1 ring-slate-200">
                {list.map((c) => (
                  <li key={c.id}>
                    <label className="flex cursor-pointer items-center gap-3 px-4 py-3">
                      <input type="checkbox" checked={picked.includes(c.id)} onChange={() => toggle(c.id)} className="size-5 accent-slate-900" />
                      <span className="flex-1 text-sm">{c.name}</span>
                      <span className="text-xs text-slate-500 tabular-nums">{formatCents(c.monthlyLimitCents)}</span>
                    </label>
                  </li>
                ))}
              </ul>
            </fieldset>
          )
        })}

        {ready && (
          <p className="mt-4 rounded-xl bg-slate-50 px-4 py-3 text-sm">
            <span className="font-semibold">{into?.name ?? trimmed}</span> gets a {formatCents(total)} monthly limit and everything filed under{' '}
            {chosen.map((c) => c.name).join(', ')}.
          </p>
        )}
        {error && (
          <p role="alert" className="mt-3 text-sm text-red-600">
            {error}
          </p>
        )}

        <div className="mt-5 grid grid-cols-2 gap-3">
          <button type="button" onClick={onClose} className="rounded-xl bg-white py-3 text-sm font-medium ring-1 ring-slate-200">
            Cancel
          </button>
          <button type="submit" disabled={!ready || busy} className="rounded-xl bg-slate-900 py-3 text-sm font-semibold text-white disabled:opacity-40">
            {busy ? 'Combining…' : 'Combine'}
          </button>
        </div>
      </form>
    </div>
  )
}

import { useEffect, useState } from 'react'
import { budgetEditApi, type AutoCategorizeResult, type Category, type CategoryDiscovery } from './api'
import { formatCents } from './format'

const typeLabels: Record<Category['type'], string> = {
  Variable: 'Spending',
  FixedBill: 'Bill',
  DebtPayment: 'Debt payment',
  SavingsTransfer: 'Savings',
}

/**
 * Builds categories from the user's own transactions instead of a fixed starter list (DECISIONS.md 80):
 * shows what the server found, with the reason and example merchants for each, plus the categories the user
 * has but never uses. The user picks; nothing is created or hidden until they apply.
 */
export default function CategorySetup({ firstTime, onDone, onCancel }: { firstTime: boolean; onDone: (r: AutoCategorizeResult) => void; onCancel?: () => void }) {
  const [d, setD] = useState<CategoryDiscovery | null>(null)
  const [create, setCreate] = useState<Set<string>>(new Set())
  const [hide, setHide] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    budgetEditApi
      .discoverCategories()
      .then((r) => {
        setD(r)
        setCreate(new Set(r.proposed.filter((p) => p.status !== 'exists').map((p) => p.name)))
        setHide(new Set(r.unused.map((u) => u.id)))
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not look at your history.'))
  }, [])

  if (error && !d) return <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">{error}</p>
  if (!d) return <p className="mt-10 text-center text-ink-faint">Looking at your transactions…</p>

  const toAdd = d.proposed.filter((p) => p.status !== 'exists')
  const kept = d.proposed.filter((p) => p.status === 'exists')
  const toggle = <T,>(set: Set<T>, value: T) => {
    const next = new Set(set)
    if (next.has(value)) next.delete(value)
    else next.add(value)
    return next
  }

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      const r = await budgetEditApi.applyDiscovery(
        toAdd.filter((p) => create.has(p.name)).map((p) => ({ name: p.name, type: p.type, isDiscretionary: p.isDiscretionary, merchantPattern: p.merchantPattern, replaces: p.replaces })),
        [...hide],
      )
      onDone(r)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save.')
      setBusy(false)
    }
  }

  const nothingFound = d.proposed.length === 0
  const changes = toAdd.filter((p) => create.has(p.name)).length + hide.size

  return (
    <div className="pb-6">
      <h2 className="mt-2 text-base font-semibold">{firstTime ? 'Set up your categories' : 'Categories from your history'}</h2>
      <p className="mt-1 text-sm text-ink-soft">
        {nothingFound
          ? 'There isn’t enough history yet to find your categories. Import a few months of CSVs or connect a bank, then come back.'
          : `From your last ${d.monthsAnalyzed} ${d.monthsAnalyzed === 1 ? 'month' : 'months'} of transactions. Only what you actually spend on is listed; you can add, rename or hide categories any time.`}
      </p>

      {toAdd.length > 0 && (
        <section className="mt-4" aria-labelledby="setup-add">
          <h3 id="setup-add" className="text-sm font-semibold text-ink-soft">
            {firstTime ? 'Categories that fit your spending' : 'Worth adding'}
          </h3>
          <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
            {toAdd.map((p) => (
              <li key={p.name}>
                <label className="flex cursor-pointer gap-3 px-4 py-3">
                  <input
                    type="checkbox"
                    checked={create.has(p.name)}
                    onChange={() => setCreate(toggle(create, p.name))}
                    className="mt-1 h-4 w-4 shrink-0 accent-primary"
                  />
                  <span className="min-w-0 flex-1">
                    <span className="flex items-baseline justify-between gap-2">
                      <span className="truncate text-sm font-medium">
                        {p.name}
                        {p.custom && <span className="ml-2 rounded bg-indigo-50 px-1.5 py-0.5 text-[11px] font-medium text-indigo-700">Just for you</span>}
                        {p.status === 'hidden' && <span className="ml-2 text-xs font-normal text-ink-faint">(archived now)</span>}
                      </span>
                      <span className="shrink-0 text-sm tabular-nums text-ink-soft">~{formatCents(p.monthlyCents)}/mo</span>
                    </span>
                    <span className="mt-0.5 block text-xs text-ink-soft">
                      {typeLabels[p.type]} · {p.reason}
                    </span>
                    {p.examples.length > 0 && <span className="mt-0.5 block truncate text-xs text-ink-faint">{p.examples.join(', ')}</span>}
                  </span>
                </label>
              </li>
            ))}
          </ul>
        </section>
      )}

      {d.unused.length > 0 && (
        <section className="mt-6" aria-labelledby="setup-hide">
          <h3 id="setup-hide" className="text-sm font-semibold text-ink-soft">Categories you don’t use</h3>
          <p className="mt-1 text-xs text-ink-soft">Checked ones are archived. Archived categories keep their history and can come back from Budget → Categories → Archived.</p>
          <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
            {d.unused.map((u) => (
              <li key={u.id}>
                <label className="flex cursor-pointer gap-3 px-4 py-3">
                  <input type="checkbox" checked={hide.has(u.id)} onChange={() => setHide(toggle(hide, u.id))} className="mt-1 h-4 w-4 shrink-0 accent-primary" />
                  <span className="min-w-0 flex-1">
                    <span className="block text-sm font-medium">Hide {u.name}</span>
                    <span className="block text-xs text-ink-soft">{u.reason}</span>
                  </span>
                </label>
              </li>
            ))}
          </ul>
        </section>
      )}

      {kept.length > 0 && (
        <p className="mt-6 text-xs text-ink-soft">
          Already set up and in use: {kept.map((p) => p.name).join(', ')}.
        </p>
      )}
      {d.unmatched > 0 && (
        <p className="mt-2 text-xs text-ink-soft">
          {d.unmatched} {d.unmatched === 1 ? 'purchase' : 'purchases'} didn’t fit a pattern. You can file those yourself in To review.
        </p>
      )}

      {error && (
        <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {error}
        </p>
      )}
      <button
        type="button"
        onClick={apply}
        disabled={busy || (changes === 0 && !firstTime) || nothingFound}
        className="mt-6 w-full rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-50 text-sm"
      >
        {busy ? 'Sorting your transactions…' : changes === 0 ? 'Nothing to change' : firstTime ? 'Create these and sort my transactions' : 'Apply'}
      </button>
      {onCancel && (
        <button type="button" onClick={onCancel} className="mt-2 w-full py-2 text-sm text-danger">
          {firstTime ? 'Skip for now' : 'Back to the budget'}
        </button>
      )}
      <p className="mt-3 text-center text-xs text-ink-faint">Transactions are sorted into these right away, and each one waits in To review for you to confirm.</p>
    </div>
  )
}

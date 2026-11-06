import { useEffect, useState } from 'react'
import { budgetEditApi, type Category, type GeneratorProposal } from './api'
import { centsToInput, formatCents, formatDay, parseDollars } from './format'

const groups: { type: Category['type']; label: string }[] = [
  { type: 'Variable', label: 'Spending' },
  { type: 'FixedBill', label: 'Bills & subscriptions' },
  { type: 'DebtPayment', label: 'Debt payments' },
  { type: 'SavingsTransfer', label: 'Savings' },
]

/**
 * The budget generator (SPEC 6.1): proposes every limit from recent history with its reason, shows income
 * vs planned with an explicit remainder, lets the user edit everything, then applies it.
 * The proposal comes from the server; totals here just add up the numbers on screen.
 */
export default function BudgetPlanner({ onApplied }: { onApplied: (message: string) => void }) {
  const [p, setP] = useState<GeneratorProposal | null>(null)
  const [income, setIncome] = useState('')
  const [limits, setLimits] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    budgetEditApi
      .generator()
      .then((r) => {
        setP(r)
        setIncome(centsToInput(r.incomeCents))
        setLimits(Object.fromEntries(r.categories.map((c) => [c.categoryId, centsToInput(c.proposedCents)])))
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not build a proposal.'))
  }, [])

  if (error && !p) return <p role="alert" className="mt-4 rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">{error}</p>
  if (!p) return <p className="mt-10 text-center text-slate-400">Looking at your history…</p>

  const incomeCents = parseDollars(income)
  const parsed = p.categories.map((c) => ({ c, cents: parseDollars(limits[c.categoryId] ?? '') }))
  const invalid = incomeCents === null || parsed.some((x) => x.cents === null)
  const planned = parsed.reduce((sum, x) => sum + (x.cents ?? 0), 0)
  const remainder = (incomeCents ?? 0) - planned

  async function apply() {
    if (invalid) return
    setBusy(true)
    setError(null)
    try {
      await budgetEditApi.applyGenerator(incomeCents!, parsed.map((x) => ({ categoryId: x.c.categoryId, limitCents: x.cents! })))
      onApplied('Your budget is set. Limits apply from this month on.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save.')
      setBusy(false)
    }
  }

  return (
    <div className="pb-6">
      <p className="mt-2 text-sm text-slate-500">
        {p.monthsUsed === 0
          ? "There isn't a complete month of history yet, so your current limits are kept. Import a few months of CSVs or connect a bank, then come back."
          : `Based on your last ${p.monthsUsed} complete ${p.monthsUsed === 1 ? 'month' : 'months'} (${formatDay(p.months[0].start)} – ${formatDay(p.months[p.months.length - 1].end)}). Every number is editable.`}
      </p>
      {p.createdStarterCategories && (
        <p className="mt-3 rounded-xl bg-slate-100 px-4 py-3 text-sm text-slate-700">Started you with a standard set of categories. Rename or hide any later.</p>
      )}
      {p.uncategorizedCount > 0 && (
        <a href="#/budget/inbox" className="mt-3 block rounded-xl bg-amber-50 px-4 py-3 text-sm text-amber-900 ring-1 ring-amber-200">
          {p.uncategorizedCount} {p.uncategorizedCount === 1 ? 'purchase' : 'purchases'} in that period had no category and weren't counted. Categorize them for a better proposal ›
        </a>
      )}

      <section className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
        <label className="block">
          <span className="text-sm font-medium">Expected income this month</span>
          <MoneyInput value={income} onChange={setIncome} label="Expected income" />
          <span className="mt-1 block text-xs text-slate-500">{p.incomeExplanation}</span>
        </label>
        <dl className="mt-4 grid grid-cols-3 gap-2 text-center">
          <div className="rounded-xl bg-slate-50 p-2">
            <dt className="text-xs text-slate-500">Income</dt>
            <dd className="text-sm font-semibold tabular-nums">{formatCents(incomeCents ?? 0)}</dd>
          </div>
          <div className="rounded-xl bg-slate-50 p-2">
            <dt className="text-xs text-slate-500">Planned</dt>
            <dd className="text-sm font-semibold tabular-nums">{formatCents(planned)}</dd>
          </div>
          <div className={`rounded-xl p-2 ${remainder < 0 ? 'bg-red-50' : 'bg-slate-50'}`}>
            <dt className="text-xs text-slate-500">{remainder < 0 ? 'Over income' : 'Unallocated'}</dt>
            <dd className={`text-sm font-semibold tabular-nums ${remainder < 0 ? 'text-red-600' : ''}`}>{formatCents(Math.abs(remainder))}</dd>
          </div>
        </dl>
        {remainder < 0 && (
          <p role="alert" className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
            This plan spends more than you expect to earn. Trim some limits before using it.
          </p>
        )}
        {remainder > 0 && (
          <p className="mt-3 text-xs text-slate-500">
            The unallocated money isn't spendable. Consider putting it toward the rainy-day fund or card payments.
          </p>
        )}
      </section>

      {groups.map((g) => {
        const rows = parsed.filter((x) => x.c.type === g.type)
        if (rows.length === 0) return null
        return (
          <section key={g.type} className="mt-6" aria-labelledby={`plan-${g.type}`}>
            <h2 id={`plan-${g.type}`} className="text-sm font-semibold text-slate-700">
              {g.label}
            </h2>
            <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
              {rows.map(({ c, cents }) => (
                <li key={c.categoryId} className="px-4 py-3">
                  <div className="flex items-center justify-between gap-3">
                    <span className="min-w-0 flex-1 truncate text-sm font-medium">{c.name}</span>
                    <div className="w-32 shrink-0">
                      <MoneyInput
                        value={limits[c.categoryId] ?? ''}
                        onChange={(v) => setLimits({ ...limits, [c.categoryId]: v })}
                        label={`${c.name} limit`}
                        invalid={cents === null}
                        compact
                      />
                    </div>
                  </div>
                  <p className="mt-1 text-xs text-slate-500">
                    {c.explanation}
                    {c.currentLimitCents !== c.proposedCents && ` Currently ${formatCents(c.currentLimitCents)}.`}
                  </p>
                </li>
              ))}
            </ul>
          </section>
        )
      })}

      {error && (
        <p role="alert" className="mt-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
          {error}
        </p>
      )}
      <button
        type="button"
        onClick={apply}
        disabled={busy || invalid}
        className="mt-6 w-full rounded-xl bg-slate-900 py-3 font-semibold text-white disabled:opacity-50"
      >
        {busy ? 'Saving…' : remainder < 0 ? 'Use this budget anyway' : 'Use this budget'}
      </button>
    </div>
  )
}

function MoneyInput({ value, onChange, label, invalid, compact }: { value: string; onChange: (v: string) => void; label: string; invalid?: boolean; compact?: boolean }) {
  return (
    <div className={`flex items-center rounded-lg border bg-white px-3 ${invalid ? 'border-red-600' : 'border-slate-300'} ${compact ? '' : 'mt-1'}`}>
      <span className="text-sm text-slate-500">$</span>
      <input
        aria-label={label}
        aria-invalid={invalid}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        inputMode="decimal"
        className={`block w-full bg-transparent px-1 text-right tabular-nums outline-none ${compact ? 'py-1.5 text-sm' : 'py-2.5 text-base'}`}
      />
    </div>
  )
}

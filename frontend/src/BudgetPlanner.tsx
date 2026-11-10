import { useEffect, useState, type ReactNode } from 'react'
import { budgetEditApi, type Category, type GeneratorProposal, listedType } from './api'
import CategorySetup from './CategorySetup'
import { centsToInput, formatCents, formatDay, parseDollars } from './format'
import { smallBtn } from './ui'

const groups: { type: Category['type']; label: string }[] = [
  { type: 'Variable', label: 'Spending Categories' },
  { type: 'FixedBill', label: 'Bill Categories' },
  { type: 'DebtPayment', label: 'Debt payments' },
  { type: 'SavingsTransfer', label: 'Savings' },
]

/**
 * The budget generator (SPEC 6.1): proposes every limit from recent history with its reason, shows income
 * vs planned with an explicit remainder, lets the user edit everything, then applies it.
 * The proposal comes from the server; totals here just add up the numbers on screen.
 */
export default function BudgetPlanner({ onApplied, top }: { onApplied: (message: string) => void; top?: (p: GeneratorProposal) => ReactNode }) {
  const [p, setP] = useState<GeneratorProposal | null>(null)
  const [income, setIncome] = useState('')
  const [partner, setPartner] = useState('')
  const [limits, setLimits] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [setup, setSetup] = useState(false)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    budgetEditApi
      .generator()
      .then((r) => {
        setP(r)
        setSetup(r.needsCategorySetup)
        setIncome(centsToInput(r.incomeCents))
        setPartner(centsToInput(r.partnerCents))
        setLimits(Object.fromEntries(r.categories.map((c) => [c.categoryId, centsToInput(c.proposedCents)])))
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not build a proposal.'))
  }, [reload])

  if (error && !p) return <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">{error}</p>
  if (!p) return <p className="mt-10 text-center text-ink-faint">Looking at your history…</p>

  if (setup)
    return (
      <CategorySetup
        firstTime={p.needsCategorySetup}
        onCancel={p.needsCategorySetup ? undefined : () => setSetup(false)}
        onDone={() => {
          setP(null)
          setSetup(false)
          setReload((n) => n + 1)
        }}
      />
    )

  async function setAutomatic(on: boolean) {
    setBusy(true)
    setError(null)
    try {
      const s = await budgetEditApi.settings()
      await budgetEditApi.saveSettings(s.sweepTarget, s.sweepSavingsPercent, on)
      setP(null)
      setReload((n) => n + 1)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save.')
    } finally {
      setBusy(false)
    }
  }

  if (p.automatic) return <AutomaticView p={p} top={top?.(p)} busy={busy} error={error} onTurnOff={() => void setAutomatic(false)} />

  const incomeCents = parseDollars(income)
  const partnerCents = parseDollars(partner)
  const parsed = p.categories.map((c) => ({ c, cents: parseDollars(limits[c.categoryId] ?? '') }))
  const invalid = incomeCents === null || partnerCents === null || parsed.some((x) => x.cents === null)
  const planned = parsed.reduce((sum, x) => sum + (x.cents ?? 0), 0)
  const remainder = (incomeCents ?? 0) + (partnerCents ?? 0) - planned
  const waiting = p.categorization.filed + p.categorization.guesses

  async function apply() {
    if (invalid) return
    setBusy(true)
    setError(null)
    try {
      await budgetEditApi.applyGenerator(incomeCents!, partnerCents!, parsed.map((x) => ({ categoryId: x.c.categoryId, limitCents: x.cents! })))
      onApplied('Your budget is set. Limits apply from this month on.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save.')
      setBusy(false)
    }
  }

  return (
    <div className="pb-6">
      <p className="mt-2 text-sm text-ink-soft">
        {p.monthsUsed === 0
          ? "There isn't a complete month of history yet, so your current limits are kept. Import a few months of CSVs or connect a bank, then come back."
          : `Based on your last ${p.monthsUsed} complete ${p.monthsUsed === 1 ? 'month' : 'months'} (${formatDay(p.months[0].start)} – ${formatDay(p.months[p.months.length - 1].end)}). Every number is editable.`}
      </p>
      {waiting > 0 && (
        <p className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-ink-soft">
          {waiting} automatically sorted {waiting === 1 ? 'purchase is' : 'purchases are'} counted here but not confirmed yet
          {p.categorization.guesses > 0 && <> ({p.categorization.guesses} of them rough guesses)</>}. Confirming them in{' '}
          <a href="#/tasks/transactions" className={`${smallBtn} py-1! text-accent`}>
            To review
          </a>{' '}
          makes this proposal more accurate.
        </p>
      )}
      <section className="mt-3 rounded-3xl neu p-4 text-sm">
        <p className="font-semibold">The automatic budget is off</p>
        <p className="mt-1 text-xs text-ink-soft">
          Turned on, the budget builds and updates itself from your history, bills, debt plan, savings priorities and paycheck forecast, and squeezes spending when a month brings in less.
        </p>
        <button type="button" disabled={busy} onClick={() => void setAutomatic(true)} className="mt-2 rounded-lg bg-primary shadow-neu-sm px-3 py-1.5 text-xs font-semibold text-primary-ink disabled:opacity-50">
          Turn on the automatic budget
        </button>
      </section>
      <button type="button" onClick={() => setSetup(true)} className={`${smallBtn} mt-3 text-ink`}>
        Find categories from my history
      </button>
      {p.uncategorizedCount > 0 && (
        <a href="#/tasks/transactions" className="mt-3 block rounded-2xl neu-inset px-4 py-3 text-sm text-gold-ink">
          {p.uncategorizedCount} {p.uncategorizedCount === 1 ? 'purchase' : 'purchases'} in that period had no category and weren't counted. Categorize them for a better proposal ›
        </a>
      )}

      <section className="mt-4 rounded-3xl neu p-4">
        <label className="block">
          <span className="text-sm font-medium">Your expected income this month</span>
          <MoneyInput value={income} onChange={setIncome} label="Your expected income" />
          <span className="mt-1 block text-xs text-ink-soft">{p.incomeExplanation}</span>
        </label>
        <label className="mt-3 block">
          <span className="text-sm font-medium">{p.partnerName ? `From ${p.partnerName}` : 'From friends'}, expected</span>
          <MoneyInput value={partner} onChange={setPartner} label="Contributions from friends expected" />
          <span className="mt-1 block text-xs text-ink-soft">
            {p.partnerFromArrangements
              ? 'Their fixed contributions (More → Friends), counted once as offsets to the bills they go toward.'
              : p.partnerName
                ? `Median of what ${p.partnerName} sent each month. Set up a fixed contribution in More → Friends to use that instead.`
                : 'Add the name on a friend’s transfers in More → Friends so their money is tracked here instead of as your income.'}
          </span>
        </label>
        <dl className="mt-4 grid grid-cols-3 gap-2 text-center">
          <div className="rounded-xl neu-inset p-2">
            <dt className="text-xs text-ink-soft">Money in</dt>
            <dd className="text-sm font-semibold tabular-nums">{formatCents((incomeCents ?? 0) + (partnerCents ?? 0))}</dd>
          </div>
          <div className="rounded-xl neu-inset p-2">
            <dt className="text-xs text-ink-soft">Planned</dt>
            <dd className="text-sm font-semibold tabular-nums">{formatCents(planned)}</dd>
          </div>
          <div className={`rounded-xl p-2 ${remainder < 0 ? 'neu-inset' : 'neu-inset'}`}>
            <dt className="text-xs text-ink-soft">{remainder < 0 ? 'Over income' : 'Unallocated'}</dt>
            <dd className={`text-sm font-semibold tabular-nums ${remainder < 0 ? 'text-danger' : ''}`}>{formatCents(Math.abs(remainder))}</dd>
          </div>
        </dl>
        {remainder < 0 && (
          <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            This plan spends more than you expect to earn. Trim some limits before using it.
          </p>
        )}
        {remainder > 0 && (
          <p className="mt-3 text-xs text-ink-soft">
            The unallocated money isn't spendable. Consider putting it toward the rainy-day fund or card payments.
          </p>
        )}
      </section>

      {groups.map((g) => {
        const rows = parsed.filter((x) => listedType(x.c) === g.type)
        if (rows.length === 0) return null
        return (
          <section key={g.type} className="mt-6" aria-labelledby={`plan-${g.type}`}>
            <h2 id={`plan-${g.type}`} className="px-1 text-sm font-semibold text-ink-soft">
              {g.label}
            </h2>
            <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
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
                  <p className="mt-1 text-xs text-ink-soft">
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
        <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {error}
        </p>
      )}
      <button
        type="button"
        onClick={apply}
        disabled={busy || invalid}
        className="mt-6 w-full rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-50 text-sm"
      >
        {busy ? 'Saving…' : remainder < 0 ? 'Use this budget anyway' : 'Use this budget'}
      </button>
    </div>
  )
}

/**
 * With the automatic budget on: how this month's budget is built, read-only (there's nothing to confirm). The month's
 * money in, planned and any shortfall are in the one card above it (<paramref name="top"/>, DECISIONS.md 168).
 */
function AutomaticView({ p, top, busy, error, onTurnOff }: { p: GeneratorProposal; top: ReactNode; busy: boolean; error: string | null; onTurnOff: () => void }) {
  return (
    <div className="pb-6">
      {top}
      {groups.map((g) => {
        const rows = p.categories.filter((c) => listedType(c) === g.type)
        if (rows.length === 0) return null
        return (
          <section key={g.type} className="mt-6" aria-labelledby={`auto-${g.type}`}>
            <h2 id={`auto-${g.type}`} className="px-1 text-sm font-semibold text-ink-soft">
              {g.label}
            </h2>
            <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
              {rows.map((c) => (
                <li key={c.categoryId} className="px-4 py-3">
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="min-w-0 flex-1 truncate text-sm font-medium">{c.name}</span>
                    <span className="shrink-0 text-sm font-semibold tabular-nums">{formatCents(c.proposedCents)}</span>
                  </div>
                  <p className="mt-1 text-xs text-ink-soft">{c.explanation}</p>
                </li>
              ))}
            </ul>
          </section>
        )
      })}
      {error && (
        <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {error}
        </p>
      )}
      <button type="button" disabled={busy} onClick={onTurnOff} className="mt-6 w-full rounded-2xl neu py-3 text-sm font-semibold text-ink-soft disabled:opacity-50">
        Turn off the automatic budget
      </button>
      <p className="mt-2 text-center text-xs text-ink-soft">Off, the limits stay where they are until you change them or build a budget yourself.</p>
    </div>
  )
}

function MoneyInput({ value, onChange, label, invalid, compact }: { value: string; onChange: (v: string) => void; label: string; invalid?: boolean; compact?: boolean }) {
  return (
    <div className={`flex items-center rounded-xl neu-inset px-3 ${invalid ? 'ring-2 ring-danger' : ''} ${compact ? '' : 'mt-1'}`}>
      <span className="text-sm text-ink-soft">$</span>
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

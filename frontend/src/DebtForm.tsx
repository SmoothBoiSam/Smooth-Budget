import { useState, type FormEvent, type ReactNode } from 'react'
import { debtApi, type Debt, type DebtFields } from './api'
import { centsToInput, formatCents, formatDay, ordinal, parseDollars } from './format'

/**
 * A card's or loan's payoff details, edited on its page in More → Accounts (DECISIONS.md 326): what it owes now (unless its
 * bank gives that), APR, minimum, due day, limit or original amount, where you started, and leaving it out of the plan. A
 * loan has a name here too; a card is named on its account. With no debt, it adds a loan.
 */
export function DebtForm({
  debt,
  onSaved,
  onCancel,
}: {
  debt: Debt | null
  onSaved: (message: string, id: string) => void
  onCancel?: () => void
}) {
  const isNew = debt === null
  const isCard = debt?.isCard ?? false
  const [name, setName] = useState(debt?.name ?? '')
  const [balance, setBalance] = useState(debt ? centsToInput(debt.balanceCents) : '')
  const [apr, setApr] = useState(debt?.aprBps != null ? String(debt.aprBps / 100) : '')
  const [minimum, setMinimum] = useState(debt?.minimumCents != null ? centsToInput(debt.minimumCents) : '')
  const [dueDay, setDueDay] = useState(debt?.dueDay != null ? String(debt.dueDay) : '')
  const [limit, setLimit] = useState(debt?.creditLimitCents != null ? centsToInput(debt.creditLimitCents) : '')
  const [starting, setStarting] = useState(debt && debt.startingBalanceCents > 0 ? centsToInput(debt.startingBalanceCents) : '')
  const [hidden, setHidden] = useState(debt?.isHidden ?? false)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const balanceEditable = !debt?.balanceFromBank

  function fields(): DebtFields | string {
    const aprNum = apr.trim() === '' ? null : Number(apr.replace('%', ''))
    if (aprNum !== null && (!Number.isFinite(aprNum) || aprNum < 0 || aprNum > 100)) return 'Enter the APR as a percentage, like 24.99.'
    const min = minimum.trim() === '' ? null : parseDollars(minimum)
    if (minimum.trim() !== '' && min === null) return 'Enter the minimum payment in dollars, like 35 or 35.00.'
    const bal = balanceEditable ? parseDollars(balance) : null
    if (balanceEditable && bal === null) return 'Enter what you owe in dollars.'
    const day = dueDay.trim() === '' ? null : Number(dueDay)
    if (day !== null && (!Number.isInteger(day) || day < 1 || day > 31)) return 'The due day is a day of the month, 1 to 31.'
    const lim = limit.trim() === '' ? null : parseDollars(limit)
    if (limit.trim() !== '' && lim === null) return 'Enter the limit in dollars.'
    const start = starting.trim() === '' ? null : parseDollars(starting)
    if (starting.trim() !== '' && start === null) return 'Enter the starting balance in dollars.'
    if (!name.trim()) return 'Give it a name.'
    return {
      name: name.trim(),
      balanceCents: bal,
      aprBps: aprNum === null ? null : Math.round(aprNum * 100),
      minimumCents: min,
      dueDay: day,
      creditLimitCents: lim,
      startingBalanceCents: start,
      isHidden: hidden,
    }
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    const f = fields()
    if (typeof f === 'string') return setErr(f)
    setBusy(true)
    setErr(null)
    try {
      if (isNew) {
        const { id } = await debtApi.add(f)
        onSaved(`Added ${f.name}.`, id)
      } else {
        await debtApi.update(debt.id, f)
        onSaved('Saved.', debt.id)
      }
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : 'Could not save.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={save} className="px-5 pt-1 pb-5">
      {!isNew && debt.fromBank && <p className="mt-3 text-xs text-ink-soft">APR and minimum came from your bank. Change them if they’re off.</p>}
      {!isCard && (
        <Field label="Name">
          <input value={name} onChange={(e) => setName(e.target.value)} className={inputCls} placeholder="e.g. Car loan" />
        </Field>
      )}
      {balanceEditable ? (
        <Field label="What you owe now">
          <Money value={balance} onChange={setBalance} label="What you owe now" />
          {isCard && (
            <p className="mt-1 text-xs text-ink-soft">
              {debt!.typedOn && debt!.typedBalanceCents !== null
                ? `You typed ${formatCents(debt!.typedBalanceCents)} on ${formatDay(debt!.typedOn)}. Since then it follows your payments to this card and what its uploads show you charged. Type what the card’s own app says to correct it.`
                : 'This card isn’t linked to a bank. Type what its own app says you owe; after that it follows your payments to it and what its uploads show you charged.'}
            </p>
          )}
        </Field>
      ) : (
        <p className="mt-4 text-sm text-ink-soft">
          Owed now: <span className="font-semibold">{formatCents(debt!.balanceCents)}</span> <span className="text-xs text-ink-faint">(from your bank)</span>
        </p>
      )}
      <div className="grid grid-cols-2 gap-3">
        <Field label="APR">
          <div className={`${inputCls} flex items-center`}>
            <input value={apr} onChange={(e) => setApr(e.target.value)} inputMode="decimal" aria-label="APR percent" placeholder="24.99" className="w-full bg-transparent text-right outline-none" />
            <span className="pl-1 text-sm text-ink-soft">%</span>
          </div>
        </Field>
        <Field label="Minimum payment">
          <Money value={minimum} onChange={setMinimum} label="Minimum payment" />
        </Field>
        <Field label="Due day of the month">
          <select aria-label="Due day of the month" value={dueDay} onChange={(e) => setDueDay(e.target.value)} className={inputCls}>
            <option value="">Not set</option>
            {Array.from({ length: 30 }, (_, i) => i + 1).map((d) => (
              <option key={d} value={String(d)}>
                {ordinal(d)}
              </option>
            ))}
            {/* Stored as 31: the 31st where there is one, otherwise the 30th, 29th or 28th. */}
            <option value="31">Last day of the month</option>
          </select>
        </Field>
        <Field label={isCard ? 'Credit limit' : 'Original amount'}>
          <Money value={limit} onChange={setLimit} label={isCard ? 'Credit limit' : 'Original amount'} />
        </Field>
      </div>
      <Field label="What you owed when you started tracking">
        <Money value={starting} onChange={setStarting} label="Starting balance" />
      </Field>
      {!isNew && (
        <label className="mt-4 flex items-center gap-3 rounded-xl neu-inset px-4 py-3">
          <input type="checkbox" checked={hidden} onChange={(e) => setHidden(e.target.checked)} className="size-5" />
          <span className="text-sm">
            Leave out of the plan
            <span className="block text-xs text-ink-soft">For a card you pay in full every month.</span>
          </span>
        </label>
      )}

      {err && (
        <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {err}
        </p>
      )}
      <div className="mt-5 flex gap-3">
        {onCancel && (
          <button type="button" onClick={onCancel} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-danger">
            Cancel
          </button>
        )}
        <button type="submit" disabled={busy} className="flex-1 rounded-2xl bg-primary py-3 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-50">
          {busy ? 'Saving…' : isNew ? 'Add loan' : 'Save'}
        </button>
      </div>
    </form>
  )
}

const inputCls = 'mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base'

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="mt-4 block">
      <span className="text-sm font-medium text-ink-soft">{label}</span>
      {children}
    </label>
  )
}

function Money({ value, onChange, label }: { value: string; onChange: (v: string) => void; label: string }) {
  return (
    <div className={`${inputCls} flex items-center`}>
      <span className="text-sm text-ink-soft">$</span>
      <input aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} inputMode="decimal" className="w-full bg-transparent px-1 text-right tabular-nums outline-none" />
    </div>
  )
}

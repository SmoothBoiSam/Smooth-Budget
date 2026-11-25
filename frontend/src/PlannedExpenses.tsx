import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ask } from './confirm'
import { plannedApi, type PlannedExpense } from './api'
import { centsToInput, formatCents, formatDay, parseDollars } from './format'
import { DateField } from './ui'

/**
 * Budget settings → big expenses coming up (DECISIONS.md 327): each paycheck before one is due keeps part of it in checking,
 * which leaves less for extra card payments and savings until then. In the month it's paid, its own bill line covers it.
 */
export default function PlannedExpenses({ today, onChanged }: { today: string; onChanged: () => void }) {
  const [list, setList] = useState<PlannedExpense[] | null>(null)
  const [editing, setEditing] = useState<PlannedExpense | null | undefined>(undefined) // null = adding
  const load = useCallback(
    () =>
      plannedApi
        .list()
        .then(setList)
        .catch(() => setList((l) => l ?? [])),
    [],
  )
  useEffect(() => {
    void load()
  }, [load])

  if (list === null) return null
  function done() {
    setEditing(undefined)
    onChanged()
    void load()
  }

  return (
    <section aria-labelledby="planned-h" className="mt-4 rounded-2xl neu px-4 py-3">
      <h2 id="planned-h" className="text-sm font-medium">
        Big expenses coming up
      </h2>
      <p className="text-xs text-ink-soft">
        Each paycheck before one is due keeps part of it in checking. Until then, that leaves less for extra card payments and savings.
      </p>
      {list.length > 0 && (
        <ul className="mt-2 divide-y divide-ink/[0.07]">
          {list.map((e) =>
            editing?.id === e.id ? (
              <li key={e.id} className="py-2">
                <PlannedForm expense={e} today={today} onDone={done} onCancel={() => setEditing(undefined)} />
              </li>
            ) : (
              <li key={e.id}>
                <button type="button" onClick={() => setEditing(e)} className="block w-full py-2.5 text-left">
                  <span className="flex items-baseline justify-between gap-3">
                    <span className="truncate text-sm font-medium">{e.name}</span>
                    <span className="shrink-0 text-sm font-semibold tabular-nums">{formatCents(e.amountCents)}</span>
                  </span>
                  <span className="mt-0.5 block text-xs text-ink-soft">{line(e)}</span>
                </button>
              </li>
            ),
          )}
        </ul>
      )}
      {editing === null ? (
        <div className="mt-2">
          <PlannedForm expense={null} today={today} onDone={done} onCancel={() => setEditing(undefined)} />
        </div>
      ) : (
        <button type="button" onClick={() => setEditing(null)} className="mt-2 w-full rounded-2xl neu py-2.5 text-sm font-medium">
          + Set aside for something
        </button>
      )}
    </section>
  )
}

/** "Due Mar 25 · $300.00 set aside so far · $300.00 from each of 3 checks", or "Paid". */
function line(e: PlannedExpense) {
  const due = `Due ${formatDay(e.dueOn)}`
  if (e.paid) return `${due} · Paid`
  const n = e.checks.length
  const each = n === 0 ? '' : n === 1 ? ` · all from your ${formatDay(e.checks[0].payDate)} check` : ` · about ${formatCents(e.checks[0].cents)} from each of ${n} checks`
  return `${due} · ${formatCents(e.setAsideCents)} set aside so far${each}${e.spentCents > 0 ? ` · ${formatCents(e.spentCents)} paid` : ''}`
}

function PlannedForm({ expense, today, onDone, onCancel }: { expense: PlannedExpense | null; today: string; onDone: () => void; onCancel: () => void }) {
  const [name, setName] = useState(expense?.name ?? '')
  const [amount, setAmount] = useState(expense ? centsToInput(expense.amountCents) : '')
  const [due, setDue] = useState(expense?.dueOn ?? '')
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function save(e: FormEvent) {
    e.preventDefault()
    const cents = parseDollars(amount)
    if (!name.trim()) return setErr('Give it a name, like Car repair.')
    if (cents === null || cents <= 0) return setErr('Enter what it costs in dollars, like 1200.00.')
    if (!due) return setErr('Pick the day it’s due.')
    setBusy(true)
    setErr(null)
    try {
      if (expense) await plannedApi.update(expense.id, name.trim(), cents, due)
      else await plannedApi.add(name.trim(), cents, due)
      onDone()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : 'Could not save it.')
    } finally {
      setBusy(false)
    }
  }

  async function remove() {
    if (!expense) return
    if (!(await ask(`Stop setting money aside for ${expense.name}?`, { yes: 'Remove', danger: true }))) return
    try {
      await plannedApi.remove(expense.id)
      onDone()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : 'Could not remove it.')
    }
  }

  return (
    <form onSubmit={save} className="space-y-2 rounded-xl neu-inset p-3">
      <input
        value={name}
        onChange={(e) => setName(e.target.value)}
        maxLength={60}
        placeholder="What it’s for, e.g. Car repair"
        aria-label="What it’s for"
        className="block w-full rounded-xl neu-inset px-3 py-2 text-base"
      />
      <div className="flex gap-2">
        <input
          value={amount}
          onChange={(e) => setAmount(e.target.value)}
          inputMode="decimal"
          placeholder="Cost, e.g. 1200.00"
          aria-label="What it costs"
          className="block w-0 min-w-0 flex-1 rounded-xl neu-inset px-3 py-2 text-base"
        />
        <DateField label="When it’s due" placeholder="Due date" value={due} min={today} onChange={setDue} />
      </div>
      <p className="text-xs text-ink-soft">File the charge under its own bill line, named after it, when it comes.</p>
      {err && (
        <p role="alert" className="text-sm text-danger">
          {err}
        </p>
      )}
      <div className="flex gap-3 pt-1">
        <button type="button" onClick={onCancel} className="flex-1 rounded-2xl neu py-2.5 text-sm font-semibold text-danger">
          Cancel
        </button>
        <button type="submit" disabled={busy} className="flex-1 rounded-2xl bg-primary py-2.5 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-50">
          {busy ? 'Saving…' : expense ? 'Save' : 'Set aside'}
        </button>
      </div>
      {expense && (
        <button type="button" onClick={() => void remove()} className="w-full py-1.5 text-xs font-medium text-danger">
          Stop setting money aside for it
        </button>
      )}
    </form>
  )
}

import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, savingsApi, type GoalFields, type PriorityStep, type SavingsGoal, type SavingsSummary } from './api'
import { useRemembered } from './offline'
import { Caret, ErrorNotice, Loading, Page, PageHeader } from './ui'
import { centsToInput, formatCents, formatDay, parseDollars } from './format'

const monthFmt = new Intl.DateTimeFormat('en-US', { month: 'short', year: 'numeric', timeZone: 'UTC' })
const formatMonth = (iso: string) => monthFmt.format(new Date(iso + 'T00:00:00Z'))
const stepLabel: Record<PriorityStep, { title: string; hint: string }> = {
  StarterFund: { title: 'Starter emergency fund', hint: 'Fill the rainy-day fund first' },
  Debt: { title: 'High-APR debt', hint: 'Extra to the card with the highest APR' },
  Savings: { title: 'Larger savings', hint: 'Your other goals' },
}

/**
 * The Savings tab (SPEC 4.5): what you've saved, each goal with progress from its account's balance and a projected
 * date at your monthly rate, the order money left over goes in, and a chart of your savings over the last year.
 */
export default function SavingsScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [s, setS] = useState<SavingsSummary | null>(null)
  const [editing, setEditing] = useState<SavingsGoal | null | undefined>(undefined) // null = adding
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )

  const load = useCallback(async () => {
    try {
      setS(await savingsApi.summary())
      setError(null)
    } catch (e) {
      fail(e)
    }
  }, [fail])

  useEffect(() => {
    void load()
  }, [load])

  async function act(action: () => Promise<unknown>, message: string) {
    try {
      await action()
      setNotice(message)
      setEditing(undefined)
      await load()
    } catch (e) {
      fail(e)
    }
  }

  function move(i: number, d: -1 | 1) {
    if (!s) return
    const order = [...s.priority]
    const j = i + d
    if (j < 0 || j >= order.length) return
    ;[order[i], order[j]] = [order[j], order[i]]
    void act(() => savingsApi.setPriority(order), 'Priority saved. The next budget you build follows it.')
  }

  return (
    <Page>
      <PageHeader title="Savings" back={{ href: '#/payday', label: 'Plan' }} />
      {notice && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!s && !error && <Loading />}

      {s && (
        <>
          <section aria-label="Total" className="mt-4 rounded-3xl neu p-5">
            <p className="text-sm text-ink-soft">You’ve saved</p>
            <p className="text-4xl font-bold tabular-nums tracking-tight">{formatCents(s.totalSavedCents)}</p>
            {s.accounts.length === 0 && (
              <p className="mt-2 text-xs text-ink-soft">No savings account yet. Add it in More → Bank connections, or import its CSV.</p>
            )}
          </section>

          {s.accounts.length > 0 && (
            <section aria-labelledby="accts-h" className="mt-4">
              <h2 id="accts-h" className="px-1 text-sm font-semibold text-ink-soft">
                Savings accounts
              </h2>
              <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
                {s.accounts.map((a) => (
                  <li key={a.id} className="flex items-center justify-between gap-3 px-4 py-3">
                    <span className="min-w-0">
                      <span className="block truncate text-sm font-medium">
                        {a.name}
                        {a.mask && <span className="text-ink-faint"> ••{a.mask}</span>}
                      </span>
                      <span className="block text-xs text-ink-soft">
                        {a.paceCents > 0 ? `About ${formatCents(a.paceCents)} a month going in lately` : 'No regular deposits lately'}
                        {a.balanceFromBank ? ' · from your bank' : ''}
                      </span>
                    </span>
                    {a.balanceFromBank ? (
                      <span className="shrink-0 text-sm font-semibold tabular-nums">{formatCents(a.balanceCents)}</span>
                    ) : (
                      <BalanceEditor
                        cents={a.balanceCents}
                        onSave={(cents) => act(() => savingsApi.setBalance(a.id, cents), `${a.name} is now ${formatCents(cents)}.`)}
                      />
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )}

          <TransfersCard s={s} />

          <section aria-labelledby="goals-h" className="mt-6">
            <h2 id="goals-h" className="px-1 text-sm font-semibold text-ink-soft">
              Goals
            </h2>
            <ul className="mt-2 space-y-2">
              {s.goals.map((g) => (
                <li key={g.id}>
                  <GoalCard g={g} onOpen={() => setEditing(g)} />
                </li>
              ))}
            </ul>
            <button type="button" onClick={() => setEditing(null)} className="mt-2 w-full rounded-2xl neu py-3 text-sm font-medium">
              + Add a goal
            </button>
          </section>

          {s.history.some((p) => p.balanceCents !== 0) && <SavingsChart points={s.history} />}

          <section aria-labelledby="prio-h" className="mt-6">
            <h2 id="prio-h" className="px-1 text-sm font-semibold text-ink-soft">
              Where money left over goes
            </h2>
            <p className="px-1 text-xs text-ink-faint">What’s left after spending, bills and minimums.</p>
            <ol className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
              {s.priority.map((p, i) => (
                <li key={p} className="flex items-center gap-3 px-4 py-2.5">
                  <span className="w-5 text-sm font-semibold tabular-nums text-ink-faint">{i + 1}</span>
                  <span className="min-w-0 flex-1">
                    <span className="block text-sm font-medium">{stepLabel[p].title}</span>
                    <span className="block text-xs text-ink-soft">{stepLabel[p].hint}</span>
                  </span>
                  <span className="flex shrink-0 gap-1">
                    <button type="button" aria-label={`Move ${stepLabel[p].title} up`} disabled={i === 0} onClick={() => move(i, -1)} className="rounded-lg px-2 py-1 text-ink-soft disabled:opacity-30">
                      ↑
                    </button>
                    <button type="button" aria-label={`Move ${stepLabel[p].title} down`} disabled={i === s.priority.length - 1} onClick={() => move(i, 1)} className="rounded-lg px-2 py-1 text-ink-soft disabled:opacity-30">
                      ↓
                    </button>
                  </span>
                </li>
              ))}
            </ol>
          </section>

        </>
      )}

      {editing !== undefined && s && (
        <GoalSheet
          goal={editing}
          accounts={s.accounts}
          onClose={() => setEditing(undefined)}
          onSave={(f) => act(() => (editing ? savingsApi.updateGoal(editing.id, f) : savingsApi.addGoal(f)), editing ? 'Saved.' : `Added ${f.name}.`)}
          onRemove={editing && !editing.isStarter ? () => act(() => savingsApi.removeGoal(editing.id), `Removed ${editing.name}.`) : undefined}
        />
      )}
    </Page>
  )
}

function GoalCard({ g, onOpen }: { g: SavingsGoal; onOpen: () => void }) {
  return (
    <button type="button" onClick={onOpen} className="block w-full rounded-3xl neu p-4 text-left">
      <div className="flex items-baseline justify-between gap-3">
        <span className="truncate font-semibold">{g.name}</span>
        <span className="shrink-0 text-sm tabular-nums">
          <span className="font-semibold">{formatCents(g.savedCents)}</span> <span className="text-ink-soft">of {formatCents(g.targetCents)}</span>
        </span>
      </div>
      <div className="mt-2 h-2 overflow-hidden rounded-full neu-inset" aria-hidden>
        <div className={`h-full rounded-full ${g.reached ? 'bg-good' : 'bg-gold'}`} style={{ width: `${g.percent}%` }} />
      </div>
      <p className="mt-1.5 text-xs text-ink-soft">
        {g.reached ? (
          <span className="font-medium text-good">Reached</span>
        ) : g.projectedDate ? (
          <>
            On track for <span className="font-medium text-ink-soft">{formatMonth(g.projectedDate)}</span> at {formatCents(g.monthlyCents)} a month
            {g.monthlySource === 'pace' ? ' (your pace lately)' : ''}
          </>
        ) : (
          <span className="text-gold-ink">Add a monthly amount to see when you’ll get there</span>
        )}
        {g.accountName ? ` · in ${g.accountName}` : ' · tracked by hand'}
      </p>
    </button>
  )
}

function BalanceEditor({ cents, onSave }: { cents: number; onSave: (cents: number) => void }) {
  const [open, setOpen] = useState(false)
  const [value, setValue] = useState(centsToInput(cents))
  if (!open)
    return (
      <button type="button" onClick={() => setOpen(true)} className="shrink-0 text-sm font-semibold tabular-nums underline decoration-dotted">
        {formatCents(cents)}
      </button>
    )
  const parsed = parseDollars(value)
  return (
    <form
      className="flex shrink-0 items-center gap-1"
      onSubmit={(e) => {
        e.preventDefault()
        if (parsed !== null) onSave(parsed)
      }}
    >
      <input aria-label="Balance" value={value} onChange={(e) => setValue(e.target.value)} inputMode="decimal" className="w-24 rounded-xl neu-inset px-2 py-1 text-right text-sm tabular-nums" />
      <button type="submit" disabled={parsed === null} className="rounded-lg bg-coral shadow-neu-sm px-2 py-1 text-xs font-semibold text-coral-ink disabled:opacity-40">
        Save
      </button>
    </form>
  )
}

/** Total in savings at the end of each month: one line (the title names it), hover or touch for a month. */
function SavingsChart({ points }: { points: { date: string; balanceCents: number }[] }) {
  const [hover, setHover] = useState<number | null>(null)
  const W = 340
  const H = 140
  const pad = { l: 44, r: 8, t: 10, b: 22 }
  const values = points.map((p) => Math.max(0, p.balanceCents))
  const top = niceCeil(Math.max(...values, 1))
  const x = (i: number) => pad.l + (i / (points.length - 1)) * (W - pad.l - pad.r)
  const y = (c: number) => pad.t + (1 - c / top) * (H - pad.t - pad.b)
  const line = values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join('')
  const area = `${line}L${x(points.length - 1).toFixed(1)},${y(0)}L${x(0)},${y(0)}Z`
  const h = hover === null ? null : points[hover]

  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    const box = e.currentTarget.getBoundingClientRect()
    const px = ((e.clientX - box.left) / box.width) * W
    const i = Math.round(((px - pad.l) / (W - pad.l - pad.r)) * (points.length - 1))
    setHover(Math.max(0, Math.min(points.length - 1, i)))
  }

  return (
    <figure className="mt-6 rounded-3xl neu p-4">
      <figcaption className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-semibold">Saved over the last year</span>
        <span className="text-xs tabular-nums text-ink-soft" aria-live="polite">
          {h ? `${formatMonth(h.date)}: ${formatCents(Math.max(0, h.balanceCents))}` : ''}
        </span>
      </figcaption>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        className="mt-2 w-full touch-none select-none"
        role="img"
        aria-label={`Savings went from ${formatCents(values[0])} to ${formatCents(values[values.length - 1])} over the last year.`}
        onPointerMove={onMove}
        onPointerDown={onMove}
        onPointerLeave={() => setHover(null)}
      >
        {[0, 0.5, 1].map((f) => (
          <g key={f}>
            <line x1={pad.l} x2={W - pad.r} y1={y(top * f)} y2={y(top * f)} className="stroke-slate-200" strokeWidth={1} />
            <text x={pad.l - 6} y={y(top * f) + 3} textAnchor="end" className="fill-slate-500 text-[10px] tabular-nums">
              {shortMoney(top * f)}
            </text>
          </g>
        ))}
        <path d={area} className="fill-emerald-600" fillOpacity={0.12} />
        <path d={line} className="stroke-emerald-600" strokeWidth={2} fill="none" strokeLinejoin="round" strokeLinecap="round" />
        {[0, points.length - 1].map((i) => (
          <text key={i} x={x(i)} y={H - 6} textAnchor={i === 0 ? 'start' : 'end'} className="fill-slate-500 text-[10px]">
            {formatMonth(points[i].date)}
          </text>
        ))}
        {h && hover !== null && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={pad.t} y2={y(0)} className="stroke-slate-400" strokeWidth={1} strokeDasharray="3 3" />
            <circle cx={x(hover)} cy={y(Math.max(0, h.balanceCents))} r={4} className="fill-emerald-600 stroke-white" strokeWidth={2} />
          </g>
        )}
      </svg>
    </figure>
  )
}

function niceCeil(cents: number) {
  const dollars = cents / 100
  const pow = 10 ** Math.floor(Math.log10(dollars))
  const step = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].map((m) => m * pow).find((v) => v >= dollars) ?? 10 * pow
  return step * 100
}

function shortMoney(cents: number) {
  const d = cents / 100
  return d >= 1000 ? `$${(d / 1000).toFixed(d % 1000 === 0 ? 0 : 1)}k` : `$${Math.round(d)}`
}

const inputCls = 'mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base'

function GoalSheet({
  goal,
  accounts,
  onClose,
  onSave,
  onRemove,
}: {
  goal: SavingsGoal | null
  accounts: SavingsSummary['accounts']
  onClose: () => void
  onSave: (f: GoalFields) => void
  onRemove?: () => void
}) {
  const [name, setName] = useState(goal?.name ?? '')
  const [target, setTarget] = useState(goal ? centsToInput(goal.targetCents) : '')
  const [accountId, setAccountId] = useState<string>(goal ? (goal.accountId ?? '') : (accounts[0]?.id ?? ''))
  const [saved, setSaved] = useState(goal?.manualSavedCents != null ? centsToInput(goal.manualSavedCents) : '')
  const [monthly, setMonthly] = useState(goal?.monthlyCentsSet != null ? centsToInput(goal.monthlyCentsSet) : '')
  const [err, setErr] = useState<string | null>(null)

  function submit(e: FormEvent) {
    e.preventDefault()
    const t = parseDollars(target)
    const m = monthly.trim() === '' ? null : parseDollars(monthly)
    const sv = accountId ? null : saved.trim() === '' ? 0 : parseDollars(saved)
    if (!name.trim()) return setErr('Give it a name.')
    if (t === null || t <= 0) return setErr('Enter the target, like 1000.')
    if (monthly.trim() !== '' && m === null) return setErr('Enter the monthly amount in dollars, or leave it empty to use your pace.')
    if (!accountId && sv === null) return setErr('Enter what you’ve saved so far in dollars.')
    onSave({ name: name.trim(), targetCents: t, accountId: accountId || null, manualSavedCents: sv, monthlyCents: m })
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="goal-h"
        onSubmit={submit}
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="goal-h" className="text-lg font-semibold">
          {goal ? goal.name : 'Add a goal'}
        </h2>
        {goal?.isStarter && <p className="text-xs text-ink-soft">Your starter emergency fund. It comes first unless you reorder.</p>}
        <label className="mt-4 block">
          <span className="text-sm font-medium text-ink-soft">Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Trip" className={inputCls} />
        </label>
        <label className="mt-4 block">
          <span className="text-sm font-medium text-ink-soft">Target</span>
          <Money value={target} onChange={setTarget} label="Target" />
        </label>
        <label className="mt-4 block">
          <span className="text-sm font-medium text-ink-soft">Progress comes from</span>
          <select value={accountId} onChange={(e) => setAccountId(e.target.value)} className={inputCls}>
            {accounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name} balance
              </option>
            ))}
            <option value="">I’ll track it by hand</option>
          </select>
        </label>
        {!accountId && (
          <label className="mt-4 block">
            <span className="text-sm font-medium text-ink-soft">Saved so far</span>
            <Money value={saved} onChange={setSaved} label="Saved so far" />
          </label>
        )}
        <label className="mt-4 block">
          <span className="text-sm font-medium text-ink-soft">Each month (optional)</span>
          <Money value={monthly} onChange={setMonthly} label="Monthly amount" />
          <span className="mt-1 block text-xs text-ink-soft">Leave empty to use your actual pace into its account.</span>
        </label>
        {err && (
          <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {err}
          </p>
        )}
        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-2xl neu py-3 font-semibold text-ink-soft text-sm">
            Cancel
          </button>
          <button type="submit" className="flex-1 rounded-2xl bg-coral shadow-neu-sm py-3 font-semibold text-coral-ink text-sm">
            Save
          </button>
        </div>
        {onRemove && (
          <button type="button" onClick={onRemove} className="mt-3 w-full py-2 text-sm font-medium text-danger">
            Remove this goal
          </button>
        )}
      </form>
    </div>
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

/** Money moved into savings, tracked from your accounts (DECISIONS.md 142): this month against the budget, and the latest moves. */
function TransfersCard({ s }: { s: SavingsSummary }) {
  const [open, setOpen] = useRemembered('savings.transfers', false)
  const list = s.transfers ?? []
  const pct = s.plannedCents > 0 ? Math.min(100, Math.round((s.thisMonthCents * 100) / s.plannedCents)) : 0
  return (
    <section aria-labelledby="moved-h" className="mt-6 rounded-3xl neu p-4">
      <h2 id="moved-h">
        <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} className="flex w-full items-baseline justify-between gap-3 text-left">
          <span className="flex items-center gap-1 text-sm font-semibold">
            <Caret open={open} />
            Moved to savings this month
          </span>
          <span className="text-sm font-semibold tabular-nums">
            {formatCents(s.thisMonthCents)}
            {s.plannedCents > 0 && <span className="font-normal text-ink-soft"> of {formatCents(s.plannedCents)}</span>}
          </span>
        </button>
      </h2>
      {s.plannedCents > 0 && (
        <div className="mt-2 h-2 overflow-hidden rounded-full neu-inset" aria-hidden>
          <div className="h-full rounded-full bg-good" style={{ width: `${pct}%` }} />
        </div>
      )}
      <p className="mt-1.5 text-xs text-ink-soft">{s.plannedCents > 0 ? 'Out of what this month’s budget plans for savings.' : 'Tracked from your accounts.'}</p>
      {open &&
        (list.length === 0 ? (
          <p className="mt-2 text-sm text-ink-soft">No transfers into savings seen yet.</p>
        ) : (
          <ul className="mt-2 divide-y divide-ink/[0.07] text-sm">
            {list.map((t, i) => (
              <li key={t.date + t.cents + i} className="flex items-center justify-between gap-3 py-2">
                <span className="min-w-0">
                  <span className="block truncate">{t.description}</span>
                  <span className="block text-xs text-ink-soft">
                    {formatDay(t.date)} · {t.account}
                  </span>
                </span>
                <span className="shrink-0 font-semibold tabular-nums text-good">+{formatCents(t.cents)}</span>
              </li>
            ))}
          </ul>
        ))}
    </section>
  )
}

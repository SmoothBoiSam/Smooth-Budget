import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, budgetApi, isNewFind, needsLook, recurringApi, type Category, type RecurringItem, type RecurringSummary, type RecurringUpdate } from './api'
import { ErrorNotice, Loading } from './ui'
import { formatCents, formatDay } from './format'

const every: Record<RecurringItem['frequency'], string> = {
  Weekly: 'a week',
  Biweekly: 'every 2 weeks',
  Monthly: 'a month',
  Quarterly: 'a quarter',
  Yearly: 'a year',
}

/**
 * Review → Bills (SPEC 4.7, DECISIONS.md 152), found from your transactions: what needs a look first (newly found
 * charges filed as spending, price changes, charges that stopped, possible duplicates), then monthly and annual totals
 * and every bill with its next expected date. Nothing here is entered by hand; you confirm, rename, move, or dismiss.
 */
export default function BillsSection({
  onSignedOut,
  onCount,
  mode,
}: {
  onSignedOut: () => void
  onCount: (needsLook: number) => void
  /** "cards": what needs a look, one at a time with its charges (DECISIONS.md 167); "list": everything at once. */
  mode: 'cards' | 'list'
}) {
  const [s, setS] = useState<RecurringSummary | null>(null)
  const [skipped, setSkipped] = useState<string[]>([])
  const [open, setOpen] = useState<RecurringItem | null>(null)
  const [categories, setCategories] = useState<Category[]>([])
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
      const [summary, cats] = await Promise.all([recurringApi.summary(), budgetApi.categories()])
      setS(summary)
      setCategories(cats)
      setError(null)
      onCount(summary.items.filter(needsLook).length)
      // A link to one bill (#/review/bills/<key>, from the Budget tab) opens it.
      const key = decodeURIComponent(window.location.hash.replace(/^#\/?review\/bills\/?/, ''))
      if (key) {
        setOpen(summary.items.find((i) => i.key === key) ?? null)
        window.history.replaceState(null, '', '#/review/bills')
      }
    } catch (e) {
      fail(e)
    }
  }, [fail, onCount])

  useEffect(() => {
    void load()
  }, [load])

  async function update(u: RecurringUpdate, message: string, moveTo?: string) {
    try {
      if (moveTo) await recurringApi.moveToCategory(u.key, moveTo)
      await recurringApi.update(u)
      setNotice(message)
      setOpen(null)
      await load()
    } catch (e) {
      fail(e)
    }
  }

  async function trackAsBill(i: RecurringItem) {
    try {
      const r = await recurringApi.trackAsBill(i.key)
      setNotice(`${r.name} is its own bill line now, and its charges moved there. Future charges follow it.`)
      setOpen(null)
      await load()
    } catch (e) {
      fail(e)
    }
  }

  const alerts = s?.items.filter(needsLook) ?? []
  const counted = (i: RecurringItem) => i.status !== 'possiblyCanceled' && i.status !== 'canceled'
  // Bills and subscriptions are one list (DECISIONS.md 143).
  const bills = s?.items ?? []
  const waiting = alerts.filter((i) => !skipped.includes(i.key))
  const current = mode === 'cards' ? waiting[0] : undefined

  return (
    <div>
      {mode === 'list' && <p className="mt-1 text-sm text-slate-500">Found from your transactions: the same store, a similar amount, on a regular schedule.</p>}

      {notice && (
        <p role="status" className="mt-3 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!s && !error && <Loading label="Looking through your transactions" />}

      {s && current && (
        <>
          <p className="mt-3 text-xs text-slate-500">
            <span className="font-semibold text-slate-700">{waiting.length} to look at</span>
            {skipped.length > 0 && (
              <>
                {' · '}
                <button type="button" onClick={() => setSkipped([])} className="underline">
                  show {skipped.length} skipped
                </button>
              </>
            )}
          </p>
          <BillCard
            key={current.key}
            item={current}
            onUpdate={update}
            onOpen={() => setOpen(current)}
            onTrackAsBill={trackAsBill}
            onSkip={() => {
              setNotice(null)
              setSkipped((k) => [...k, current.key])
            }}
          />
        </>
      )}
      {s && mode === 'cards' && !current && (
        <p className="mt-3 rounded-xl bg-white px-4 py-3 text-sm text-slate-600 ring-1 ring-slate-200">
          {alerts.length === 0 ? 'Nothing needs a look. ' : `You skipped the rest (${skipped.length}). `}
          {skipped.length > 0 && (
            <button type="button" onClick={() => setSkipped([])} className="font-medium underline">
              Go through them again
            </button>
          )}
          {alerts.length === 0 && 'Every bill is below; tap one to see its charges.'}
        </p>
      )}

      {s && !current && (
        <>
          <dl className="mt-4 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
            <dt className="text-xs text-slate-500">Your bills cost</dt>
            <dd className="text-xl font-semibold tabular-nums">{formatCents(s.monthlyCents)} <span className="text-sm font-normal text-slate-500">a month</span></dd>
            <dd className="text-xs text-slate-500">{formatCents(s.annualCents)} a year</dd>
          </dl>

          {mode === 'list' && alerts.length > 0 && (
            <section aria-labelledby="alerts-h" className="mt-6">
              <h2 id="alerts-h" className="text-sm font-semibold text-slate-700">
                Needs a look
              </h2>
              <ul className="mt-2 space-y-2">
                {alerts.map((i) => (
                  <li key={i.key}>
                    <Alert item={i} onUpdate={update} onOpen={() => setOpen(i)} onTrackAsBill={trackAsBill} />
                  </li>
                ))}
              </ul>
            </section>
          )}

          <List title="All bills" items={bills} counted={counted} onOpen={setOpen} />

          {s.items.length === 0 && (
            <p className="mt-6 rounded-xl bg-white px-4 py-3 text-sm text-slate-500 ring-1 ring-slate-200">
              Nothing recurring yet. It takes about three charges (two for a yearly one) to spot a pattern.
            </p>
          )}

          {s.dismissed.length > 0 && (
            <details className="mt-6 text-sm">
              <summary className="cursor-pointer text-slate-500">Not recurring ({s.dismissed.length})</summary>
              <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
                {s.dismissed.map((i) => (
                  <li key={i.key} className="flex items-center justify-between gap-3 px-4 py-2.5">
                    <span className="truncate">{i.name}</span>
                    <button type="button" onClick={() => void update({ key: i.key, dismissed: false }, `${i.name} is back on the list.`)} className="shrink-0 text-xs font-medium underline">
                      Restore
                    </button>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      )}

      {open && <ItemSheet item={open} categories={categories} onClose={() => setOpen(null)} onUpdate={update} onTrackAsBill={trackAsBill} />}
    </div>
  )
}

function Alert({
  item: i,
  onUpdate,
  onOpen,
  onTrackAsBill,
}: {
  item: RecurringItem
  onUpdate: (u: RecurringUpdate, m: string) => void
  onOpen: () => void
  onTrackAsBill: (i: RecurringItem) => void
}) {
  const box = 'rounded-xl px-4 py-3 text-sm ring-1'
  if (isNewFind(i))
    return (
      <div className={`${box} bg-white text-slate-700 ring-slate-200`}>
        <p>
          <span className="font-semibold">{i.name}</span> looks recurring: {formatCents(i.amountCents)} {every[i.frequency]}
          {i.accountName && ` on ${i.accountName}`}, filed {i.categoryName ? `under ${i.categoryName}` : 'with no category'}.
        </p>
        <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs font-semibold">
          <button type="button" onClick={() => onTrackAsBill(i)} className="underline">
            Make it a bill line
          </button>
          {i.categoryName && (
            <button type="button" onClick={() => onUpdate({ key: i.key, confirmed: true }, `${i.name} stays under ${i.categoryName}.`)} className="underline">
              Keep it in {i.categoryName}
            </button>
          )}
          <button type="button" onClick={() => onUpdate({ key: i.key, dismissed: true }, `${i.name} won't show as recurring.`)} className="underline">
            It isn’t recurring
          </button>
        </div>
      </div>
    )
  if (i.status === 'priceUp' || i.status === 'priceDown')
    return (
      <div className={`${box} bg-amber-50 text-amber-900 ring-amber-200`}>
        <p>
          <span className="font-semibold">{i.name}</span> went {i.status === 'priceUp' ? 'up' : 'down'} from {formatCents(i.previousAmountCents!)} to{' '}
          <span className="font-semibold">{formatCents(i.amountCents)}</span>
          {i.priceChangedOn && ` on ${formatDay(i.priceChangedOn)}`}.
        </p>
        <div className="mt-2 flex gap-3 text-xs font-semibold">
          <button type="button" onClick={() => onUpdate({ key: i.key, acknowledgePrice: true }, `Noted the new price for ${i.name}.`)} className="underline">
            Got it
          </button>
          <button type="button" onClick={onOpen} className="underline">
            Details
          </button>
        </div>
      </div>
    )
  if (i.status === 'possiblyCanceled')
    return (
      <div className={`${box} bg-white text-slate-700 ring-slate-200`}>
        <p>
          <span className="font-semibold">{i.name}</span>: no charge since {formatDay(i.lastDate)}. It was expected {formatDay(i.nextExpected)}.
        </p>
        <div className="mt-2 flex gap-3 text-xs font-semibold">
          <button type="button" onClick={() => onUpdate({ key: i.key, canceled: true }, `Marked ${i.name} as canceled.`)} className="underline">
            I canceled it
          </button>
          <button type="button" onClick={() => onUpdate({ key: i.key, dismissed: true }, `${i.name} won't show as recurring.`)} className="underline">
            It wasn’t recurring
          </button>
        </div>
      </div>
    )
  return (
    <div className={`${box} bg-amber-50 text-amber-900 ring-amber-200`}>
      <p>
        <span className="font-semibold">{i.name}</span> may be charged twice: another {formatCents(i.amountCents)} charge from it repeats on the same schedule
        {i.accountName && ` (this one on ${i.accountName})`}.
      </p>
      <button type="button" onClick={onOpen} className="mt-2 text-xs font-semibold underline">
        Details
      </button>
    </div>
  )
}

/** A date with its year, since a bill's charges go back 18 months. */
function dayYear(iso: string) {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })
}

/** When it stopped (or when it's next due), then every charge with its amount, newest first (DECISIONS.md 167). */
function ChargeHistory({ item: i }: { item: RecurringItem }) {
  const [all, setAll] = useState(false)
  const charges = i.charges ?? []
  const shown = all ? charges : charges.slice(0, 6)
  const status =
    i.status === 'canceled'
      ? { text: `Canceled. Last charged ${dayYear(i.lastDate)}.`, cls: 'text-slate-700' }
      : i.status === 'possiblyCanceled'
        ? { text: `Looks stopped: last charged ${dayYear(i.lastDate)}, and the next was expected ${formatDay(i.nextExpected)}.`, cls: 'font-medium text-amber-700' }
        : { text: `Still going: next expected ${formatDay(i.nextExpected)}.`, cls: 'text-slate-700' }
  return (
    <section aria-label="Charges" className="mt-3">
      <p className={`text-xs ${status.cls}`}>{status.text}</p>
      {charges.length > 0 && (
        <>
          <p className="mt-2 text-xs text-slate-500">
            {charges.length} {charges.length === 1 ? 'charge' : 'charges'} since {dayYear(charges[charges.length - 1].date)}
          </p>
          <ul className="mt-1 divide-y divide-slate-100 rounded-xl bg-slate-50 px-3 text-sm">
            {shown.map((c, n) => {
              const older = charges[n + 1]
              const change = older && older.amountCents !== c.amountCents ? c.amountCents - older.amountCents : 0
              return (
                <li key={`${c.date}-${n}`} className="flex justify-between gap-3 py-1.5">
                  <span className="text-slate-600">{dayYear(c.date)}</span>
                  <span className="tabular-nums">
                    {formatCents(c.amountCents)}
                    {change !== 0 && (
                      <span className={`ml-1 text-xs ${change > 0 ? 'text-red-600' : 'text-emerald-700'}`}>
                        {change > 0 ? '↑' : '↓'} {formatCents(Math.abs(change))}
                      </span>
                    )}
                  </span>
                </li>
              )
            })}
          </ul>
          {charges.length > 6 && (
            <button type="button" onClick={() => setAll(!all)} className="mt-1 text-xs font-medium text-slate-600 underline">
              {all ? 'Show fewer' : `Show all ${charges.length}`}
            </button>
          )}
        </>
      )}
    </section>
  )
}

/** One bill that needs a look, with its charges, in Review's one-by-one view (DECISIONS.md 167). */
function BillCard({
  item: i,
  onUpdate,
  onOpen,
  onTrackAsBill,
  onSkip,
}: {
  item: RecurringItem
  onUpdate: (u: RecurringUpdate, m: string) => void
  onOpen: () => void
  onTrackAsBill: (i: RecurringItem) => void
  onSkip: () => void
}) {
  return (
    <article className="mt-2 rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="min-w-0 truncate text-lg font-semibold">{i.name}</h2>
        <span className="shrink-0 font-semibold tabular-nums">
          {formatCents(i.amountCents)} <span className="text-xs font-normal text-slate-500">{every[i.frequency]}</span>
        </span>
      </div>
      <p className="truncate text-xs text-slate-500">
        {[i.accountName, i.categoryName ?? 'No category'].filter(Boolean).join(' · ')}
      </p>
      <div className="mt-3">
        <Alert item={i} onUpdate={onUpdate} onOpen={onOpen} onTrackAsBill={onTrackAsBill} />
      </div>
      <ChargeHistory item={i} />
      <div className="mt-3 grid grid-cols-2 gap-2 text-sm font-medium">
        <button type="button" onClick={onOpen} className="rounded-xl bg-slate-100 py-2.5 text-slate-700">
          Edit
        </button>
        <button type="button" onClick={onSkip} className="rounded-xl bg-slate-100 py-2.5 text-slate-700">
          Skip for now
        </button>
      </div>
    </article>
  )
}

function List({ title, items, counted, onOpen }: { title: string; items: RecurringItem[]; counted: (i: RecurringItem) => boolean; onOpen: (i: RecurringItem) => void }) {
  if (items.length === 0) return null
  return (
    <section aria-labelledby={`list-${title}`} className="mt-6">
      <h2 id={`list-${title}`} className="text-sm font-semibold text-slate-700">
        {title}
      </h2>
      <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
        {items.map((i) => (
          <li key={i.key}>
            <button type="button" onClick={() => onOpen(i)} className={`block w-full px-4 py-3 text-left hover:bg-slate-50 ${counted(i) ? '' : 'opacity-60'}`}>
              <div className="flex items-baseline justify-between gap-3">
                <span className="truncate text-sm font-medium">{i.name}</span>
                <span className="shrink-0 text-sm tabular-nums">
                  {formatCents(i.amountCents)} <span className="text-xs text-slate-500">{every[i.frequency]}</span>
                </span>
              </div>
              <p className="mt-0.5 truncate text-xs text-slate-500">
                {i.status === 'canceled' ? 'Canceled' : i.status === 'possiblyCanceled' ? `Stopped? Last ${formatDay(i.lastDate)}` : `Next ${formatDay(i.nextExpected)}`}
                {i.accountName && ` · ${i.accountName}`}
                {i.categoryName && ` · ${i.categoryName}`}
                {!i.inBillCategory && counted(i) && <span className="text-amber-600"> · not a bill line yet</span>}
              </p>
            </button>
          </li>
        ))}
      </ul>
    </section>
  )
}

const categoryGroups: { type: Category['type']; label: string }[] = [
  { type: 'FixedBill', label: 'Bills' },
  { type: 'Variable', label: 'Spending' },
  { type: 'DebtPayment', label: 'Debt payments' },
  { type: 'SavingsTransfer', label: 'Savings' },
]

function ItemSheet({
  item: i,
  categories,
  onClose,
  onUpdate,
  onTrackAsBill,
}: {
  item: RecurringItem
  categories: Category[]
  onClose: () => void
  onUpdate: (u: RecurringUpdate, m: string, moveTo?: string) => void
  onTrackAsBill: (i: RecurringItem) => void
}) {
  const [name, setName] = useState(i.name)
  const [due, setDue] = useState(i.dueDay ? String(i.dueDay) : '')
  const [categoryId, setCategoryId] = useState(i.categoryId ?? '')
  const [err, setErr] = useState<string | null>(null)
  const moved = categoryId !== '' && categoryId !== (i.categoryId ?? '')

  function save(e: FormEvent) {
    e.preventDefault()
    const day = due.trim() === '' ? null : Number(due)
    if (day !== null && (!Number.isInteger(day) || day < 1 || day > 31)) return setErr('The due day is a day of the month, 1 to 31.')
    const label = name.trim() || i.detectedName
    const to = categories.find((c) => c.id === categoryId)?.name
    onUpdate(
      { key: i.key, name, confirmed: true, ...(day === null ? { clearDueDay: true } : { dueDay: day }) },
      moved ? `${label} is filed under ${to} now, past charges and future ones.` : `Saved ${label}.`,
      moved ? categoryId : undefined,
    )
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="bill-sheet-h"
        onSubmit={save}
        onClick={(e) => e.stopPropagation()}
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-white p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="bill-sheet-h" className="text-lg font-semibold">
          {i.name}
        </h2>
        <p className="text-sm text-slate-500">
          {formatCents(i.amountCents)} {every[i.frequency]} · {i.occurrences} charges in the last 18 months · last {formatDay(i.lastDate)}
          {i.accountName && ` on ${i.accountName}`}
        </p>
        {i.name !== i.detectedName && <p className="text-xs text-slate-400">Shows on your statement as {i.detectedName}.</p>}
        <ChargeHistory item={i} />

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} className="mt-1 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-base" />
        </label>

        {i.frequency === 'Monthly' && (
          <label className="mt-4 block">
            <span className="text-sm font-medium text-slate-700">Due day of the month</span>
            <input value={due} onChange={(e) => setDue(e.target.value)} inputMode="numeric" placeholder={`Last charged on the ${Number(i.lastDate.slice(8))}`} className="mt-1 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-base" />
          </label>
        )}

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Category</span>
          <select
            value={categoryId}
            onChange={(e) => setCategoryId(e.target.value)}
            className="mt-1 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-base"
          >
            {categoryId === '' && <option value="">No category</option>}
            {categoryGroups.map((g) => {
              const list = categories.filter((c) => c.type === g.type).sort((a, b) => a.sortOrder - b.sortOrder)
              return list.length === 0 ? null : (
                <optgroup key={g.type} label={g.label}>
                  {list.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.name}
                    </option>
                  ))}
                </optgroup>
              )
            })}
          </select>
          <span className="mt-1 block text-xs text-slate-500">
            {moved
              ? i.merchantWide
                ? 'Its past charges move there, and future ones go there too.'
                : 'Its past charges move there. This store bills more than one thing, so check new charges in Review.'
              : 'Pick another category to move its charges there.'}
          </span>
        </label>

        {!i.inBillCategory && !moved && (
          <div className="mt-4 rounded-xl bg-slate-50 px-4 py-3">
            <p className="text-sm">
              {i.categoryName ? `Its charges are filed under ${i.categoryName}` : 'Its charges have no category'}, so your budget treats it as everyday spending.
            </p>
            <button type="button" onClick={() => onTrackAsBill(i)} className="mt-2 rounded-full bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white">
              Make it its own bill line
            </button>
          </div>
        )}

        {err && (
          <p role="alert" className="mt-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
            {err}
          </p>
        )}
        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-xl bg-slate-100 py-3 font-semibold text-slate-700">
            Cancel
          </button>
          <button type="submit" className="flex-1 rounded-xl bg-slate-900 py-3 font-semibold text-white">
            Save
          </button>
        </div>
        <div className="mt-3 flex justify-between text-sm">
          {i.status === 'canceled' ? (
            <button type="button" onClick={() => onUpdate({ key: i.key, canceled: false }, `${i.name} is active again.`)} className="py-2 font-medium text-slate-600">
              Not canceled after all
            </button>
          ) : (
            <button type="button" onClick={() => onUpdate({ key: i.key, canceled: true }, `Marked ${i.name} as canceled.`)} className="py-2 font-medium text-slate-600">
              I canceled it
            </button>
          )}
          <button type="button" onClick={() => onUpdate({ key: i.key, dismissed: true }, `${i.name} won't show as recurring.`)} className="py-2 font-medium text-red-600">
            It’s not recurring
          </button>
        </div>
      </form>
    </div>
  )
}

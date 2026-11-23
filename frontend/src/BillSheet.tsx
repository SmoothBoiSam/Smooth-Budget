import { useState, type FormEvent } from 'react'
import { recurringApi, type Category, type RecurringItem, type RecurringUpdate } from './api'
import { dayYear, every, keepLookingMessage, price, type SheetPart } from './bills'
import { Chevron, sheetCls, smallBtn } from './ui'
import { formatCents, formatDay } from './format'

/**
 * A bill's pop-up (DECISIONS.md 197, 228), the same wherever a bill is tapped: Review → Bills and the bills under each
 * bill line on the Budget tab. It runs each choice itself and reports back with a message.
 */
export default function BillSheet({
  item,
  start = 'edit',
  items,
  categories,
  onClose,
  onDone,
  onError,
}: {
  item: RecurringItem
  start?: SheetPart
  /** Every bill, to pick "the same bill as…" from. */
  items: RecurringItem[]
  categories: Category[]
  onClose: () => void
  onDone: (message: string) => void
  onError: (e: unknown) => void
}) {
  async function run(action: () => Promise<string>) {
    try {
      onDone(await action())
    } catch (e) {
      onError(e)
    }
  }
  return (
    <ItemSheet
      key={`${item.key}-${start}`}
      item={item}
      start={start}
      items={items}
      categories={categories}
      onClose={onClose}
      onUpdate={(u, m, moveTo) =>
        void run(async () => {
          if (moveTo) await recurringApi.moveToCategory(u.key, moveTo)
          await recurringApi.update(u)
          return m
        })
      }
      onTrackAsBill={(i) =>
        void run(async () => {
          const r = await recurringApi.trackAsBill(i.key)
          return `${r.name} is a bill now, with its own line in your budget. Its charges moved there, and future ones follow.`
        })
      }
      onNotABill={(i, categoryId) =>
        void run(async () => {
          const r = await recurringApi.notABill(i.key, categoryId)
          return `${i.name} isn't a bill: its charges are spending under ${r.name} now, and nothing expects it.`
        })
      }
    />
  )
}

/** When it stopped (or when it's next due), then every charge with its amount, newest first (DECISIONS.md 167). */
export function ChargeHistory({ item: i }: { item: RecurringItem }) {
  const [all, setAll] = useState(false)
  const charges = i.charges ?? []
  const shown = all ? charges : charges.slice(0, 6)
  const status =
    i.status === 'canceled'
      ? { text: `Canceled. Last charged ${dayYear(i.lastDate)}.`, cls: 'text-ink-soft' }
      : i.status === 'possiblyCanceled'
        ? { text: `Looks stopped: last charged ${dayYear(i.lastDate)}, and the next was expected ${formatDay(i.nextExpected)}.`, cls: 'font-medium text-gold-ink' }
        : i.lookingSince && !i.found
          ? { text: `Looking for its next charge, even at a new price: none yet after ${dayYear(i.lastDate)}. Your budget still expects it.`, cls: 'text-ink-soft' }
          : {
              text: `Still going: next expected ${formatDay(i.nextExpected)}.${i.found ? ` Found ${i.found} newer charge${i.found === 1 ? '' : 's'} when you said it was still going.` : ''}`,
              cls: 'text-ink-soft',
            }
  return (
    <section aria-label="Charges" className="mt-4">
      <p className={`text-xs ${status.cls}`}>{status.text}</p>
      {charges.length > 0 && (
        <>
          <p className="mt-2 text-xs text-ink-soft">
            {charges.length} {charges.length === 1 ? 'charge' : 'charges'} since {dayYear(charges[charges.length - 1].date)}
          </p>
          <ul className="mt-2 divide-y divide-ink/[0.07] rounded-2xl neu-inset px-4 text-sm">
            {shown.map((c, n) => {
              const older = charges[n + 1]
              const change = older && older.amountCents !== c.amountCents ? c.amountCents - older.amountCents : 0
              return (
                <li key={`${c.date}-${n}`} className="flex justify-between gap-3 py-1.5">
                  <span className="text-ink-soft">{dayYear(c.date)}</span>
                  <span className="tabular-nums">
                    {formatCents(c.amountCents)}
                    {change !== 0 && (
                      <span className={`ml-1 text-xs ${change > 0 ? 'text-danger' : 'text-good'}`}>
                        {change > 0 ? '↑' : '↓'} {formatCents(Math.abs(change))}
                      </span>
                    )}
                  </span>
                </li>
              )
            })}
          </ul>
          {charges.length > 6 && (
            <button type="button" onClick={() => setAll(!all)} className={`${smallBtn} mt-2 text-accent`}>
              {all ? 'Show fewer' : `Show all ${charges.length}`}
            </button>
          )}
        </>
      )}
    </section>
  )
}


/**
 * The bill sheet (DECISIONS.md 197): every option for one bill, the same wherever it's opened (Review's cards, All
 * bills, a bill on the Budget tab). Edit its name, due day and category; say it's the same as another bill, or split
 * bills you joined; mark it canceled; or say it isn't a bill at all.
 */
function ItemSheet({
  item: i,
  start,
  items,
  categories,
  onClose,
  onUpdate,
  onTrackAsBill,
  onNotABill,
}: {
  item: RecurringItem
  start: SheetPart
  items: RecurringItem[]
  categories: Category[]
  onClose: () => void
  onUpdate: (u: RecurringUpdate, m: string, moveTo?: string) => void
  onTrackAsBill: (i: RecurringItem) => void
  onNotABill: (i: RecurringItem, categoryId: string) => void
}) {
  const [part, setPart] = useState<SheetPart>(start)
  const [name, setName] = useState(i.name)
  const [due, setDue] = useState(i.dueDay ? String(i.dueDay) : '')
  // Each bill is its own line (DECISIONS.md 279): no moving it into another one. Spending is "Not a bill".
  const spending = categories.filter((c) => c.type === 'Variable').sort((a, b) => a.sortOrder - b.sortOrder)
  const [spendId, setSpendId] = useState(spending.some((c) => c.id === i.categoryId) ? i.categoryId! : (spending[0]?.id ?? ''))
  const [err, setErr] = useState<string | null>(null)

  // Bills to join it with: ones sharing its first word first (another Hulu), then the rest by name.
  const word = i.name.trim().split(/\s+/)[0]?.toLowerCase() ?? ''
  const others = items
    .filter((o) => o.key !== i.key)
    .sort((a, b) => Number(b.name.toLowerCase().startsWith(word)) - Number(a.name.toLowerCase().startsWith(word)) || a.name.localeCompare(b.name))

  function save(e: FormEvent) {
    e.preventDefault()
    const day = due.trim() === '' ? null : Number(due)
    if (day !== null && (!Number.isInteger(day) || day < 1 || day > 31)) return setErr('The due day is a day of the month, 1 to 31.')
    const label = name.trim() || i.detectedName
    onUpdate({ key: i.key, name, confirmed: true, ...(day === null ? { clearDueDay: true } : { dueDay: day }) }, `Saved ${label}.`)
  }

  /** The newer of the two keeps going; the older one's charges join it. */
  function join(o: RecurringItem) {
    const [from, into] = o.lastDate > i.lastDate ? [i, o] : [o, i]
    onUpdate({ key: from.key, mergeInto: into.key, confirmed: true }, `${into.name} and ${from.name} are one bill now, with all their charges.`)
  }

  const action = 'flex w-full items-center justify-between gap-3 px-4 py-3 text-left text-sm font-medium'
  const back = (
    <button type="button" onClick={() => setPart('edit')} className={`${smallBtn} mb-3 text-danger`}>
      <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className="size-3.5">
        <path d="m12 5-5 5 5 5" />
      </svg>
      Back
    </button>
  )

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form role="dialog" aria-modal="true" aria-labelledby="bill-sheet-h" onSubmit={save} onClick={(e) => e.stopPropagation()} className={sheetCls}>
        <h2 id="bill-sheet-h" className="text-lg font-semibold">
          {i.name}
        </h2>
        <p className="text-sm text-ink-soft">
          {price(i)} {every[i.frequency]} · {i.occurrences} charges in the last 18 months · last {formatDay(i.lastDate)}
          {i.accountName && ` on ${i.accountName}`}
        </p>
        {i.name !== i.detectedName && <p className="text-xs text-ink-faint">Shows on your statement as {i.detectedName}.</p>}
        {i.varies && <p className="text-xs text-ink-faint">Its price varies, so your budget plans the usual amount: the middle of its last 6 charges.</p>}

        {part === 'same' && (
          <section className="mt-4">
            {back}
            <p className="text-sm font-semibold">Same bill as…</p>
            <p className="mt-0.5 text-xs text-ink-soft">
              Pick the bill this one really is. Their charges become one bill; if the price varies, the budget plans its usual amount.
            </p>
            {others.length === 0 ? (
              <p className="mt-3 text-sm text-ink-soft">There are no other bills to join it with.</p>
            ) : (
              <ul className="mt-3 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
                {others.map((o) => (
                  <li key={o.key}>
                    <button type="button" onClick={() => join(o)} className={action}>
                      <span className="min-w-0">
                        <span className="block truncate">{o.name}</span>
                        <span className="block truncate text-xs font-normal text-ink-soft">
                          {price(o)} {every[o.frequency]} · last {formatDay(o.lastDate)}
                          {o.status === 'canceled' ? ' · canceled' : ''}
                        </span>
                      </span>
                      <Chevron />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>
        )}

        {part === 'notBill' && (
          <section className="mt-4">
            {back}
            <p className="text-sm font-semibold">Not a bill</p>
            <p className="mt-0.5 text-xs text-ink-soft">
              Its charges become everyday spending in the category you pick, and your budget stops expecting it. You can restore it from “Not bills”.
            </p>
            {spending.length === 0 ? (
              <p className="mt-3 text-sm text-ink-soft">Add a spending category first.</p>
            ) : (
              <>
                <label className="mt-3 block">
                  <span className="text-sm font-medium text-ink-soft">Spending Category</span>
                  <select value={spendId} onChange={(e) => setSpendId(e.target.value)} className="mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base">
                    {spending.map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name}
                      </option>
                    ))}
                  </select>
                </label>
                <button type="button" disabled={!spendId} onClick={() => onNotABill(i, spendId)} className="mt-4 w-full rounded-2xl bg-danger py-3 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-50">
                  It’s not a bill
                </button>
              </>
            )}
          </section>
        )}

        {part === 'edit' && (
          <>
            <ChargeHistory item={i} />

            <label className="mt-4 block">
              <span className="text-sm font-medium text-ink-soft">Name</span>
              <input value={name} onChange={(e) => setName(e.target.value)} className="mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base" />
            </label>

            {i.frequency === 'Monthly' && (
              <label className="mt-4 block">
                <span className="text-sm font-medium text-ink-soft">Due day of the month</span>
                <input value={due} onChange={(e) => setDue(e.target.value)} inputMode="numeric" placeholder={`Last charged on the ${Number(i.lastDate.slice(8))}`} className="mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base" />
              </label>
            )}


            {err && (
              <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
                {err}
              </p>
            )}

            <p className="mt-6 px-1 text-sm font-semibold text-ink-soft">Options</p>
            <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
              {(i.status === 'priceUp' || i.status === 'priceDown') && i.previousAmountCents != null && (
                <li>
                  <button
                    type="button"
                    onClick={() => onUpdate({ key: i.key, acknowledgePrice: true, confirmed: true }, `Got it: ${i.name} is ${formatCents(i.amountCents)} now.`)}
                    className={`${action} text-primary`}
                  >
                    <span>
                      Got it, that’s the new price
                      <span className="block text-xs font-normal text-ink-soft">
                        {formatCents(i.previousAmountCents)} → {formatCents(i.amountCents)}. Your budget plans the new price.
                      </span>
                    </span>
                    <Chevron />
                  </button>
                </li>
              )}
              {!i.inBillCategory && (
                <li>
                  <button type="button" onClick={() => onTrackAsBill(i)} className={action}>
                    <span>
                      Make it a bill
                      <span className="block text-xs font-normal text-ink-soft">
                        {i.categoryName ? `Filed under ${i.categoryName}` : 'No category'}, so it counts as everyday spending now.
                      </span>
                    </span>
                    <Chevron />
                  </button>
                </li>
              )}
              <li>
                <button type="button" onClick={() => setPart('same')} className={action}>
                  <span>
                    Same as another bill…
                    <span className="block text-xs font-normal text-ink-soft">Combine it with the bill it really is, even if the price differs.</span>
                  </span>
                  <Chevron />
                </button>
              </li>
              {(i.mergedCount ?? 0) > 0 && (
                <li>
                  <button type="button" onClick={() => onUpdate({ key: i.key, split: true }, `Split ${i.name} apart: the ${i.mergedCount === 1 ? 'bill' : `${i.mergedCount} bills`} you joined into it are separate again.`)} className={action}>
                    <span>
                      Split apart
                      <span className="block text-xs font-normal text-ink-soft">
                        Undo combining: {i.mergedCount === 1 ? 'the bill' : `the ${i.mergedCount} bills`} joined into it become separate again.
                      </span>
                    </span>
                    <Chevron />
                  </button>
                </li>
              )}
              {(i.status === 'possiblyCanceled' || i.lookingSince) && (
                <li>
                  {i.lookingSince ? (
                    <button type="button" onClick={() => onUpdate({ key: i.key, keepLooking: false }, `Stopped looking for ${i.name}'s newer charges.`)} className={action}>
                      <span>
                        Stop looking for it
                        <span className="block text-xs font-normal text-ink-soft">
                          {i.found ? `The ${i.found} newer charge${i.found === 1 ? '' : 's'} it found go back to being separate. ` : ''}It can be flagged as stopped again.
                        </span>
                      </span>
                      <Chevron />
                    </button>
                  ) : (
                    <button type="button" onClick={() => onUpdate({ key: i.key, keepLooking: true }, keepLookingMessage(i))} className={`${action} text-primary`}>
                      <span>
                        Still going: keep looking
                        <span className="block text-xs font-normal text-ink-soft">
                          You didn’t cancel it. The app finds its newer charges, even at a new price, and your budget keeps expecting it.
                        </span>
                      </span>
                      <Chevron />
                    </button>
                  )}
                </li>
              )}
              <li>
                {i.status === 'canceled' ? (
                  <button type="button" onClick={() => onUpdate({ key: i.key, canceled: false }, `${i.name} is active again.`)} className={action}>
                    <span>
                      Not canceled after all
                      <span className="block text-xs font-normal text-ink-soft">Your budget expects it again.</span>
                    </span>
                    <Chevron />
                  </button>
                ) : (
                  <button type="button" onClick={() => onUpdate({ key: i.key, canceled: true }, `Marked ${i.name} as canceled. Your budget stops expecting it.`)} className={`${action} text-danger`}>
                    <span>
                      Canceled
                      <span className="block text-xs font-normal text-ink-soft">It was a bill and it stopped. Its history stays.</span>
                    </span>
                    <Chevron />
                  </button>
                )}
              </li>
              <li>
                <button type="button" onClick={() => setPart('notBill')} className={`${action} text-danger`}>
                  <span>
                    Not a bill
                    <span className="block text-xs font-normal text-ink-soft">It never was one: its charges become everyday spending.</span>
                  </span>
                  <Chevron />
                </button>
              </li>
            </ul>
            <div className="mt-5 flex gap-3">
              <button type="button" onClick={onClose} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-danger">
                Cancel
              </button>
              <button type="submit" className="flex-1 rounded-2xl bg-primary py-3 text-sm font-semibold text-primary-ink shadow-neu-sm">
                Save
              </button>
            </div>
          </>
        )}
      </form>
    </div>
  )
}


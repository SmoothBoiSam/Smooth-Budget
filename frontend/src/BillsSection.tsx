import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, budgetApi, isNewFind, needsLook, recurringApi, type Category, type RecurringItem, type RecurringSummary, type RecurringUpdate, listedType } from './api'
import { Chevron, ErrorNotice, Loading, sheetCls, smallBtn, smallBtnOn } from './ui'
import { formatCents, formatDay } from './format'

/** A bill's price: "about" when it varies from charge to charge (DECISIONS.md 197). */
const price = (i: RecurringItem) => `${i.varies ? 'about ' : ''}${formatCents(i.amountCents)}`

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
  /** The bill sheet (DECISIONS.md 197): one bill's every option, opened on a part of it. */
  const [open, setOpen] = useState<{ item: RecurringItem; part: SheetPart } | null>(null)
  /** A bill tapped in the list (or linked from Budget): shown as its own card, with its charges and what to do. */
  const [focus, setFocus] = useState<string | null>(null)
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
        setFocus(summary.items.some((i) => i.key === key) ? key : null)
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
      setFocus(null)
      await load()
    } catch (e) {
      fail(e)
    }
  }

  /** "Not a bill": its charges become spending in the category picked (DECISIONS.md 197). */
  async function notABill(i: RecurringItem, categoryId: string) {
    try {
      const r = await recurringApi.notABill(i.key, categoryId)
      setNotice(`${i.name} isn't a bill: its charges are spending under ${r.name} now, and nothing expects it.`)
      setOpen(null)
      setFocus(null)
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
      setFocus(null)
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
  const focused = focus ? s?.items.find((i) => i.key === focus) : undefined
  const current = focused ?? (mode === 'cards' ? waiting[0] : undefined)
  const show = (i: RecurringItem) => {
    setNotice(null)
    setFocus(i.key)
    window.scrollTo(0, 0)
  }

  return (
    <div>
      {mode === 'list' && <p className="mt-1 text-sm text-ink-soft">Found from your transactions: the same store, a similar amount, on a regular schedule.</p>}

      {notice && (
        <p role="status" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!s && !error && <Loading label="Looking through your transactions" />}

      {s && current && (
        <>
          {focused ? (
            <button type="button" onClick={() => setFocus(null)} className="neu-sm mt-3 inline-flex items-center gap-1 rounded-full py-1.5 pr-3.5 pl-2.5 text-xs font-semibold text-danger">
              <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className="size-3.5">
                <path d="m12 5-5 5 5 5" />
              </svg>
              All bills
            </button>
          ) : (
            <p className="mt-3 text-xs text-ink-soft">
              <span className="font-semibold text-ink-soft">{waiting.length} to look at</span>
              {skipped.length > 0 && (
                <button type="button" onClick={() => setSkipped([])} className={`${smallBtn} ml-2 text-ink-soft`}>
                  Show {skipped.length} skipped
                </button>
              )}
            </p>
          )}
          <BillCard
            key={current.key}
            item={current}
            onUpdate={update}
            onOpen={(part = 'edit') => setOpen({ item: current, part })}
            onTrackAsBill={trackAsBill}
            // Opened from All bills: the back pill above the card goes back, so the card has no second one.
            onSkip={
              focused
                ? undefined
                : () => {
                    setNotice(null)
                    setSkipped((k) => [...k, current.key])
                  }
            }
          />
        </>
      )}
      {s && mode === 'cards' && !current && (
        <p className="mt-3 rounded-2xl neu px-4 py-3 text-sm text-ink-soft">
          {alerts.length === 0 ? 'Nothing needs a look. ' : `You skipped the rest (${skipped.length}). `}
          {skipped.length > 0 && (
            <button type="button" onClick={() => setSkipped([])} className={`${smallBtnOn} ml-1`}>
              Go through them again
            </button>
          )}
          {alerts.length === 0 && 'Every bill is below; tap one to see its charges.'}
        </p>
      )}

      {s && !current && (
        <>
          <dl className="mt-4 rounded-3xl neu p-4">
            <dt className="text-xs text-ink-soft">Your bills cost</dt>
            <dd className="text-xl font-semibold tabular-nums">{formatCents(s.monthlyCents)} <span className="text-sm font-normal text-ink-soft">a month</span></dd>
            <dd className="text-xs text-ink-soft">{formatCents(s.annualCents)} a year</dd>
          </dl>

          {mode === 'list' && alerts.length > 0 && (
            <section aria-labelledby="alerts-h" className="mt-6">
              <h2 id="alerts-h" className="px-1 text-sm font-semibold text-ink-soft">
                Needs a look
              </h2>
              <ul className="mt-2 space-y-2">
                {alerts.map((i) => (
                  <li key={i.key}>
                    <Alert item={i} onUpdate={update} onOpen={() => show(i)} onTrackAsBill={trackAsBill} onNotABill={() => setOpen({ item: i, part: 'notBill' })} />
                  </li>
                ))}
              </ul>
            </section>
          )}

          <List title="All bills" items={bills.filter((i) => i.status !== 'canceled')} counted={counted} onOpen={show} />
          {bills.some((i) => i.status === 'canceled') && (
            <details className="group mt-6">
              <summary className="flex cursor-pointer list-none items-center gap-1 px-1 text-sm font-semibold text-ink-soft">
                <span className="transition-transform group-open:rotate-90">
                  <Chevron />
                </span>
                Canceled ({bills.filter((i) => i.status === 'canceled').length})
              </summary>
              <List title="" items={bills.filter((i) => i.status === 'canceled')} counted={counted} onOpen={show} />
            </details>
          )}

          {s.items.length === 0 && (
            <p className="mt-6 rounded-2xl neu px-4 py-3 text-sm text-ink-soft">
              Nothing recurring yet. It takes about three charges (two for a yearly one) to spot a pattern.
            </p>
          )}

          {s.dismissed.length > 0 && (
            <details className="group mt-6 text-sm">
              <summary className="flex cursor-pointer list-none items-center gap-1 px-1 font-semibold text-ink-soft">
                <span className="transition-transform group-open:rotate-90">
                  <Chevron />
                </span>
                Not bills ({s.dismissed.length})
              </summary>
              <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
                {s.dismissed.map((i) => (
                  <li key={i.key} className="flex items-center justify-between gap-3 px-4 py-2.5">
                    <span className="min-w-0 truncate">
                      {i.name}
                      {i.categoryName && <span className="text-xs text-ink-soft"> · {i.categoryName}</span>}
                    </span>
                    <button type="button" onClick={() => void update({ key: i.key, dismissed: false }, `${i.name} is back on the list.`)} className={`${smallBtn} shrink-0`}>
                      Restore
                    </button>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      )}

      {open && (
        <ItemSheet
          key={`${open.item.key}-${open.part}`}
          item={open.item}
          start={open.part}
          items={bills}
          categories={categories}
          onClose={() => setOpen(null)}
          onUpdate={update}
          onTrackAsBill={trackAsBill}
          onNotABill={notABill}
        />
      )}
    </div>
  )
}

function Alert({
  item: i,
  onUpdate,
  onOpen,
  onTrackAsBill,
  onNotABill,
}: {
  item: RecurringItem
  onUpdate: (u: RecurringUpdate, m: string) => void
  onOpen: () => void
  onTrackAsBill: (i: RecurringItem) => void
  onNotABill: () => void
}) {
  const box = 'rounded-2xl px-4 py-3 text-sm'
  const actions = 'mt-2.5 flex flex-wrap gap-2'
  // One store, two prices: maybe one bill whose price changed (DECISIONS.md 181). Asked before anything else about it.
  if (i.sameAs) {
    const old = i.sameAs
    const started = i.charges.length > 0 ? i.charges[i.charges.length - 1].date : i.lastDate
    return (
      <div className={`${box} neu-inset text-gold-ink`}>
        <p>
          <span className="font-semibold">Same bill, new price?</span> {i.name} at <span className="font-semibold tabular-nums">{formatCents(i.amountCents)}</span>{' '}
          started {formatDay(started)}, right after the <span className="tabular-nums">{formatCents(old.amountCents)}</span> one
          {old.name !== i.name && ` (${old.name})`} stopped on {formatDay(old.lastDate)}.
        </p>
        <div className={actions}>
          <button
            type="button"
            onClick={() =>
              onUpdate(
                { key: old.key, mergeInto: i.key },
                `${i.name} is one bill now: ${formatCents(old.amountCents)} until ${formatDay(old.lastDate)}, ${formatCents(i.amountCents)} since.`,
              )
            }
            className={smallBtnOn}
          >
            Yes, combine them
          </button>
          <button type="button" onClick={() => onUpdate({ key: old.key, notSameAs: i.key }, `Kept ${i.name} and the ${formatCents(old.amountCents)} one apart.`)} className={`${smallBtn} text-ink`}>
            No, they’re different
          </button>
        </div>
      </div>
    )
  }
  if (isNewFind(i))
    return (
      <div className={`${box} neu text-ink-soft`}>
        <p>
          <span className="font-semibold">{i.name}</span> looks recurring: {formatCents(i.amountCents)} {every[i.frequency]}
          {i.accountName && ` on ${i.accountName}`}, filed {i.categoryName ? `under ${i.categoryName}` : 'with no category'}.
        </p>
        <div className={actions}>
          <button type="button" onClick={() => onTrackAsBill(i)} className={smallBtnOn}>
            Make it a bill line
          </button>
          {i.categoryName && (
            <button type="button" onClick={() => onUpdate({ key: i.key, confirmed: true }, `${i.name} stays under ${i.categoryName}.`)} className={`${smallBtn} text-ink`}>
              Keep it in {i.categoryName}
            </button>
          )}
          <button type="button" onClick={onNotABill} className={`${smallBtn} text-danger`}>
            Not a bill
          </button>
        </div>
      </div>
    )
  if (i.status === 'priceUp' || i.status === 'priceDown')
    return (
      <div className={`${box} neu-inset text-gold-ink`}>
        <p>
          <span className="font-semibold">{i.name}</span> went {i.status === 'priceUp' ? 'up' : 'down'} from {formatCents(i.previousAmountCents!)} to{' '}
          <span className="font-semibold">{formatCents(i.amountCents)}</span>
          {i.priceChangedOn && ` on ${formatDay(i.priceChangedOn)}`}.
        </p>
        <div className={actions}>
          <button type="button" onClick={() => onUpdate({ key: i.key, acknowledgePrice: true }, `Noted the new price for ${i.name}.`)} className={smallBtnOn}>
            Got it
          </button>
          <button type="button" onClick={onOpen} className={`${smallBtn} text-accent`}>
            Details
          </button>
        </div>
      </div>
    )
  if (i.status === 'possiblyCanceled')
    return (
      <div className={`${box} neu text-ink-soft`}>
        <p>
          <span className="font-semibold">{i.name}</span>: no charge since {formatDay(i.lastDate)}. It was expected {formatDay(i.nextExpected)}.
        </p>
        <div className={actions}>
          <button type="button" onClick={() => onUpdate({ key: i.key, keepLooking: true }, keepLookingMessage(i))} className={smallBtnOn}>
            Still going
          </button>
          <button type="button" onClick={() => onUpdate({ key: i.key, canceled: true }, `Marked ${i.name} as canceled. Your budget stops expecting it.`)} className={`${smallBtn} text-danger`}>
            Canceled
          </button>
          <button type="button" onClick={onNotABill} className={`${smallBtn} text-danger`}>
            Not a bill
          </button>
        </div>
      </div>
    )
  if (!i.possibleDuplicate) return null
  return (
    <div className={`${box} neu-inset text-gold-ink`}>
      <p>
        <span className="font-semibold">{i.name}</span> may be charged twice: another {formatCents(i.amountCents)} charge from it repeats on the same schedule
        {i.accountName && ` (this one on ${i.accountName})`}.
      </p>
      <div className={actions}>
        <button type="button" onClick={onOpen} className={`${smallBtn} text-accent`}>
          Details
        </button>
      </div>
    </div>
  )
}

/** What "Still going" does (DECISIONS.md 200), said when you tap it. */
function keepLookingMessage(i: RecurringItem) {
  return `Got it: ${i.name} is still going. The app looks for its charges after ${formatDay(i.lastDate)}, even at a new price, and your budget keeps expecting it.`
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
    <section aria-label="Charges" className="mt-3">
      <p className={`text-xs ${status.cls}`}>{status.text}</p>
      {charges.length > 0 && (
        <>
          <p className="mt-2 text-xs text-ink-soft">
            {charges.length} {charges.length === 1 ? 'charge' : 'charges'} since {dayYear(charges[charges.length - 1].date)}
          </p>
          <ul className="mt-1 divide-y divide-ink/[0.07] rounded-xl neu-inset px-3 text-sm">
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
  onOpen: (part?: SheetPart) => void
  onTrackAsBill: (i: RecurringItem) => void
  /** Skip for now; left out when the card was opened from All bills, whose back pill goes back. */
  onSkip?: () => void
}) {
  // The quick choices on the card; every other option is in the bill sheet ("More options"), the same one everywhere.
  const confirmable = !i.confirmed && i.status !== 'canceled' && !needsLook(i)
  return (
    <article className="mt-2 rounded-3xl neu p-4">
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="min-w-0 truncate text-lg font-semibold">{i.name}</h2>
        <span className="shrink-0 font-semibold tabular-nums">
          {price(i)} <span className="text-xs font-normal text-ink-soft">{every[i.frequency]}</span>
        </span>
      </div>
      <p className="truncate text-xs text-ink-soft">
        {[i.accountName, i.categoryName ?? 'No category', i.varies ? 'price varies' : null].filter(Boolean).join(' · ')}
      </p>
      {needsLook(i) && (
        <div className="mt-3">
          <Alert item={i} onUpdate={onUpdate} onOpen={() => onOpen()} onTrackAsBill={onTrackAsBill} onNotABill={() => onOpen('notBill')} />
        </div>
      )}
      <ChargeHistory item={i} />
      <div className="mt-4 grid grid-cols-2 gap-3 text-sm font-semibold">
        {confirmable && (
          <button type="button" onClick={() => onUpdate({ key: i.key, confirmed: true }, `${i.name} looks right.`)} className="col-span-2 rounded-xl bg-primary py-2.5 text-primary-ink shadow-neu-sm">
            Looks right
          </button>
        )}
        <button type="button" onClick={() => onOpen('same')} className="rounded-xl neu py-2.5">
          Same as another…
        </button>
        <button type="button" onClick={() => onOpen('edit')} className="rounded-xl neu py-2.5">
          More options
        </button>
        {onSkip && (
          <button type="button" onClick={onSkip} className="col-span-2 rounded-xl neu py-2.5 text-danger">
            Skip for now
          </button>
        )}
      </div>
    </article>
  )
}

function List({ title, items, counted, onOpen }: { title: string; items: RecurringItem[]; counted: (i: RecurringItem) => boolean; onOpen: (i: RecurringItem) => void }) {
  if (items.length === 0) return null
  return (
    <section aria-labelledby={title ? `list-${title}` : undefined} className={title ? 'mt-6' : 'mt-2'}>
      {title && (
        <h2 id={`list-${title}`} className="px-1 text-sm font-semibold text-ink-soft">
          {title}
        </h2>
      )}
      <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
        {items.map((i) => (
          <li key={i.key}>
            <button type="button" onClick={() => onOpen(i)} className={`block w-full px-4 py-3 text-left ${counted(i) ? '' : 'opacity-60'}`}>
              <div className="flex items-baseline justify-between gap-3">
                <span className="truncate text-sm font-medium">{i.name}</span>
                <span className="shrink-0 text-sm tabular-nums">
                  {price(i)} <span className="text-xs text-ink-soft">{every[i.frequency]}</span>
                </span>
              </div>
              <p className="mt-0.5 truncate text-xs text-ink-soft">
                {i.status === 'canceled' ? 'Canceled' : i.status === 'possiblyCanceled' ? `Stopped? Last ${formatDay(i.lastDate)}` : `Next ${formatDay(i.nextExpected)}`}
                {i.accountName && ` · ${i.accountName}`}
                {i.categoryName && ` · ${i.categoryName}`}
                {!i.inBillCategory && counted(i) && <span className="text-gold-ink"> · not a bill line yet</span>}
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

type SheetPart = 'edit' | 'same' | 'notBill'

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
  const [categoryId, setCategoryId] = useState(i.categoryId ?? '')
  const spending = categories.filter((c) => c.type === 'Variable').sort((a, b) => a.sortOrder - b.sortOrder)
  const [spendId, setSpendId] = useState(spending.some((c) => c.id === i.categoryId) ? i.categoryId! : (spending[0]?.id ?? ''))
  const [err, setErr] = useState<string | null>(null)
  const moved = categoryId !== '' && categoryId !== (i.categoryId ?? '')

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
    const to = categories.find((c) => c.id === categoryId)?.name
    onUpdate(
      { key: i.key, name, confirmed: true, ...(day === null ? { clearDueDay: true } : { dueDay: day }) },
      moved ? `${label} is filed under ${to} now, past charges and future ones.` : `Saved ${label}.`,
      moved ? categoryId : undefined,
    )
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
                  <span className="text-sm font-medium text-ink-soft">Spending category</span>
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

            <label className="mt-4 block">
              <span className="text-sm font-medium text-ink-soft">Category</span>
              <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className="mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base">
                {categoryId === '' && <option value="">No category</option>}
                {categoryGroups.map((g) => {
                  const list = categories.filter((c) => listedType(c) === g.type).sort((a, b) => a.sortOrder - b.sortOrder)
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
              <span className="mt-1 block text-xs text-ink-soft">
                {moved
                  ? i.merchantWide
                    ? 'Its past charges move there, and future ones go there too.'
                    : 'Its past charges move there. This store bills more than one thing, so check new charges in Review.'
                  : 'Pick another category to move its charges there.'}
              </span>
            </label>

            {err && (
              <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
                {err}
              </p>
            )}
            <div className="mt-5 flex gap-3">
              <button type="button" onClick={onClose} className="flex-1 rounded-2xl neu py-3 text-sm font-semibold text-danger">
                Cancel
              </button>
              <button type="submit" className="flex-1 rounded-2xl bg-primary py-3 text-sm font-semibold text-primary-ink shadow-neu-sm">
                Save
              </button>
            </div>

            <p className="mt-6 px-1 text-sm font-semibold text-ink-soft">Options</p>
            <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu">
              {!i.inBillCategory && !moved && (
                <li>
                  <button type="button" onClick={() => onTrackAsBill(i)} className={action}>
                    <span>
                      Make it its own bill line
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
          </>
        )}
      </form>
    </div>
  )
}

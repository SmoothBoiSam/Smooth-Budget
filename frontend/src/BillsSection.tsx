import { useCallback, useEffect, useState } from 'react'
import { ApiError, budgetApi, isNewFind, needsLook, recurringApi, type Category, type RecurringItem, type RecurringSummary, type RecurringUpdate } from './api'
import BillSheet, { ChargeHistory } from './BillSheet'
import { every, keepLookingMessage, price, type SheetPart } from './bills'
import { Chevron, ErrorNotice, Loading, reviewCard, reviewPrimary, reviewSecondary, smallBtn, smallBtnOn } from './ui'
import { formatCents, formatDay } from './format'


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
      // A link to one bill (#/tasks/bills/<key>, from the Budget tab) opens it.
      const key = decodeURIComponent(window.location.hash.replace(/^#\/?tasks\/bills\/?/, ''))
      if (key) {
        setFocus(summary.items.some((i) => i.key === key) ? key : null)
        window.history.replaceState(null, '', '#/tasks/bills')
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

  async function trackAsBill(i: RecurringItem) {
    try {
      const r = await recurringApi.trackAsBill(i.key)
      setNotice(`${r.name} is its own bill category now, and its charges moved there. Future charges follow it.`)
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
            <p className="mt-3 flex min-h-8 items-center text-xs text-ink-soft">
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
          <dl className="mt-3 rounded-3xl neu p-5">
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
        <BillSheet
          item={open.item}
          start={open.part}
          items={bills}
          categories={categories}
          onClose={() => setOpen(null)}
          onDone={(message) => {
            setNotice(message)
            setOpen(null)
            setFocus(null)
            void load()
          }}
          onError={fail}
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
  inCard = false,
}: {
  item: RecurringItem
  onUpdate: (u: RecurringUpdate, m: string) => void
  onOpen: () => void
  onTrackAsBill: (i: RecurringItem) => void
  onNotABill: () => void
  /** On a Review card (DECISIONS.md 214): no box of its own inside the card. */
  inCard?: boolean
}) {
  const box = inCard ? 'text-sm' : 'rounded-2xl px-4 py-3 text-sm'
  const inset = inCard ? '' : 'neu-inset'
  const raised = inCard ? '' : 'neu'
  const actions = 'mt-3 flex flex-wrap gap-2'
  // One store, two prices: maybe one bill whose price changed (DECISIONS.md 181). Asked before anything else about it.
  if (i.sameAs) {
    const old = i.sameAs
    const started = i.charges.length > 0 ? i.charges[i.charges.length - 1].date : i.lastDate
    return (
      <div className={`${box} ${inset} text-gold-ink`}>
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
      <div className={`${box} ${raised} text-ink-soft`}>
        <p>
          <span className="font-semibold">{i.name}</span> looks recurring: {formatCents(i.amountCents)} {every[i.frequency]}
          {i.accountName && ` on ${i.accountName}`}, filed {i.categoryName ? `under ${i.categoryName}` : 'with no category'}.
        </p>
        <div className={actions}>
          <button type="button" onClick={() => onTrackAsBill(i)} className={smallBtnOn}>
            Make it a Bill Category
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
      <div className={`${box} ${inset} text-gold-ink`}>
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
      <div className={`${box} ${raised} text-ink-soft`}>
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
    <div className={`${box} ${inset} text-gold-ink`}>
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
    <article className={reviewCard}>
      {/* Laid out like a transaction's Review card (DECISIONS.md 214). "Same as another" is in More options. */}
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-base font-semibold">{i.name}</h2>
          <p className="mt-0.5 truncate text-xs text-ink-soft">
            {[i.accountName, i.categoryName ?? 'No category', i.varies ? 'price varies' : null].filter(Boolean).join(' · ')}
          </p>
        </div>
        <p className="shrink-0 text-right text-lg font-semibold tabular-nums">
          {price(i)} <span className="block text-xs font-normal text-ink-soft">{every[i.frequency]}</span>
        </p>
      </div>
      {needsLook(i) && (
        <div className="mt-4">
          <Alert item={i} inCard onUpdate={onUpdate} onOpen={() => onOpen()} onTrackAsBill={onTrackAsBill} onNotABill={() => onOpen('notBill')} />
        </div>
      )}
      <ChargeHistory item={i} />
      {confirmable && (
        <button type="button" onClick={() => onUpdate({ key: i.key, confirmed: true }, `${i.name} looks right.`)} className={`${reviewPrimary} mt-4`}>
          ✓ Looks right
        </button>
      )}
      <div className={`${confirmable ? 'mt-3' : 'mt-4'} grid gap-3 ${onSkip ? 'grid-cols-2' : ''}`}>
        <button type="button" onClick={() => onOpen('edit')} className={reviewSecondary}>
          More options
        </button>
        {onSkip && (
          <button type="button" onClick={onSkip} className={reviewSecondary}>
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
                {!i.inBillCategory && counted(i) && <span className="text-gold-ink"> · not in a bill category yet</span>}
              </p>
            </button>
          </li>
        ))}
      </ul>
    </section>
  )
}

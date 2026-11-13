import { isNewFind, needsLook, type RecurringItem, type RecurringUpdate } from './api'
import { ChargeHistory } from './BillSheet'
import { every, keepLookingMessage, price, type SheetPart } from './bills'
import { smallBtn, smallBtnOn } from './ui'
import { formatCents, formatDay } from './format'


/**
 * A bill's needs-a-look question and its card on a transaction's Review card (DECISIONS.md 245). The All bills page is
 * gone (DECISIONS.md 250): bills are edited from their category on the Budget tab.
 */
export function Alert({
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

/**
 * A bill on a transaction's Review card once you say it's a bill (DECISIONS.md 245): what the bill review card shows, its
 * price, what needs a look, its charges, and More options, under the transaction's own buttons.
 */
export function BillOnCard({
  item: i,
  onUpdate,
  onOpen,
  onTrackAsBill,
}: {
  item: RecurringItem
  onUpdate: (u: RecurringUpdate, m: string) => void
  onOpen: (part?: SheetPart) => void
  onTrackAsBill: (i: RecurringItem) => void
}) {
  return (
    <div className="mt-3 rounded-2xl neu-inset px-4 py-3">
      <div className="flex items-baseline justify-between gap-3 text-sm">
        <span className="min-w-0 truncate font-medium">{i.name}</span>
        <span className="shrink-0 tabular-nums">
          {price(i)} <span className="text-xs text-ink-soft">{every[i.frequency]}</span>
        </span>
      </div>
      <p className="truncate text-xs text-ink-soft">
        {[i.categoryName ?? 'No category', i.status === 'canceled' ? 'Canceled' : `Next ${formatDay(i.nextExpected)}`].join(' · ')}
      </p>
      {/* A new find is what this card makes into a bill, so only the other needs-a-look questions show here. */}
      {needsLook(i) && !isNewFind(i) && (
        <div className="mt-3">
          <Alert item={i} inCard onUpdate={onUpdate} onOpen={() => onOpen()} onTrackAsBill={onTrackAsBill} onNotABill={() => onOpen('notBill')} />
        </div>
      )}
      <ChargeHistory item={i} />
      <div className="mt-3 flex flex-wrap gap-2">
        <button type="button" onClick={() => onOpen('edit')} className={`${smallBtn} text-accent`}>
          More options
        </button>
        {!isNewFind(i) && (
          <button type="button" onClick={() => onOpen('notBill')} className={`${smallBtn} text-danger`}>
            Not a bill
          </button>
        )}
      </div>
    </div>
  )
}

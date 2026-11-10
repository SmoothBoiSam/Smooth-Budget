import type { RecurringItem } from './api'
import { formatCents, formatDay } from './format'

/** A bill's price: "about" when it varies from charge to charge (DECISIONS.md 197). */
export const price = (i: RecurringItem) => `${i.varies ? 'about ' : ''}${formatCents(i.amountCents)}`

export const every: Record<RecurringItem['frequency'], string> = {
  Weekly: 'a week',
  Biweekly: 'every 2 weeks',
  Monthly: 'a month',
  Quarterly: 'a quarter',
  Yearly: 'a year',
}

export function keepLookingMessage(i: RecurringItem) {
  return `Got it: ${i.name} is still going. The app looks for its charges after ${formatDay(i.lastDate)}, even at a new price, and your budget keeps expecting it.`
}

/** A date with its year, since a bill's charges go back 18 months. */
export function dayYear(iso: string) {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })
}

export type SheetPart = 'edit' | 'same' | 'notBill'

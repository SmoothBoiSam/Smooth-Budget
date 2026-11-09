import type { PayCheck } from './api'

/** "Fri, Nov 21": how a pay date reads on Plan and in Tasks. */
export const weekday = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })
}

/** A paid check whose every plan item is done. */
export const finished = (c: PayCheck) => c.isCurrent && !!c.plan && c.plan.items.length > 0 && c.plan.items.every((i) => i.done)

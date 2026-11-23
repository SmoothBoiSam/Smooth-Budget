import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { ApiError, budgetApi, needsLook, recurringApi, type Category, type RecurringItem, type RecurringSummary, type RecurringUpdate } from './api'
import BillSheet from './BillSheet'
import type { SheetPart } from './bills'

/**
 * Your bills and what to do with them (DECISIONS.md 245): Review transactions (bills that need a look, and a charge you
 * mark as a bill) and the All bills page share it. `sheet` is the bill sheet when one is open, to render once.
 */
export function useBills(onSignedOut: () => void, onCount: (needsLook: number) => void, onAfter?: () => void) {
  const [s, setS] = useState<RecurringSummary | null>(null)
  /** The bill sheet (DECISIONS.md 197): one bill's every option, opened on a part of it. */
  const [open, setOpen] = useState<{ item: RecurringItem; part: SheetPart } | null>(null)
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
      return summary
    } catch (e) {
      fail(e)
      return null
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
      onAfter?.()
      await load()
    } catch (e) {
      fail(e)
    }
  }

  async function trackAsBill(i: RecurringItem) {
    try {
      const r = await recurringApi.trackAsBill(i.key)
      setNotice(`${r.name} is a bill now, with its own line in your budget. Its charges moved there, and future ones follow.`)
      setOpen(null)
      onAfter?.()
      await load()
    } catch (e) {
      fail(e)
    }
  }

  const sheet: ReactNode = open && (
    <BillSheet
      item={open.item}
      start={open.part}
      items={s?.items ?? []}
      categories={categories}
      onClose={() => setOpen(null)}
      onDone={(message) => {
        setNotice(message)
        setOpen(null)
        onAfter?.()
        void load()
      }}
      onError={fail}
    />
  )

  return { s, notice, setNotice, error, load, update, trackAsBill, openSheet: (item: RecurringItem, part: SheetPart = 'edit') => setOpen({ item, part }), sheet }
}

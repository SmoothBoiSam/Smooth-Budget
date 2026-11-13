import { useCallback, useEffect, useState } from 'react'
import { ApiError, budgetApi, openPlanItems, payBadge, paydayApi, type PaydaySummary } from './api'
import { icons } from './icons'
import { useResume } from './offline'
import { weekday } from './paycheck'
import { Card, Page, PageHeader, Row, Rows } from './ui'

/**
 * The Tasks tab (DECISIONS.md 206): everything waiting on you, in one card like Plan's. Each part opens its own page and
 * carries its own count on its icon (the tab bar shows their sum): transactions to confirm, bills to look at, and the steps for the check you're on. Pay period
 * tasks only show once there's a paycheck forecast (schedule or pay stubs).
 */
export default function TasksScreen({
  inboxCount,
  onInboxCount,
  onPlanCount,
  onSignedOut,
}: {
  inboxCount: number
  onInboxCount: (n: number) => void
  onPlanCount: (n: number) => void
  onSignedOut: () => void
}) {
  const [payday, setPayday] = useState<PaydaySummary | null>(null)
  // Counted again when you come back to the app after a while (DECISIONS.md 251).
  const [again, setAgain] = useState(0)
  useResume(useCallback(() => setAgain((n) => n + 1), []))

  // Fresh counts each visit: a sync may have filed purchases or ticked steps off since the badge was last worked out.
  useEffect(() => {
    const signedOut = (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) onSignedOut()
    }
    budgetApi
      .inboxCount()
      .then((r) => onInboxCount(r.count + r.review))
      .catch(signedOut)
    paydayApi
      .summary()
      .then((p) => {
        setPayday(p)
        onPlanCount(payBadge(p))
      })
      .catch(signedOut)
  }, [onInboxCount, onPlanCount, onSignedOut, again])

  const current = payday?.checks.find((c) => c.isCurrent && c.plan && c.plan.items.length > 0)
  const hasPayTasks = !!payday?.checks.some((c) => c.plan && c.plan.items.length > 0)
  // planCount (the tab badge) includes uploads due; the pay period row counts only its own steps.
  const steps = payday ? openPlanItems(payday) : 0
  const uploads = payday?.uploadsDue ?? []
  const both = uploads.some((u) => u.kind === 'schedule') && uploads.some((u) => u.kind === 'stub')
  const payHint = current
    ? steps > 0
      ? `${steps} to do for the check paid ${weekday(current.payDate)}`
      : `All done for the check paid ${weekday(current.payDate)}`
    : 'The steps for your next check'

  return (
    <Page>
      <PageHeader title="Tasks" />
      <Card>
        <Rows>
          {/* No separate bill review (DECISIONS.md 248): a bill's charge is reviewed as a transaction. */}
          <Row
            icon={icons.transactions}
            label={<span className="font-semibold">Review transactions</span>}
            hint={inboxCount > 0 ? `${inboxCount} ${inboxCount === 1 ? 'purchase' : 'purchases'} to confirm` : 'All caught up'}
            badge={inboxCount}
            href="#/tasks/transactions"
          />
          {hasPayTasks && (
            <Row
              icon={icons.payTasks}
              label={<span className="font-semibold">Pay period tasks</span>}
              hint={payHint}
              badge={steps}
              href="#/tasks/payday"
            />
          )}
          {/* Schedules and stubs to upload (DECISIONS.md 238): they keep the paycheck forecast right. */}
          {uploads.length > 0 && (
            <Row
              icon={icons.payday}
              label={
                <span className="font-semibold">
                  Upload {both ? 'schedule and pay stubs' : uploads[0].kind === 'schedule' ? (uploads.length === 1 ? 'your schedule' : 'schedules') : uploads.length === 1 ? 'a pay stub' : 'pay stubs'}
                </span>
              }
              hint={uploads.length === 1 ? uploads[0].label : `${uploads[0].label}, and ${uploads.length - 1} more`}
              badge={uploads.length}
              href="#/payday/setup"
            />
          )}
        </Rows>
      </Card>
    </Page>
  )
}

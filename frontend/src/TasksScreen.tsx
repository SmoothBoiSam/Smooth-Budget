import { useEffect, useState } from 'react'
import { ApiError, budgetApi, needsLook, openPlanItems, paydayApi, recurringApi, type PaydaySummary } from './api'
import { icons } from './icons'
import { weekday } from './paycheck'
import { Card, CountBubble, Page, PageHeader, Row, Rows } from './ui'

/**
 * The Tasks tab (DECISIONS.md 206): everything waiting on you, in one card like Plan's. Each part opens its own page and
 * carries its own count: transactions to confirm, bills to look at, and the steps for the check you're on. Pay period
 * tasks only show once there's a paycheck forecast (schedule or pay stubs).
 */
export default function TasksScreen({
  inboxCount,
  billsCount,
  planCount,
  onInboxCount,
  onBillsCount,
  onPlanCount,
  onSignedOut,
}: {
  inboxCount: number
  billsCount: number
  planCount: number
  onInboxCount: (n: number) => void
  onBillsCount: (n: number) => void
  onPlanCount: (n: number) => void
  onSignedOut: () => void
}) {
  const [payday, setPayday] = useState<PaydaySummary | null>(null)

  // Fresh counts each visit: a sync may have filed purchases or ticked steps off since the badge was last worked out.
  useEffect(() => {
    const signedOut = (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) onSignedOut()
    }
    budgetApi
      .inboxCount()
      .then((r) => onInboxCount(r.count + r.review))
      .catch(signedOut)
    recurringApi
      .summary()
      .then((s) => onBillsCount(s.items.filter(needsLook).length))
      .catch(signedOut)
    paydayApi
      .summary()
      .then((p) => {
        setPayday(p)
        onPlanCount(openPlanItems(p))
      })
      .catch(signedOut)
  }, [onInboxCount, onBillsCount, onPlanCount, onSignedOut])

  const current = payday?.checks.find((c) => c.isCurrent && c.plan && c.plan.items.length > 0)
  const hasPayTasks = !!payday?.checks.some((c) => c.plan && c.plan.items.length > 0)
  const payHint = current
    ? planCount > 0
      ? `${planCount} to do for the check paid ${weekday(current.payDate)}`
      : `All done for the check paid ${weekday(current.payDate)}`
    : 'The steps for your next check'

  return (
    <Page>
      <PageHeader title="Tasks" />
      <Card>
        <Rows>
          <Row
            icon={icons.transactions}
            label={<span className="font-semibold">Review transactions</span>}
            hint={inboxCount > 0 ? `${inboxCount} ${inboxCount === 1 ? 'purchase' : 'purchases'} to confirm` : 'All caught up'}
            value={inboxCount > 0 ? <CountBubble n={inboxCount} /> : undefined}
            href="#/tasks/transactions"
          />
          <Row
            icon={icons.bills}
            label={<span className="font-semibold">Review bills</span>}
            hint={billsCount > 0 ? `${billsCount} ${billsCount === 1 ? 'bill needs' : 'bills need'} a look` : 'Nothing new'}
            value={billsCount > 0 ? <CountBubble n={billsCount} /> : undefined}
            href="#/tasks/bills"
          />
          {hasPayTasks && (
            <Row
              icon={icons.payTasks}
              label={<span className="font-semibold">Pay period tasks</span>}
              hint={payHint}
              value={planCount > 0 ? <CountBubble n={planCount} /> : undefined}
              href="#/tasks/payday"
            />
          )}
        </Rows>
      </Card>
    </Page>
  )
}

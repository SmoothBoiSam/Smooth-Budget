import type { ReactNode } from 'react'
import type { HomeSummary } from './api'
import { formatCents } from './format'

/**
 * Everything on Home that wants a tap, most urgent first: problems, then things to do, then good news. The Home tab's
 * bubble counts only the ones you can do something about now (act; DECISIONS.md 283, 294).
 */
export function homeNotices(s: HomeSummary, stale: boolean) {
  const notices: { key: string; tone: 'danger' | 'todo' | 'good'; href?: string; act: boolean; text: ReactNode }[] = []
  if (!stale && (s.sync.state === 'stale' || s.sync.state === 'error'))
    notices.push({ key: 'sync', tone: s.sync.state === 'error' ? 'danger' : 'todo', href: '#/more/banks', act: true, text: s.sync.message ?? 'Bank sync needs a look.' })
  // A friend's money that's late or short: worth knowing, but nothing to do here until it comes.
  for (const f of (s.contributions ?? []).filter((f) => f.state === 'late' || (f.state === 'differentAmount' && f.shortfallCents > 0)))
    notices.push({
      key: `partner-${f.arrangementId}`,
      tone: 'danger',
      href: f.friendId ? `#/friends/${f.friendId}` : '#/friends',
      act: false,
      text:
        f.state === 'late'
          ? `${formatCents(f.expectedCents)} for ${f.categoryName} hasn’t come yet`
          : `Only ${formatCents(f.receivedCents)} of ${formatCents(f.expectedCents)} came for ${f.categoryName}`,
    })
  // No card for maxed categories (DECISIONS.md 294): the spending tiles right above say so. The push notification stays (280).
  if (s.incomeUnknown) notices.push({ key: 'income', tone: 'todo', href: '#/budget/plan', act: true, text: 'Set this month’s income' })
  if (s.uncategorizedCount > 0)
    notices.push({ key: 'uncat', tone: 'todo', href: '#/tasks/transactions', act: true, text: `${s.uncategorizedCount} ${s.uncategorizedCount === 1 ? 'purchase needs' : 'purchases need'} a category` })
  if (s.reviewCount > 0)
    notices.push({ key: 'review', tone: 'todo', href: '#/tasks/transactions', act: true, text: `${s.reviewCount} ${s.reviewCount === 1 ? 'purchase' : 'purchases'} to confirm` })
  for (const p of s.pendingSweeps)
    notices.push({
      key: `sweep-${p.id}`,
      tone: 'good',
      href: '#/tasks/payday',
      act: true,
      text: (
        <>
          Send <b className="tabular-nums">{formatCents(p.amountCents)}</b> of {p.fromMonth.split(' ')[0]}’s leftover to {p.destination}
        </>
      ),
    })

  return notices
}

/** The Home tab's bubble (DECISIONS.md 283, 294): the notices you can act on now. */
export const homeBadge = (s: HomeSummary, stale: boolean) => homeNotices(s, stale).filter((n) => n.act).length

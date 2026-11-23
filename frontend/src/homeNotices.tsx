import type { ReactNode } from 'react'
import type { HomeSummary } from './api'
import { formatCents, isMaxed } from './format'

/**
 * Everything on Home that wants a tap, most urgent first: problems, then things to do, then good news. The Home tab's
 * bubble counts them (DECISIONS.md 283).
 */
export function homeNotices(s: HomeSummary, stale: boolean) {
  const notices: { key: string; tone: 'danger' | 'todo' | 'good'; href?: string; text: ReactNode }[] = []
  if (!stale && (s.sync.state === 'stale' || s.sync.state === 'error'))
    notices.push({ key: 'sync', tone: s.sync.state === 'error' ? 'danger' : 'todo', href: '#/more/banks', text: s.sync.message ?? 'Bank sync needs a look.' })
  for (const f of (s.contributions ?? []).filter((f) => f.state === 'late' || (f.state === 'differentAmount' && f.shortfallCents > 0)))
    notices.push({
      key: `partner-${f.arrangementId}`,
      tone: 'danger',
      href: f.friendId ? `#/friends/${f.friendId}` : '#/friends',
      text:
        f.state === 'late'
          ? `${formatCents(f.expectedCents)} for ${f.categoryName} hasn’t come yet`
          : `Only ${formatCents(f.receivedCents)} of ${formatCents(f.expectedCents)} came for ${f.categoryName}`,
    })
  // Limits grow to fit what's spent (DECISIONS.md 268), so a category is maxed rather than over (280).
  const maxed = (s.spending ?? []).filter((c) => isMaxed(c.spentCents, c.leftCents)).length
  if (maxed > 0) notices.push({ key: 'maxed', tone: 'danger', href: '#/budget', text: `${maxed} ${maxed === 1 ? 'category' : 'categories'} maxed` })
  if (s.incomeUnknown) notices.push({ key: 'income', tone: 'todo', href: '#/budget/plan', text: 'Set this month’s income' })
  if (s.uncategorizedCount > 0)
    notices.push({ key: 'uncat', tone: 'todo', href: '#/tasks/transactions', text: `${s.uncategorizedCount} ${s.uncategorizedCount === 1 ? 'purchase needs' : 'purchases need'} a category` })
  if (s.reviewCount > 0)
    notices.push({ key: 'review', tone: 'todo', href: '#/tasks/transactions', text: `${s.reviewCount} ${s.reviewCount === 1 ? 'purchase' : 'purchases'} to confirm` })
  for (const p of s.pendingSweeps)
    notices.push({
      key: `sweep-${p.id}`,
      tone: 'good',
      href: '#/tasks/payday',
      text: (
        <>
          Send <b className="tabular-nums">{formatCents(p.amountCents)}</b> of {p.fromMonth.split(' ')[0]}’s leftover to {p.destination}
        </>
      ),
    })

  return notices
}

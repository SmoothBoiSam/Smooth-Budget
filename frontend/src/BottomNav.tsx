// Bottom tab bar (DECISIONS.md 147, 206): five tabs with their names under the icons. Tasks holds everything waiting on
// you (transactions, bills, pay period tasks); Money your Debit, Debt, Savings and Income; Friends and Settings live under More.
import type { ReactNode } from 'react'

import { icons } from './icons'
import { bubbleStyle, bubbleText } from './theme'

export type Route = 'home' | 'transactions' | 'budget' | 'tasks' | 'review' | 'paytasks' | 'debt' | 'savings' | 'payday' | 'paysetup' | 'debit' | 'more' | 'friends' | 'friend'

const tabs: { label: string; route: Route; icon: ReactNode; also: Route[] }[] = [
  { label: 'Home', route: 'home', icon: icons.home, also: [] },
  { label: 'Budget', route: 'budget', icon: icons.budget, also: [] },
  { label: 'Tasks', route: 'tasks', icon: icons.tasks, also: ['review', 'paytasks'] },
  // Debit, Debt, Savings and Income live under Money (once Plan, DECISIONS.md 256); Friends under More.
  { label: 'Money', route: 'payday', icon: icons.money, also: ['paysetup', 'debit', 'debt', 'savings', 'transactions'] },
  { label: 'More', route: 'more', icon: icons.more, also: ['friends', 'friend'] },
]

/** A count in a blue bubble on a tab: Tasks (transactions and bills to look at, payday steps still to do). */
export default function BottomNav({ route, badges }: { route: Route; badges: Partial<Record<Route, number>> }) {
  return (
    <nav
      aria-label="Main"
      className="fixed inset-x-0 bottom-0 z-10 rounded-t-3xl bg-surface [view-transition-name:tabbar] pb-[env(safe-area-inset-bottom)] shadow-[0_-6px_16px_var(--color-neu-dark)]"
    >
      <ul className="mx-auto flex max-w-md justify-between px-3 pt-2">
        {tabs.map((tab) => {
          const active = tab.route === route || tab.also.includes(route)
          const count = badges[tab.route] ?? 0
          return (
            <li key={tab.label} className="flex-1">
              <a
                href={`#/${tab.route === 'home' ? '' : tab.route}`}
                aria-current={active ? 'page' : undefined}
                aria-label={count > 0 ? `${tab.label}, ${count} to do` : tab.label}
                className={`flex flex-col items-center gap-1 pb-2 ${active ? "text-accent" : "text-ink-faint"}`}
              >
                <span className={`relative block rounded-2xl px-3.5 py-1.5 transition-shadow duration-300 ${active ? 'neu-inset' : ''}`}>
                  <span className={`block transition-opacity duration-300 ${active ? '' : 'opacity-60'}`}>{tab.icon}</span>
                  {count > 0 && (
                    <span
                      key={count}
                      aria-hidden
                      className="animate-pop absolute top-0 left-1/2 ml-1.5 inline-flex min-w-4 items-center justify-center whitespace-nowrap rounded-full px-1 text-[10px] font-bold tabular-nums leading-4"
                      style={bubbleStyle}
                    >
                      {bubbleText(count)}
                    </span>
                  )}
                </span>
                <span aria-hidden className={`text-[11px] ${active ? 'font-semibold' : 'font-medium'}`}>
                  {tab.label}
                </span>
              </a>
            </li>
          )
        })}
      </ul>
    </nav>
  )
}

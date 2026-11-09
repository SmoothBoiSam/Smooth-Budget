// Bottom tab bar (DECISIONS.md 147): five tabs with their names under the icons. Plan holds the payday plan with Debt and
// Savings under it; Bills, Partner and Settings live under More.
import type { ReactNode } from 'react'

import { icons } from './icons'

export type Route = 'home' | 'transactions' | 'budget' | 'review' | 'debt' | 'savings' | 'payday' | 'paysetup' | 'more' | 'partner'

const tabs: { label: string; route: Route; icon: ReactNode; also: Route[] }[] = [
  { label: 'Home', route: 'home', icon: icons.home, also: ['transactions'] },
  { label: 'Budget', route: 'budget', icon: icons.budget, also: [] },
  { label: 'Review', route: 'review', icon: icons.review, also: [] },
  // Payday setup, Debt and Savings live under Plan; Partner under More; Bills under Review.
  { label: 'Plan', route: 'payday', icon: icons.payday, also: ['paysetup', 'debt', 'savings'] },
  { label: 'More', route: 'more', icon: icons.more, also: ['partner'] },
]

/** A count in a red bubble on a tab: Review (what to look at) and Plan (payday steps still to do). */
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
                aria-label={count > 0 ? `${tab.label}, ${count} ${tab.route === 'review' ? 'to review' : 'to do'}` : tab.label}
                className={`flex flex-col items-center gap-1 pb-2 ${active ? "text-primary" : "text-ink-faint"}`}
              >
                <span className={`relative block rounded-2xl px-3.5 py-1.5 transition-shadow duration-200 ${active ? 'neu-inset' : ''}`}>
                  <span className={`block transition-opacity duration-200 ${active ? '' : 'opacity-60'}`}>{tab.icon}</span>
                  {count > 0 && (
                    <span
                      key={count}
                      aria-hidden
                      className="animate-pop absolute top-0 left-1/2 ml-1.5 inline-flex min-w-4 items-center justify-center rounded-full px-1 text-[10px] font-bold leading-4"
                      // A strong red so the white number stands out; fixed colors so the dark theme doesn't swap them.
                      style={{ backgroundColor: '#DC2626', backgroundImage: 'linear-gradient(160deg, #f05a5a, #c81e1e)', color: '#fff' }}
                    >
                      {count > 99 ? '99+' : count}
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

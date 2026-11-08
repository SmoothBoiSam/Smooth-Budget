// Bottom tab bar (DECISIONS.md 147): five tabs with their names under the icons. Plan holds the payday plan with Debt and
// Savings under it; Bills, Partner and Settings live under More.
import type { ReactNode } from 'react'

import { icons } from './icons'

export type Route = 'home' | 'budget' | 'review' | 'debt' | 'savings' | 'payday' | 'paysetup' | 'more' | 'bills' | 'partner'

const tabs: { label: string; route: Route; icon: ReactNode; also: Route[] }[] = [
  { label: 'Home', route: 'home', icon: icons.home, also: [] },
  { label: 'Budget', route: 'budget', icon: icons.budget, also: [] },
  { label: 'Review', route: 'review', icon: icons.review, also: [] },
  // Payday setup, Debt and Savings live under Plan; Bills and Partner under More.
  { label: 'Plan', route: 'payday', icon: icons.payday, also: ['paysetup', 'debt', 'savings'] },
  { label: 'More', route: 'more', icon: icons.more, also: ['bills', 'partner'] },
]

export default function BottomNav({ route, inboxCount }: { route: Route; inboxCount: number }) {
  return (
    <nav
      aria-label="Main"
      className="fixed inset-x-0 bottom-0 z-10 border-t border-slate-200 bg-white/95 pb-[env(safe-area-inset-bottom)] backdrop-blur"
    >
      <ul className="mx-auto flex max-w-md justify-between px-2">
        {tabs.map((tab) => {
          const active = tab.route === route || tab.also.includes(route)
          return (
            <li key={tab.label} className="flex-1">
              <a
                href={`#/${tab.route === 'home' ? '' : tab.route}`}
                aria-current={active ? 'page' : undefined}
                aria-label={tab.route === 'review' && inboxCount > 0 ? `${tab.label}, ${inboxCount} to review` : tab.label}
                className={`flex flex-col items-center gap-0.5 pt-2 pb-1.5 ${active ? 'text-slate-900' : 'text-slate-400'}`}
              >
                <span className="relative">
                  <span className={active ? '' : 'opacity-55'}>{tab.icon}</span>
                  {tab.route === 'review' && inboxCount > 0 && (
                    <span
                      aria-hidden
                      className="absolute -top-1 left-1/2 ml-1 inline-flex min-w-4 items-center justify-center rounded-full px-1 text-[10px] font-bold leading-4"
                      // A strong red so the white number stands out; fixed colors so the dark theme doesn't swap them.
                      style={{ backgroundColor: '#DC2626', color: '#fff' }}
                    >
                      {inboxCount > 99 ? '99+' : inboxCount}
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

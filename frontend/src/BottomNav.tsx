// Bottom tab bar from SPEC section 4, plus Review (DECISIONS.md 87). Bills, Partner and Settings live under More.
// Icons instead of words so seven tabs fit on a phone (DECISIONS.md 129); each keeps its name for screen readers.
import type { ReactNode } from 'react'

export type Route = 'home' | 'budget' | 'review' | 'debt' | 'savings' | 'payday' | 'paysetup' | 'more' | 'bills' | 'partner'

const icon = (children: ReactNode) => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round" aria-hidden className="mx-auto size-6">
    {children}
  </svg>
)

const tabs: { label: string; route: Route; icon: ReactNode }[] = [
  { label: 'Home', route: 'home', icon: icon(<><path d="M3 11.5 12 4l9 7.5" /><path d="M5.5 10v10h13V10" /><path d="M10 20v-5.5h4V20" /></>) },
  { label: 'Budget', route: 'budget', icon: icon(<><path d="M12 3.5a8.5 8.5 0 1 0 8.5 8.5H12z" /><path d="M15 3.9A8.5 8.5 0 0 1 20.1 9H15z" /></>) },
  { label: 'Review', route: 'review', icon: icon(<><path d="M4 6.5h10M4 12h10M4 17.5h6" /><path d="m14.5 17 2.2 2.2L21 15" /></>) },
  { label: 'Debt', route: 'debt', icon: icon(<><rect x="2.5" y="5.5" width="19" height="13" rx="2" /><path d="M2.5 10h19M6.5 15h4" /></>) },
  { label: 'Savings', route: 'savings', icon: icon(<><ellipse cx="12" cy="6.5" rx="7" ry="2.8" /><path d="M5 6.5v5.2c0 1.5 3.1 2.8 7 2.8s7-1.3 7-2.8V6.5" /><path d="M5 11.7v5.5c0 1.5 3.1 2.8 7 2.8s7-1.3 7-2.8v-5.5" /></>) },
  { label: 'Payday', route: 'payday', icon: icon(<><rect x="3" y="5" width="18" height="16" rx="2" /><path d="M3 10h18M8 3v4M16 3v4" /><path d="M12 12.5v6M10.2 14.2c0-.8.8-1.3 1.8-1.3s1.8.5 1.8 1.2-.8 1-1.8 1.2-1.8.6-1.8 1.3.8 1.3 1.8 1.3 1.8-.5 1.8-1.3" /></>) },
  { label: 'More', route: 'more', icon: icon(<><circle cx="5.5" cy="12" r="1.2" fill="currentColor" /><circle cx="12" cy="12" r="1.2" fill="currentColor" /><circle cx="18.5" cy="12" r="1.2" fill="currentColor" /></>) },
]

export default function BottomNav({ route, inboxCount }: { route: Route; inboxCount: number }) {
  return (
    <nav
      aria-label="Main"
      className="fixed inset-x-0 bottom-0 z-10 border-t border-slate-200 bg-white/95 pb-[env(safe-area-inset-bottom)] backdrop-blur"
    >
      <ul className="mx-auto flex max-w-md justify-between px-2">
        {tabs.map((tab) => {
          // Payday setup lives under Payday; Bills and Partner live under More.
          const active = tab.route === route || (tab.route === 'payday' && route === 'paysetup') || (tab.route === 'more' && (route === 'bills' || route === 'partner'))
          return (
            <li key={tab.label} className="flex-1">
              <a
                href={`#/${tab.route === 'home' ? '' : tab.route}`}
                aria-current={active ? 'page' : undefined}
                aria-label={tab.route === 'review' && inboxCount > 0 ? `${tab.label}, ${inboxCount} to review` : tab.label}
                title={tab.label}
                className={`relative flex flex-col items-center gap-1 pt-2.5 pb-2 ${active ? 'text-slate-900' : 'text-slate-400'}`}
              >
                {tab.icon}
                <span aria-hidden className={`size-1 rounded-full ${active ? 'bg-slate-900' : 'bg-transparent'}`} />
                {tab.route === 'review' && inboxCount > 0 && (
                  <span
                    aria-hidden
                    className="absolute top-1 left-1/2 ml-1.5 inline-flex min-w-4 items-center justify-center rounded-full bg-amber-500 px-1 text-[10px] font-bold leading-4 text-white"
                  >
                    {inboxCount > 99 ? '99+' : inboxCount}
                  </span>
                )}
              </a>
            </li>
          )
        })}
      </ul>
    </nav>
  )
}

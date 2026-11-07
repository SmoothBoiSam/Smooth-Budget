// Bottom tab bar from SPEC section 4, plus Review (DECISIONS.md 87). Bills, Partner and Settings live under More.
export type Route = 'home' | 'budget' | 'review' | 'debt' | 'savings' | 'payday' | 'paysetup' | 'more' | 'bills' | 'partner'

const tabs: { label: string; route?: Route }[] = [
  { label: 'Home', route: 'home' },
  { label: 'Budget', route: 'budget' },
  { label: 'Review', route: 'review' },
  { label: 'Debt', route: 'debt' },
  { label: 'Savings', route: 'savings' },
  { label: 'Payday', route: 'payday' },
  { label: 'More', route: 'more' },
]

export default function BottomNav({ route, inboxCount }: { route: Route; inboxCount: number }) {
  return (
    <nav
      aria-label="Main"
      className="fixed inset-x-0 bottom-0 z-10 border-t border-slate-200 bg-white/95 pb-[env(safe-area-inset-bottom)] backdrop-blur"
    >
      <ul className="mx-auto flex max-w-md justify-between px-1">
        {tabs.map((tab) => {
          if (!tab.route) {
            return (
              <li key={tab.label} className="flex-1">
                <span
                  aria-disabled
                  title="Coming in a later milestone"
                  className="block py-3 text-center text-[11px] font-medium text-slate-300"
                >
                  {tab.label}
                </span>
              </li>
            )
          }
          const active = tab.route === route || (tab.route === 'payday' && route === 'paysetup') || (tab.route === 'more' && (route === 'bills' || route === 'partner')) // Bills and Partner live under More
          return (
            <li key={tab.label} className="flex-1">
              <a
                href={`#/${tab.route === 'home' ? '' : tab.route}`}
                aria-current={active ? 'page' : undefined}
                className={`relative block py-3 text-center text-[11px] font-medium ${active ? 'text-slate-900' : 'text-slate-500'}`}
              >
                {tab.label}
                {tab.route === 'review' && inboxCount > 0 && (
                  <span
                    aria-label={`${inboxCount} to review`}
                    className="absolute top-1 ml-0.5 inline-flex min-w-4 items-center justify-center rounded-full bg-amber-500 px-1 text-[10px] font-bold leading-4 text-white"
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

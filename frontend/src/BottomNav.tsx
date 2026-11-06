// Bottom tab bar from SPEC section 4. Only Home exists in milestone 1; the rest are shown disabled.
const tabs = ['Home', 'Budget', 'Debt', 'Savings', 'Payday', 'More'] as const

export default function BottomNav() {
  return (
    <nav
      aria-label="Main"
      className="fixed inset-x-0 bottom-0 border-t border-slate-200 bg-white/95 pb-[env(safe-area-inset-bottom)] backdrop-blur"
    >
      <ul className="mx-auto flex max-w-md justify-between px-2">
        {tabs.map((tab) => {
          const active = tab === 'Home'
          return (
            <li key={tab} className="flex-1">
              <span
                aria-current={active ? 'page' : undefined}
                aria-disabled={!active}
                title={active ? undefined : 'Coming in a later milestone'}
                className={`block py-3 text-center text-xs font-medium ${active ? 'text-slate-900' : 'text-slate-300'}`}
              >
                {tab}
              </span>
            </li>
          )
        })}
      </ul>
    </nav>
  )
}

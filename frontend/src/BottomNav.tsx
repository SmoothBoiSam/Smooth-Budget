// Bottom tab bar from SPEC section 4, plus Review (DECISIONS.md 87). Bills, Partner and Settings live under More.
// Icons instead of words so seven tabs fit on a phone (DECISIONS.md 129); each keeps its name for screen readers.
import type { ReactNode } from 'react'

export type Route = 'home' | 'budget' | 'review' | 'debt' | 'savings' | 'payday' | 'paysetup' | 'more' | 'bills' | 'partner'

// Tab icons drawn in the style of your cat logo (DECISIONS.md 140): flat charcoal shapes, gold coins, pink blush and a
// white outline that keeps them clear on the dark theme.
const C = { body: '#2A2B33', ring: '#4A4A57', coin: '#FFCE47', coinRim: '#F2B533', coinSide: '#E0A12A', dollar: '#D99A1E', blush: '#F28DA8', red: '#E25B5B' }

const art = (children: ReactNode) => (
  <svg viewBox="0 0 32 32" aria-hidden className="mx-auto size-7">
    {children}
  </svg>
)
/** Shapes inside get the logo's white outline, drawn behind their fill. */
const Outlined = ({ children }: { children: ReactNode }) => (
  <g stroke="#fff" strokeWidth={2.4} strokeLinejoin="round" style={{ paintOrder: 'stroke' }}>
    {children}
  </g>
)

const tabs: { label: string; route: Route; icon: ReactNode }[] = [
  { label: 'Home', route: 'home', icon: <img src="/brand/logo.png" alt="" aria-hidden className="mx-auto size-7" /> },
  {
    label: 'Budget',
    route: 'budget',
    icon: art(
      <>
        <Outlined>
          <path d="M15 6.5a11 11 0 1 0 11 11H15z" fill={C.body} />
          {/* The space the slice was cut from is filled white, like its outline. */}
          <path d="M15 17.5V6.5a11 11 0 0 1 11 11z" fill="#fff" />
          <path d="M18 3.5a11 11 0 0 1 10.5 10.5H18z" fill={C.coin} />
        </Outlined>
        <path d="M18 3.5a11 11 0 0 1 10.5 10.5" fill="none" stroke={C.coinRim} strokeWidth={1} />
      </>,
    ),
  },
  {
    label: 'Review',
    route: 'review',
    icon: art(
      <>
        <Outlined>
          <rect x="6" y="6" width="20" height="23" rx="3.5" fill={C.body} />
          <rect x="11" y="3.5" width="10" height="5.5" rx="1.8" fill={C.ring} />
        </Outlined>
        <rect x="10" y="12.5" width="9" height="2.2" rx="1.1" fill={C.ring} />
        <rect x="10" y="17.5" width="5.5" height="2.2" rx="1.1" fill={C.ring} />
        <path d="m15.5 22.8 2.6 2.6 5.4-5.8" fill="none" stroke={C.coin} strokeWidth={2.6} strokeLinecap="round" strokeLinejoin="round" />
      </>,
    ),
  },
  {
    label: 'Debt',
    route: 'debt',
    icon: art(
      <>
        <Outlined>
          <rect x="3" y="8" width="26" height="17" rx="3.5" fill={C.body} />
        </Outlined>
        <rect x="3" y="12" width="26" height="3.2" fill={C.ring} />
        <rect x="7" y="18.5" width="6.5" height="4" rx="1.2" fill={C.coin} />
        <circle cx="21.5" cy="20.5" r="2.1" fill={C.red} />
        <circle cx="24.3" cy="20.5" r="2.1" fill={C.blush} fillOpacity={0.9} />
      </>,
    ),
  },
  {
    label: 'Savings',
    route: 'savings',
    icon: art(
      <>
        {/* The outline comes from the coins themselves (same 2.4 white stroke as the other icons), then the coins on top. */}
        <Outlined>
          {[22, 17, 12].map((y) => (
            <g key={y} fill="#fff">
              <rect x="7" y={y} width="18" height="4.5" />
              <ellipse cx="16" cy={y + 4.5} rx="9" ry="2.5" />
              <ellipse cx="16" cy={y} rx="9" ry="2.5" />
            </g>
          ))}
        </Outlined>
        {[22, 17, 12].map((y) => (
          <g key={y}>
            <rect x="7" y={y} width="18" height="4.5" fill={C.coinSide} />
            <ellipse cx="16" cy={y + 4.5} rx="9" ry="2.5" fill={C.coinSide} />
            <ellipse cx="16" cy={y} rx="8.55" ry="2.05" fill={C.coin} stroke={C.coinRim} strokeWidth={0.9} />
          </g>
        ))}
      </>,
    ),
  },
  {
    label: 'Payday',
    route: 'payday',
    icon: art(
      <>
        <Outlined>
          <rect x="4" y="6.5" width="24" height="22" rx="3.5" fill={C.body} />
        </Outlined>
        <path d="M4 10a3.5 3.5 0 0 1 3.5-3.5h17A3.5 3.5 0 0 1 28 10v2.5H4z" fill={C.red} />
        <rect x="9.5" y="3.5" width="3" height="6" rx="1.5" fill={C.ring} stroke="#fff" strokeWidth={1.2} />
        <rect x="19.5" y="3.5" width="3" height="6" rx="1.5" fill={C.ring} stroke="#fff" strokeWidth={1.2} />
        <circle cx="16" cy="20.5" r="5.6" fill={C.coinRim} />
        <circle cx="16" cy="20.5" r="4.3" fill={C.coin} />
        <path
          d="M17.6 18.6c-.3-.7-1-1.1-1.7-1.1-1 0-1.8.5-1.8 1.2 0 1.7 3.7 1 3.7 2.9 0 .8-.8 1.4-1.9 1.4-.9 0-1.6-.4-1.9-1.1M15.9 16.4v8.2"
          fill="none"
          stroke={C.dollar}
          strokeWidth={1.3}
          strokeLinecap="round"
        />
      </>,
    ),
  },
  {
    label: 'More',
    route: 'more',
    icon: art(
      <>
        <Outlined>
          {[7, 16, 25].map((x) => (
            <circle key={x} cx={x} cy="16" r="3.8" fill={C.body} />
          ))}
        </Outlined>
        {[7, 16, 25].map((x) => (
          <circle key={x} cx={x} cy="16" r="1.5" fill={C.blush} />
        ))}
      </>,
    ),
  },
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
                className={`relative flex flex-col items-center gap-1 pt-2.5 pb-2 ${active ? "text-slate-900" : "text-slate-400"}`}
              >
                <span className={active ? "" : "opacity-55"}>{tab.icon}</span>
                <span aria-hidden className={`size-1 rounded-full ${active ? 'bg-slate-900' : 'bg-transparent'}`} />
                {tab.route === 'review' && inboxCount > 0 && (
                  <span
                    aria-hidden
                    className="absolute top-1 left-1/2 ml-1.5 inline-flex min-w-4 items-center justify-center rounded-full px-1 text-[10px] font-bold leading-4"
                    // The logo's blush pink with white text, fixed colors so the dark theme doesn't swap them.
                    style={{ backgroundColor: C.blush, color: '#fff' }}
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

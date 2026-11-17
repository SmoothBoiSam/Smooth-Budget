import type { ReactNode } from 'react'

// Icons drawn in the style of your cat logo (DECISIONS.md 140): flat charcoal shapes, gold coins, pink blush and a
// white outline that keeps them clear on the dark theme.
const C = { body: '#2A2B33', ring: '#4A4A57', coin: '#FFCE47', coinRim: '#F2B533', coinSide: '#E0A12A', dollar: '#D99A1E', blush: '#F28DA8', red: '#E25B5B' }

const art = (children: ReactNode) => (
  <svg viewBox="0 0 32 32" aria-hidden className="mx-auto size-7">
    {children}
  </svg>
)
/** Shapes inside get the logo's white outline, drawn behind their fill. */
const outlined = (children: ReactNode) => (
  <g stroke="#fff" strokeWidth={2.4} strokeLinejoin="round" style={{ paintOrder: 'stroke' }}>
    {children}
  </g>
)

/** Every icon: the tab bar, the Debt and Savings links on Plan, and the parts of Tasks. */
export const icons = {
  home: <img src="/brand/logo.png" alt="" aria-hidden className="mx-auto size-7" />,
  budget: art(
    <>
      {outlined(
        <>
          <path d="M15 6.5a11 11 0 1 0 11 11H15z" fill={C.body} />
          <path d="M18 3.5a11 11 0 0 1 10.5 10.5H18z" fill={C.coin} />
        </>,
      )}
      <path d="M18 3.5a11 11 0 0 1 10.5 10.5" fill="none" stroke={C.coinRim} strokeWidth={1} />
    </>,
  ),
  tasks: art(
    <>
      {outlined(
        <>
          <rect x="6" y="6" width="20" height="23" rx="3.5" fill={C.body} />
          <rect x="11" y="3.5" width="10" height="5.5" rx="1.8" fill={C.ring} />
        </>,
      )}
      <rect x="10" y="12.5" width="9" height="2.2" rx="1.1" fill={C.ring} />
      <rect x="10" y="17.5" width="5.5" height="2.2" rx="1.1" fill={C.ring} />
      <path d="m15.5 22.8 2.6 2.6 5.4-5.8" fill="none" stroke={C.coin} strokeWidth={2.6} strokeLinecap="round" strokeLinejoin="round" />
    </>,
  ),
  debt: art(
    <>
      {outlined(
        <>
          <rect x="3" y="8" width="26" height="17" rx="3.5" fill={C.body} />
        </>,
      )}
      <rect x="3" y="12" width="26" height="3.2" fill={C.ring} />
      <rect x="7" y="18.5" width="6.5" height="4" rx="1.2" fill={C.coin} />
      <circle cx="21.5" cy="20.5" r="2.1" fill={C.red} />
      <circle cx="24.3" cy="20.5" r="2.1" fill={C.blush} fillOpacity={0.9} />
    </>,
  ),
  savings: art(
    <>
      {/* The outline comes from the coins themselves (same 2.4 white stroke as the other icons), then the coins on top. */}
      {outlined(
        <>
          {[22, 17, 12].map((y) => (
            <g key={y} fill="#fff">
              <rect x="7" y={y} width="18" height="4.5" />
              <ellipse cx="16" cy={y + 4.5} rx="9" ry="2.5" />
              <ellipse cx="16" cy={y} rx="9" ry="2.5" />
            </g>
          ))}
        </>,
      )}
      {[22, 17, 12].map((y) => (
        <g key={y}>
          <rect x="7" y={y} width="18" height="4.5" fill={C.coinSide} />
          <ellipse cx="16" cy={y + 4.5} rx="9" ry="2.5" fill={C.coinSide} />
          <ellipse cx="16" cy={y} rx="8.55" ry="2.05" fill={C.coin} stroke={C.coinRim} strokeWidth={0.9} />
        </g>
      ))}
    </>,
  ),
  /** A bank with a coin in its roof: Debit, your checking accounts (DECISIONS.md 256). */
  debit: art(
    <>
      {outlined(
        <>
          <path d="M16 3.5 28.5 10.5v1.5h-25v-1.5z" fill={C.body} />
          {[6, 11.5, 17.5, 23].map((x) => (
            <rect key={x} x={x} y="13.5" width="3" height="10" rx="1" fill={C.body} />
          ))}
          <rect x="3.5" y="24.5" width="25" height="4" rx="1.4" fill={C.body} />
        </>,
      )}
      <circle cx="16" cy="8.6" r="2.3" fill={C.coin} stroke={C.coinRim} strokeWidth={0.9} />
      <rect x="5.5" y="25.8" width="21" height="1.4" rx="0.7" fill={C.ring} />
    </>,
  ),
  /** A wallet with a coin peeking out: the Money tab, once Plan (DECISIONS.md 256). */
  money: art(
    <>
      {outlined(<circle cx="12" cy="9" r="5.6" fill={C.coinRim} />)}
      <circle cx="12" cy="9" r="4.3" fill={C.coin} />
      <path d="M12 6.8v3.6" stroke={C.dollar} strokeWidth={1.4} strokeLinecap="round" />
      {outlined(<rect x="3" y="11" width="25" height="17" rx="4" fill={C.body} />)}
      <path d="M3 15h25" stroke={C.ring} strokeWidth={1.6} />
      <rect x="19" y="17" width="11" height="7.5" rx="2.5" fill={C.ring} stroke="#fff" strokeWidth={1.2} />
      <circle cx="23.5" cy="20.75" r="1.7" fill={C.blush} />
    </>,
  ),
  payday: art(
    <>
      {outlined(
        <>
          <rect x="4" y="6.5" width="24" height="22" rx="3.5" fill={C.body} />
        </>,
      )}
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
  // The four parts of Tasks (DECISIONS.md 206).
  /** A receipt with a coin: purchases to confirm. */
  transactions: art(
    <>
      {outlined(
        <>
          <path d="M7.5 5.5A1.5 1.5 0 0 1 9 4h14a1.5 1.5 0 0 1 1.5 1.5V28l-2.8-2-2.8 2-2.9-2-2.9 2-2.8-2-2.8 2z" fill={C.body} />
          <circle cx="22" cy="20.5" r="5.4" fill={C.coinRim} />
        </>,
      )}
      <rect x="11" y="8.5" width="10" height="2.2" rx="1.1" fill={C.ring} />
      <rect x="11" y="13.5" width="6" height="2.2" rx="1.1" fill={C.ring} />
      <circle cx="22" cy="20.5" r="4.1" fill={C.coin} />
      <path d="M22 18.3v4.4" stroke={C.dollar} strokeWidth={1.5} strokeLinecap="round" />
    </>,
  ),
  /** An envelope sealed with a coin: bills. No red on it, so it isn't mistaken for a count bubble. */
  bills: art(
    <>
      {outlined(
        <>
          <rect x="3.5" y="7.5" width="25" height="18" rx="3" fill={C.body} />
        </>,
      )}
      <path d="m5 9.5 11 8.5 11-8.5" fill="none" stroke={C.ring} strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="16" cy="18.5" r="4.9" fill={C.coinRim} stroke="#fff" strokeWidth={1.2} />
      <circle cx="16" cy="18.5" r="3.6" fill={C.coin} />
      <path d="M16 16.4v4.2" stroke={C.dollar} strokeWidth={1.4} strokeLinecap="round" />
    </>,
  ),
  /** Two coins passing between friends: the running tab. */
  tab: art(
    <>
      {[
        'M18.5 5.5h5a2.5 2.5 0 0 1 2.5 2.5v4.5m-2.6-2.4 2.6 2.6 2.6-2.6',
        'M13.5 26.5h-5A2.5 2.5 0 0 1 6 24v-4.5m2.6 2.4L6 19.3l-2.6 2.6',
      ].map((d) => (
        <g key={d} fill="none" strokeLinecap="round" strokeLinejoin="round">
          <path d={d} stroke="#fff" strokeWidth={4.6} />
          <path d={d} stroke={C.body} strokeWidth={2.2} />
        </g>
      ))}
      {outlined(
        <>
          <circle cx="11.5" cy="11.5" r="6" fill={C.coinRim} />
          <circle cx="20.5" cy="20.5" r="6" fill={C.body} />
        </>,
      )}
      <circle cx="11.5" cy="11.5" r="4.6" fill={C.coin} />
      <circle cx="20.5" cy="20.5" r="2.6" fill={C.blush} />
    </>,
  ),
  /** A checklist with gold checks: the steps for a paycheck. */
  payTasks: art(
    <>
      {outlined(
        <>
          <rect x="5" y="4" width="22" height="25" rx="3.5" fill={C.body} />
        </>,
      )}
      {[9.5, 16.5, 23.5].map((y, i) => (
        <g key={y}>
          <rect x="8.5" y={y - 2.2} width="4.4" height="4.4" rx="1.2" fill={i < 2 ? C.ring : C.blush} />
          <rect x="15" y={y - 1.1} width="8.5" height="2.2" rx="1.1" fill={C.ring} />
          {i < 2 && <path d={`m8.6 ${y - 0.2} 2 2 4-4.6`} fill="none" stroke={C.coin} strokeWidth={2.2} strokeLinecap="round" strokeLinejoin="round" />}
        </g>
      ))}
    </>,
  ),
  more: art(
    <>
      {outlined(
        <>
          {[7, 16, 25].map((x) => (
            <circle key={x} cx={x} cy="16" r="3.8" fill={C.body} />
          ))}
        </>,
      )}
      {[7, 16, 25].map((x) => (
        <circle key={x} cx={x} cy="16" r="1.5" fill={C.blush} />
      ))}
    </>,
  ),
}

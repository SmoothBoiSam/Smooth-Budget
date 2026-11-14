// The app's building blocks (DECISIONS.md 187). Every screen is made of these, so cards, rows, notices and buttons look
// and behave the same everywhere. Neumorphic: everything is the page's own color, raised (neu) or pressed in (neu-inset).
import { useEffect, useSyncExternalStore, type ReactNode } from 'react'
import { confirmStore } from './confirm'
import { bubbleStyle, bubbleText } from './theme'

/** Every screen's frame: one width, one gutter, room above the bottom nav. */
export function Page({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`mx-auto min-h-screen max-w-md px-5 pt-[max(1.25rem,env(safe-area-inset-top))] pb-[calc(7rem+env(safe-area-inset-bottom))] ${className}`}>{children}</div>
}

/** Every screen's title. A sub-page gets a back pill above it; `action` sits on the right (a small button or link). */
export function PageHeader({ title, sub, back, action }: { title: ReactNode; sub?: ReactNode; back?: { href: string; label: string }; action?: ReactNode }) {
  return (
    <header>
      {back && (
        <a href={back.href} className="neu-sm mb-3 inline-flex items-center gap-1 rounded-full py-1.5 pr-3.5 pl-2.5 text-xs font-semibold text-danger">
          <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className="size-3.5">
            <path d="m12 5-5 5 5 5" />
          </svg>
          {back.label}
        </a>
      )}
      <div className="flex items-center justify-between gap-3">
        <h1 className="min-w-0 truncate text-2xl font-bold tracking-tight">{title}</h1>
        {action}
      </div>
      {sub && <p className="mt-0.5 text-sm text-ink-soft">{sub}</p>}
    </header>
  )
}

/**
 * A small button (DECISIONS.md 198): flat, not raised, so a row of them close together doesn't blur into overlapping
 * shadows. Colored ones (smallBtnOn) get the shared gradient from index.css.
 */
export const smallBtn = 'inline-flex items-center justify-center gap-1 rounded-full bg-ink/[0.07] px-3 py-1.5 text-xs font-semibold transition-colors disabled:opacity-50'
export const smallBtnOn = 'inline-flex items-center justify-center gap-1 rounded-full bg-primary px-3 py-1.5 text-xs font-semibold text-primary-ink transition-colors disabled:opacity-50'

/**
 * Review's cards (DECISIONS.md 214): one padding, one gap between parts (16px), one main button and flat secondary
 * buttons, the same on Review transactions and Review bills.
 */
export const reviewCard = 'animate-rise-in mt-3 rounded-3xl neu p-5'
export const reviewPrimary = 'w-full rounded-2xl bg-primary py-3 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-40'
export const reviewSecondary = 'rounded-2xl bg-ink/[0.07] py-2.5 text-sm font-semibold text-ink-soft disabled:opacity-50'

/** A small raised pill button for a header's action ("Add", "Edit"). */
export const headerActionCls = 'neu-sm shrink-0 rounded-full px-3.5 py-1.5 text-xs font-semibold text-accent'

/** The bottom sheet's panel (sheets slide up over a dim backdrop). */
export const sheetCls = 'max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]'

/** A raised card. With a title, a small heading sits above it. */
export function Card({ title, action, children, className = '' }: { title?: string; action?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`mt-6 ${className}`}>
      {title && (
        <div className="mb-2 flex items-baseline justify-between gap-3 px-1">
          <h2 className="px-1 text-sm font-semibold text-ink-soft">{title}</h2>
          {action}
        </div>
      )}
      <div className="neu rounded-3xl">{children}</div>
    </section>
  )
}

/** Rows stacked inside a card, with hairlines between them. */
export function Rows({ children }: { children: ReactNode }) {
  return <ul className="divide-y divide-ink/[0.07]">{children}</ul>
}

/** One row: a label, an optional value, and a chevron when it opens something. */
export function Row({
  label,
  hint,
  value,
  tone,
  icon,
  badge,
  href,
  onClick,
}: {
  label: ReactNode
  icon?: ReactNode
  /** A count on the icon's corner, like the tab bar's. */
  badge?: number
  hint?: ReactNode
  value?: ReactNode
  tone?: 'good' | 'danger'
  href?: string
  onClick?: () => void
}) {
  const inner = (
    <>
      {icon && (
        // A longer number stretches the bubble into a pill (DECISIONS.md 210). It starts a little further over the icon so
        // it never reaches the label, which stays lined up with the other rows'.
        <span className={`relative w-7 shrink-0 ${badge ? 'mr-1.5' : ''}`}>
          {icon}
          {!!badge && (
            <span
              className={`absolute -top-1.5 flex ${bubbleText(badge).length > 3 ? 'left-2.5' : bubbleText(badge).length > 2 ? 'left-4' : 'left-5'}`}
              aria-label={`${badge} to do`}
            >
              <CountBubble n={badge} small />
            </span>
          )}
        </span>
      )}
      <span className="min-w-0 flex-1">
        <span className="block">{label}</span>
        {hint && <span className="mt-0.5 block truncate text-xs text-ink-soft">{hint}</span>}
      </span>
      {value !== undefined && (
        <span className={`shrink-0 whitespace-nowrap font-semibold tabular-nums ${tone === 'danger' ? 'text-danger' : tone === 'good' ? 'text-good' : ''}`}>{value}</span>
      )}
      {(href || onClick) && <Chevron />}
    </>
  )
  const cls = 'flex w-full items-center gap-3 px-5 py-3.5 text-left text-sm'
  return (
    <li>
      {href ? (
        <a href={href} className={cls}>
          {inner}
        </a>
      ) : onClick ? (
        <button type="button" onClick={onClick} className={cls}>
          {inner}
        </button>
      ) : (
        <div className={cls}>{inner}</div>
      )}
    </li>
  )
}

const noticeDot = { danger: 'bg-danger', todo: 'bg-gold', good: 'bg-good' }

/** Something to act on or know about: a colored dot (red problem, gold to do, green good news) and a sentence. */
export function Notice({ tone, href, children }: { tone: keyof typeof noticeDot; href?: string; children: ReactNode }) {
  const inner = (
    <>
      <span aria-hidden className={`mt-1.5 size-2 shrink-0 rounded-full transition-colors duration-300 ${noticeDot[tone]}`} />
      <span className="min-w-0 flex-1">{children}</span>
      {href && <Chevron />}
    </>
  )
  const cls = 'neu flex items-start gap-3 rounded-2xl px-4 py-3 text-sm'
  // The pressed look comes from the shared button styles (index.css); a status notice that appears slides in there too.
  return href ? (
    <a href={href} className={cls}>
      {inner}
    </a>
  ) : (
    <div role="status" className={cls}>
      {inner}
    </div>
  )
}

/** A small rounded label, e.g. "On track". Flat, like other small things (DECISIONS.md 198, 204): too small for a bevel. */
export function Pill({ tone, children }: { tone?: 'good' | 'danger' | 'todo'; children: ReactNode }) {
  const color = tone === 'good' ? 'text-good' : tone === 'danger' ? 'text-danger' : tone === 'todo' ? 'text-gold-ink' : 'text-ink-soft'
  return <span className={`inline-block rounded-full bg-ink/[0.06] px-3 py-1 text-xs font-semibold transition-colors duration-300 ${color}`}>{children}</span>
}

/** The one main button style (green) and the one secondary style (raised). */
export function Button({
  children,
  onClick,
  kind = 'primary',
  type = 'button',
  disabled,
  className = '',
}: {
  children: ReactNode
  onClick?: () => void
  kind?: 'primary' | 'secondary'
  type?: 'button' | 'submit'
  disabled?: boolean
  className?: string
}) {
  const look = kind === 'primary' ? 'bg-primary shadow-neu-sm text-primary-ink' : 'neu text-ink'
  return (
    <button
      type={type}
      onClick={onClick}
      disabled={disabled}
      className={`${look} w-full rounded-2xl py-3 text-sm font-semibold transition active:neu-inset disabled:opacity-50 ${className}`}
    >
      {children}
    </button>
  )
}

/** The "Are you sure?" sheet that ask() in confirm.ts opens (DECISIONS.md 215). Drawn once, above everything. */
export function ConfirmHost() {
  const req = useSyncExternalStore(confirmStore.subscribe, confirmStore.get)
  useEffect(() => {
    if (!req) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && req.answer(false)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [req])
  if (!req) return null
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-slate-900/40" onClick={() => req.answer(false)}>
      <div role="alertdialog" aria-modal="true" aria-labelledby="confirm-msg" onClick={(e) => e.stopPropagation()} className={sheetCls}>
        <p id="confirm-msg" className="text-base">
          {req.message}
        </p>
        <div className="mt-5 grid grid-cols-2 gap-3">
          <button type="button" onClick={() => req.answer(false)} className="rounded-2xl neu py-3 text-sm font-semibold">
            Cancel
          </button>
          <button
            type="button"
            autoFocus
            onClick={() => req.answer(true)}
            className={`rounded-2xl py-3 text-sm font-semibold shadow-neu-sm ${req.danger ? 'bg-danger text-primary-ink' : 'bg-primary text-primary-ink'}`}
          >
            {req.yes}
          </button>
        </div>
      </div>
    </div>
  )
}

/** A count in a blue bubble, like the tab bar's: a circle for one digit, a pill for more. Small sits on an icon. Nothing at zero. */
export function CountBubble({ n, small = false }: { n: number; small?: boolean }) {
  if (n <= 0) return null
  return (
    <span
      key={n}
      className={`animate-pop inline-flex items-center justify-center whitespace-nowrap rounded-full font-bold tabular-nums ${small ? 'min-w-4 px-1 text-[10px] leading-4' : 'min-w-5 px-1.5 text-[11px] leading-5'}`}
      style={bubbleStyle}
    >
      {bubbleText(n)}
    </span>
  )
}

export function Chevron() {
  return (
    <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className="size-4 shrink-0 text-ink-faint">
      <path d="m8 5 5 5-5 5" />
    </svg>
  )
}

/**
 * A placeholder in the shape of what's loading (DECISIONS.md 254), so nothing seems to promise cards that never come:
 * "cards", a big card and two rows (Home, Debt, Savings, Income, a Review card); "list", a heading and one list of rows
 * (Budget, the history pages, Friends).
 */
export function Loading({ label = 'Loading', shape = 'cards' }: { label?: string; shape?: 'cards' | 'list' }) {
  return (
    <div role="status" aria-live="polite" className="mt-6 space-y-4">
      <span className="sr-only">{label}…</span>
      {shape === 'list' ? (
        <>
          <div className="neu-inset shimmer h-4 w-1/3 rounded-full" />
          <div className="neu shimmer h-44 rounded-2xl" />
        </>
      ) : (
        <>
          <div className="neu shimmer h-64 rounded-3xl" />
          <div className="neu shimmer h-14 rounded-2xl" />
          <div className="neu shimmer h-14 rounded-2xl" />
        </>
      )}
    </div>
  )
}

/** A failed load, said plainly, with a way to try again. */
export function ErrorNotice({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" className="neu mt-4 rounded-2xl px-4 py-3 text-sm">
      <p className="text-danger">{message}</p>
      {onRetry && (
        <button type="button" onClick={onRetry} className="mt-2 font-semibold text-accent">
          Try again
        </button>
      )}
    </div>
  )
}

/** A thin bar across the top while the phone has no connection. */
export function OfflineBar() {
  return (
    <div role="status" className="sticky top-0 z-20 bg-ink px-4 py-1.5 pt-[max(0.375rem,env(safe-area-inset-top))] text-center text-xs font-medium text-surface">
      Offline: showing what was saved on this phone.
    </div>
  )
}

/** Across the top in demo mode (DECISIONS.md 213): it's made-up data, and a way out. */
export function DemoBar({ onLeave }: { onLeave: () => void }) {
  return (
    <div role="status" className="sticky top-0 z-20 flex items-center justify-center gap-3 bg-accent px-4 py-1.5 pt-[max(0.375rem,env(safe-area-inset-top))] text-xs font-medium text-white">
      <span>Demo: made-up numbers, just for you</span>
      <button type="button" onClick={onLeave} className="rounded-full bg-white/20 px-2.5 py-0.5 font-semibold">
        Leave demo
      </button>
    </div>
  )
}

/** The caret on a collapsible section: points down when closed, up when open. */
export function Caret({ open }: { open: boolean }) {
  return (
    <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className={`size-4 shrink-0 transition-transform ${open ? 'rotate-180' : ''}`}>
      <path d="m5 8 5 5 5-5" />
    </svg>
  )
}

/** The button at the bottom of a chart card that opens its own page (DECISIONS.md 237, 240, 241). */
export function CardLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <a href={href} className="mt-4 flex items-center justify-between rounded-2xl neu-sm px-4 py-2.5 text-sm font-semibold">
      {children}
      <span className="text-ink-soft" aria-hidden>
        ›
      </span>
    </a>
  )
}

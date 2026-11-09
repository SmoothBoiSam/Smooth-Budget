// The app's building blocks (DECISIONS.md 187). Every screen is made of these, so cards, rows, notices and buttons look
// and behave the same everywhere. Neumorphic: everything is the page's own color, raised (neu) or pressed in (neu-inset).
import type { ReactNode } from 'react'

/** Every screen's frame: one width, one gutter, room above the bottom nav. */
export function Page({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`mx-auto min-h-screen max-w-md px-5 pt-[max(1.25rem,env(safe-area-inset-top))] pb-28 ${className}`}>{children}</div>
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

/** A small raised pill button for a header's action ("Add", "Edit"). */
export const headerActionCls = 'neu-sm shrink-0 rounded-full px-3.5 py-1.5 text-xs font-semibold text-primary'

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
  href,
  onClick,
}: {
  label: ReactNode
  icon?: ReactNode
  hint?: ReactNode
  value?: ReactNode
  tone?: 'good' | 'danger'
  href?: string
  onClick?: () => void
}) {
  const inner = (
    <>
      {icon && <span className="w-7 shrink-0">{icon}</span>}
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

/** A small rounded label, e.g. "On track". */
export function Pill({ tone, children }: { tone?: 'good' | 'danger' | 'todo'; children: ReactNode }) {
  const color = tone === 'good' ? 'text-good' : tone === 'danger' ? 'text-danger' : tone === 'todo' ? 'text-gold-ink' : 'text-ink-soft'
  return <span className={`neu-inset inline-block rounded-full px-3 py-1 text-xs font-semibold transition-colors duration-300 ${color}`}>{children}</span>
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

export function Chevron() {
  return (
    <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className="size-4 shrink-0 text-ink-faint">
      <path d="m8 5 5 5-5 5" />
    </svg>
  )
}

/** A placeholder in the shape of a screen's cards while it loads. */
export function Loading({ label = 'Loading' }: { label?: string }) {
  return (
    <div role="status" aria-live="polite" className="mt-6 space-y-4">
      <span className="sr-only">{label}…</span>
      <div className="neu shimmer h-64 rounded-3xl" />
      <div className="neu shimmer h-14 rounded-2xl" />
      <div className="neu shimmer h-14 rounded-2xl" />
    </div>
  )
}

/** A failed load, said plainly, with a way to try again. */
export function ErrorNotice({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" className="neu mt-4 rounded-2xl px-4 py-3 text-sm">
      <p className="text-danger">{message}</p>
      {onRetry && (
        <button type="button" onClick={onRetry} className="mt-2 font-semibold text-primary">
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

/** The caret on a collapsible section: points down when closed, up when open. */
export function Caret({ open }: { open: boolean }) {
  return (
    <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden className={`size-4 shrink-0 transition-transform ${open ? 'rotate-180' : ''}`}>
      <path d="m5 8 5 5 5-5" />
    </svg>
  )
}

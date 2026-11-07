// Shared loading and error states (milestone 10), so every screen behaves the same way.

/** A placeholder in the shape of a screen's cards while it loads. */
export function Loading({ label = 'Loading' }: { label?: string }) {
  return (
    <div role="status" aria-live="polite" className="mt-6 space-y-3">
      <span className="sr-only">{label}…</span>
      <div className="h-24 animate-pulse rounded-2xl bg-slate-200/70" />
      <div className="h-14 animate-pulse rounded-xl bg-slate-200/60" />
      <div className="h-14 animate-pulse rounded-xl bg-slate-200/50" />
    </div>
  )
}

/** A failed load, said plainly, with a way to try again. */
export function ErrorNotice({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" className="mt-4 rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">
      <p>{message}</p>
      {onRetry && (
        <button type="button" onClick={onRetry} className="mt-2 font-semibold underline">
          Try again
        </button>
      )}
    </div>
  )
}

/** A thin bar across the top while the phone has no connection. */
export function OfflineBar() {
  return (
    <div role="status" className="sticky top-0 z-20 bg-slate-900 px-4 py-1.5 pt-[max(0.375rem,env(safe-area-inset-top))] text-center text-xs font-medium text-white">
      You’re offline. Showing what was saved on this phone; changes need a connection.
    </div>
  )
}

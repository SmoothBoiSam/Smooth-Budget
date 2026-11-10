// "Are you sure?" inside the app, instead of the browser's confirm popup (DECISIONS.md 215). Some browsers, like the
// ones built into chat apps, close that popup without showing it, which made buttons quietly do nothing.
// `await ask(...)` opens the sheet that <ConfirmHost /> draws and resolves true for the confirm button, false otherwise.

export type ConfirmRequest = { message: string; yes: string; danger: boolean; answer: (ok: boolean) => void }

let current: ConfirmRequest | null = null
const listeners = new Set<() => void>()

export function ask(message: string, opts: { yes?: string; danger?: boolean } = {}): Promise<boolean> {
  current?.answer(false)
  return new Promise((resolve) => {
    const request: ConfirmRequest = {
      message,
      yes: opts.yes ?? 'OK',
      danger: opts.danger ?? false,
      answer: (ok) => {
        if (current === request) {
          current = null
          listeners.forEach((l) => l())
        }
        resolve(ok)
      },
    }
    current = request
    listeners.forEach((l) => l())
  })
}

export const confirmStore = {
  subscribe(l: () => void) {
    listeners.add(l)
    return () => {
      listeners.delete(l)
    }
  },
  get: () => current,
}

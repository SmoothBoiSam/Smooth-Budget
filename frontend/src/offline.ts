// What the app keeps on this device so it can open offline (milestone 10). Only the signed-in account and the last
// Home summary are kept, and both are cleared when you sign out or your session ends.
import { useEffect, useState } from 'react'
import type { HomeSummary, Me } from './api'

const ME_KEY = 'smoothbudget.me'
const HOME_KEY = 'smoothbudget.home'

function read<T>(key: string): T | null {
  try {
    const raw = localStorage.getItem(key)
    return raw ? (JSON.parse(raw) as T) : null
  } catch {
    return null
  }
}

function write(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value))
  } catch {
    /* storage full or blocked: offline just won't have it */
  }
}

export const saved = {
  me: () => read<Me>(ME_KEY),
  setMe: (me: Me) => write(ME_KEY, me),
  /** The last Home summary for this household, and when it was fetched. */
  home: (householdId: string) => {
    const h = read<{ householdId: string; savedUtc: string; summary: HomeSummary }>(HOME_KEY)
    return h && h.householdId === householdId ? h : null
  },
  setHome: (householdId: string, summary: HomeSummary) => write(HOME_KEY, { householdId, savedUtc: new Date().toISOString(), summary }),
  clear: () => {
    try {
      localStorage.removeItem(ME_KEY)
      localStorage.removeItem(HOME_KEY)
    } catch {
      /* nothing to clear */
    }
  },
}

/** Whether the browser thinks it's online. A server that can't be reached shows up as an ApiError with status 0. */
export function useOnline() {
  const [online, setOnline] = useState(() => (typeof navigator === 'undefined' ? true : navigator.onLine))
  useEffect(() => {
    const on = () => setOnline(true)
    const off = () => setOnline(false)
    window.addEventListener('online', on)
    window.addEventListener('offline', off)
    return () => {
      window.removeEventListener('online', on)
      window.removeEventListener('offline', off)
    }
  }, [])
  return online
}

// How long the app was last out of sight. One listener, set up before any screen's, so screens can read it when it returns.
let hiddenAt: number | null = null
let awayMs = 0
if (typeof document !== 'undefined')
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') hiddenAt = Date.now()
    else {
      awayMs = hiddenAt === null ? 0 : Date.now() - hiddenAt
      hiddenAt = null
    }
  })

const RESUME_AFTER_MS = 60_000

/**
 * Runs `reload` when the app comes back to the front after a minute or more away (DECISIONS.md 251). A phone keeps the
 * app open for days; without this it shows the numbers from whenever that screen was last opened.
 */
export function useResume(reload: () => void) {
  useEffect(() => {
    const onChange = () => {
      if (document.visibilityState === 'visible' && awayMs >= RESUME_AFTER_MS) reload()
    }
    document.addEventListener('visibilitychange', onChange)
    return () => document.removeEventListener('visibilitychange', onChange)
  }, [reload])
}

/** Open/closed state of a collapsible section, remembered on this device (DECISIONS.md 132). */
export function useRemembered(key: string, initial: boolean) {
  const [open, setOpen] = useState(() => {
    try {
      const v = localStorage.getItem('smoothbudget.open.' + key)
      return v === null ? initial : v === '1'
    } catch {
      return initial
    }
  })
  const set = (next: boolean) => {
    setOpen(next)
    try {
      localStorage.setItem('smoothbudget.open.' + key, next ? '1' : '0')
    } catch {
      /* not remembered: fine */
    }
  }
  return [open, set] as const
}

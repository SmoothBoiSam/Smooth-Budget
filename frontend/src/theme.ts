// Appearance preference: follow the phone's setting, or always light, or always dark.
// Stored per device in localStorage; the inline script in index.html applies it before first paint.
import { useEffect, useState } from 'react'

export type ThemeChoice = 'system' | 'light' | 'dark'

const KEY = 'smoothbudget.theme'

export function readTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY)
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch {
    return 'system' // private mode or blocked storage
  }
}

function systemPrefersDark(): boolean {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false
}

export function applyTheme(choice: ThemeChoice): void {
  const dark = choice === 'dark' || (choice === 'system' && systemPrefersDark())
  document.documentElement.classList.toggle('dark', dark)
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#282828' : '#e9ebf0')
}

/** The current choice and a setter that saves and applies it. Follows the system live when set to "system". */
export function useTheme(): [ThemeChoice, (c: ThemeChoice) => void] {
  const [choice, setChoice] = useState<ThemeChoice>(readTheme)

  useEffect(() => {
    applyTheme(choice)
    if (choice !== 'system') return
    const media = window.matchMedia?.('(prefers-color-scheme: dark)')
    const onChange = () => applyTheme('system')
    media?.addEventListener('change', onChange)
    return () => media?.removeEventListener('change', onChange)
  }, [choice])

  function set(c: ThemeChoice) {
    try {
      localStorage.setItem(KEY, c)
    } catch {
      /* still applies for this visit */
    }
    setChoice(c)
  }

  return [choice, set]
}

/** The count bubble's blue (DECISIONS.md 208): fixed colors, so the dark theme doesn't swap them, with white numbers. */
export const bubbleStyle = { backgroundColor: '#2160d9', backgroundImage: 'linear-gradient(160deg, #4a8af4, #1d4fc4)', color: '#fff' } as const

/**
 * What a count bubble says (DECISIONS.md 210): the number itself up to 999, then "999+". A longer number stretches the
 * bubble sideways into a pill instead of shrinking the text.
 */
export const bubbleText = (n: number) => (n > 999 ? '999+' : String(n))

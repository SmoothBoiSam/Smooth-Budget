// The app's motion (DECISIONS.md 190, 210): soft, unhurried, and never in the way. Only opacity and transform are animated where
// possible (the phone's graphics chip does those without redrawing), nothing blocks a tap, and with Reduce Motion on,
// all of it is off.
import { useEffect, useRef, useState } from 'react'

export const reducedMotion = () => typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

const easeOut = (p: number) => 1 - Math.pow(1 - p, 3)

/**
 * A number that glides to its target instead of jumping. It starts at `from` (0 for a count-up, or the target itself
 * to show it straight away) and follows later changes over `ms`.
 */
export function useTween(target: number, ms: number, from: number = target) {
  const [value, setValue] = useState(() => (reducedMotion() ? target : from))
  const current = useRef(value)
  useEffect(() => {
    const start = current.current
    if (start === target) return
    if (reducedMotion()) {
      current.current = target
      const id = requestAnimationFrame(() => setValue(target))
      return () => cancelAnimationFrame(id)
    }
    const frame = { id: 0 }
    const t0 = performance.now()
    const step = (t: number) => {
      const p = Math.min(1, (t - t0) / ms)
      current.current = p === 1 ? target : start + (target - start) * easeOut(p)
      setValue(current.current)
      if (p < 1) frame.id = requestAnimationFrame(step)
    }
    frame.id = requestAnimationFrame(step)
    return () => cancelAnimationFrame(frame.id)
  }, [target, ms])
  return value
}

/** Which items in a list arrived after it first showed, so only those slide in (the rest were already there). */
export function useArrivals(keys: string[]) {
  const [initial] = useState(() => new Set(keys))
  return (key: string) => !initial.has(key)
}

/**
 * Tab and page changes: the old screen fades out as the new one fades in (the browser's view transitions, where the
 * phone has them). Links within the app are hash links, so one listener covers every tap. Elsewhere, and for the back
 * button, the new screen fades in on its own (App).
 */
export function installPageTransitions() {
  // iPhones only show :active (the pressed look) once a page listens for touches.
  document.addEventListener('touchstart', () => undefined, { passive: true })

  document.addEventListener('click', (e) => {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return
    const a = (e.target as Element | null)?.closest?.('a[href^="#"]')
    if (!a || !('startViewTransition' in document) || reducedMotion()) return
    const href = a.getAttribute('href') ?? ''
    const here = window.location.hash || '#/'
    if (href === here || (href === '#/' && here === '#')) return
    e.preventDefault()
    viewTransitionRunning = true
    const t = document.startViewTransition(
      () =>
        new Promise<void>((resolve) => {
          window.location.hash = href
          // React draws the new screen right after the hash changes; the old picture stays up until then.
          setTimeout(resolve, 40)
        }),
    )
    void t.finished.finally(() => (viewTransitionRunning = false))
  })
}

let viewTransitionRunning = false
/** True while a tap's view transition is animating the change, so App doesn't fade the screen in a second time. */
export const inViewTransition = () => viewTransitionRunning


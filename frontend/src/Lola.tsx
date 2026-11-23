import { useEffect, useState, type KeyboardEvent, type MouseEvent } from 'react'
import { createPortal } from 'react-dom'
import { reducedMotion } from './motion'

/**
 * Lola, the orange tabby (DECISIONS.md 295): once in a while (1 in 30 pages opened, at most once a day) she's asleep on top
 * of something on the page (a card, a row, a button) at any spot along it, with z's drifting off her. Tap her and she hisses, then runs off the screen. She sits inside the card she's
 * on, so on a lower card she's drawn in front of the card above. Add ?lola to the address to see her every time.
 */
const ODDS = 30

const today = () => new Date().toLocaleDateString('en-CA')
function seenToday() {
  try {
    return localStorage.getItem('lola:day') === today()
  } catch {
    return false
  }
}
function markSeen() {
  try {
    localStorage.setItem('lola:day', today())
  } catch {
    // Private mode or blocked storage: she may nap again today, which is fine.
  }
}

/** Anything on the page she can sit on (a card, a row, a button), preferring one in view. */
function findPerch(): HTMLElement | null {
  const root = document.getElementById('root')
  if (!root) return null
  const fits = [...root.querySelectorAll<HTMLElement>('.neu, .neu-sm, .shadow-neu-sm, li, a, button')].filter((el) => {
    if (el.closest('nav, [role="dialog"], [role="alert"], .fixed, summary, .lola')) return false
    const r = el.getBoundingClientRect()
    if (r.width < 90 || r.height < 28) return false
    // Anything that clips above the card would cut her off.
    for (let a: HTMLElement | null = el; a && a !== root; a = a.parentElement) {
      const s = getComputedStyle(a)
      if (s.overflowY !== 'visible' || s.position === 'fixed' || s.position === 'sticky') return false
    }
    return true
  })
  const inView = fits.filter((el) => {
    const top = el.getBoundingClientRect().top
    return top > 110 && top < window.innerHeight - 170
  })
  const pool = inView.length > 0 ? inView : fits
  return pool[Math.floor(Math.random() * pool.length)] ?? null
}

/** A short cat hiss: a burst of filtered noise. Plays even with the phone on silent. */
function hiss() {
  try {
    const session = (navigator as Navigator & { audioSession?: { type: string } }).audioSession
    if (session) session.type = 'playback'
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    const ctx = new Ctx()
    const dur = 0.75
    const buf = ctx.createBuffer(1, Math.floor(ctx.sampleRate * dur), ctx.sampleRate)
    const data = buf.getChannelData(0)
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1
    const src = ctx.createBufferSource()
    src.buffer = buf
    const high = ctx.createBiquadFilter()
    high.type = 'highpass'
    high.frequency.value = 2600
    const band = ctx.createBiquadFilter()
    band.type = 'peaking'
    band.frequency.value = 5200
    band.gain.value = 8
    const gain = ctx.createGain()
    const t = ctx.currentTime
    gain.gain.setValueAtTime(0.0001, t)
    gain.gain.exponentialRampToValueAtTime(0.9, t + 0.03) // the spit
    gain.gain.exponentialRampToValueAtTime(0.45, t + 0.12)
    gain.gain.setValueAtTime(0.45, t + 0.45)
    gain.gain.exponentialRampToValueAtTime(0.0001, t + dur)
    src.connect(high).connect(band).connect(gain).connect(ctx.destination)
    src.start(t)
    src.stop(t + dur)
    src.onended = () => {
      void ctx.close()
      if (session) session.type = 'auto'
    }
  } catch {
    // No sound: she still hisses.
  }
}

export default function Lola() {
  const [perch, setPerch] = useState<HTMLElement | null>(null)
  const [mode, setMode] = useState<'sleep' | 'hiss' | 'run'>('sleep')
  const [dir, setDir] = useState<1 | -1>(1)
  // Where along the top she lies: anywhere from the left edge to the right (0 to 1).
  const [spot, setSpot] = useState(0.5)

  // A roll of the dice each time a page opens, once it has had a moment to load.
  useEffect(() => {
    let timer = 0
    const roll = () => {
      window.clearTimeout(timer)
      const force = window.location.search.includes('lola')
      timer = window.setTimeout(() => {
        const el = force || (!seenToday() && Math.random() * ODDS < 1) ? findPerch() : null
        if (el && !force) markSeen()
        setMode('sleep')
        setSpot(Math.random())
        setPerch(el)
      }, 1200)
    }
    const onPage = () => {
      setPerch(null)
      roll()
    }
    roll()
    window.addEventListener('hashchange', onPage)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('hashchange', onPage)
    }
  }, [])

  if (!perch) return null

  function wake(e: MouseEvent | KeyboardEvent) {
    e.preventDefault()
    e.stopPropagation()
    if (mode !== 'sleep') return
    hiss()
    setMode('hiss')
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
    setDir(r.left + r.width / 2 < window.innerWidth / 2 ? -1 : 1)
    if (reducedMotion()) {
      window.setTimeout(() => setPerch(null), 700)
      return
    }
    window.setTimeout(() => setMode('run'), 650)
    window.setTimeout(() => setPerch(null), 650 + 950)
  }

  return createPortal(
    <div
      role="button"
      tabIndex={0}
      aria-label={mode === 'sleep' ? 'Lola the cat, asleep on this card. Tap her.' : 'Lola hisses and runs off'}
      className="lola"
      data-mode={mode}
      style={{ ['--lola-dir' as string]: dir, left: `calc(${spot * 100}% - ${spot * 70}px)` }}
      onClick={wake}
      onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && wake(e)}
    >
      <div className="lola-face" style={{ transform: mode === 'run' && dir < 0 ? 'scaleX(-1)' : undefined }}>
        {mode === 'sleep' ? <Sleeping /> : <Hissing />}
      </div>
      {mode === 'sleep' && (
        <span className="lola-z" aria-hidden>
          <i>z</i>
          <i>z</i>
          <i>Z</i>
        </span>
      )}
      {mode === 'hiss' && (
        <span className="lola-hiss" aria-hidden>
          Hsss!
        </span>
      )}
    </div>,
    perch,
  )
}

// Drawn like the app's cat logo (DECISIONS.md 140): flat shapes with a white outline, pink blush. An orange tabby.
const O = { fur: '#F39C4A', stripe: '#C96A22', cream: '#FCE4C6', pink: '#F28DA8', line: '#4A2E1A', eye: '#C9D94A' }
const outline = { stroke: '#fff', strokeWidth: 2.4, strokeLinejoin: 'round' as const, style: { paintOrder: 'stroke' } as const }

/** Curled up in a loaf, facing right, tail wrapped round the front. */
function Sleeping() {
  return (
    <svg viewBox="0 0 64 46" width="64" height="46" aria-hidden className="lola-breathe">
      <g {...outline}>
        <ellipse cx="28" cy="31" rx="22" ry="12.5" fill={O.fur} />
        <polygon points="36.5,23 38,10.5 45.5,17.5" fill={O.fur} />
        <polygon points="47,17.5 54.5,10.5 55.5,23" fill={O.fur} />
        <circle cx="46" cy="27.5" r="11" fill={O.fur} />
      </g>
      {/* The tail, round the front, with its white edge. */}
      <path d="M7 32 C 3 41, 18 45, 37 42" fill="none" stroke="#fff" strokeWidth={8.4} strokeLinecap="round" />
      <path d="M7 32 C 3 41, 18 45, 37 42" fill="none" stroke={O.fur} strokeWidth={6} strokeLinecap="round" />
      <path d="M14 42.5 l1 -4.5 M21 43.6 l0.6 -4.8 M28 43.4 l0.2 -4.8" stroke={O.stripe} strokeWidth={1.6} strokeLinecap="round" />
      <ellipse cx="40.5" cy="41" rx="3.4" ry="2.3" fill={O.cream} {...outline} strokeWidth={1.6} />
      <ellipse cx="47.5" cy="41" rx="3.4" ry="2.3" fill={O.cream} {...outline} strokeWidth={1.6} />
      <polygon points="39.2,19.5 39.6,14 43.2,17.6" fill={O.pink} />
      <polygon points="48.8,17.6 52.8,14 53.2,19.5" fill={O.pink} />
      <path d="M43.5 18.6 v3.2 M46 17.6 v3.8 M48.5 18.6 v3.2" stroke={O.stripe} strokeWidth={1.4} strokeLinecap="round" />
      <path d="M13 21.5 q2.4 4.2 0 8.6 M20.5 19.5 q2.6 4.8 0 9.6 M28 19 q2.6 4.8 0 9.6" fill="none" stroke={O.stripe} strokeWidth={2} strokeLinecap="round" />
      <ellipse cx="46" cy="32" rx="5.2" ry="3.6" fill={O.cream} />
      <path d="M39.4 27 q2.3 2.5 4.6 0 M48 27 q2.3 2.5 4.6 0" fill="none" stroke={O.line} strokeWidth={1.6} strokeLinecap="round" />
      <path d="M44.8 30 h2.4 l-1.2 1.4 z" fill={O.pink} />
      <circle cx="39.6" cy="31" r="1.7" fill={O.pink} fillOpacity={0.75} />
      <circle cx="52.4" cy="31" r="1.7" fill={O.pink} fillOpacity={0.75} />
    </svg>
  )
}

/** Up on her toes: back arched, fur and tail puffed, ears flat, mouth open. Facing right. */
function Hissing() {
  return (
    <svg viewBox="0 0 70 54" width="70" height="54" aria-hidden className="lola-shake">
      <g {...outline}>
        <path d="M13 33 L6 29 L9 25 L3 21 L8 17 L4 12 L10 10 L8 4 L14 7 L16 1 L18 8 L22 9 L18 14 L22 18 L17 22 L21 27 L17 31 Z" fill={O.fur} />
        <rect x="14" y="34" width="5" height="16" rx="2.2" fill={O.fur} />
        <rect x="21.5" y="34" width="5" height="16" rx="2.2" fill={O.fur} />
        <rect x="39" y="34" width="5" height="16" rx="2.2" fill={O.fur} />
        <rect x="46.5" y="34" width="5" height="16" rx="2.2" fill={O.fur} />
        <polygon points="25,15 27,7 30,13" fill={O.fur} />
        <polygon points="30,12.5 33,5 35.5,12" fill={O.fur} />
        <polygon points="36,13 40,7 40.5,15" fill={O.fur} />
        <path d="M9 41 C 8 12, 52 8, 55 38 C 46 33, 18 33, 9 41 Z" fill={O.fur} />
        <polygon points="47,27 41,19.5 51.5,22" fill={O.fur} />
        <polygon points="58.5,22 69,19.5 63,27" fill={O.fur} />
        <circle cx="55" cy="31" r="10.5" fill={O.fur} />
      </g>
      <path d="M21 18.5 q-1.4 5 0.8 9.5 M29.5 15.5 q-1.4 5.2 0.8 10.5 M38 16 q-1.4 5 0.8 9.8" fill="none" stroke={O.stripe} strokeWidth={2} strokeLinecap="round" />
      <path d="M11 22 l5 1.2 M10 15 l5 1.4" stroke={O.stripe} strokeWidth={1.6} strokeLinecap="round" />
      <path d="M52.5 22 v3 M55 21.4 v3.4 M57.5 22 v3" stroke={O.stripe} strokeWidth={1.4} strokeLinecap="round" />
      <ellipse cx="55" cy="35.5" rx="5.4" ry="3.8" fill={O.cream} />
      <circle cx="51" cy="29" r="2.7" fill={O.eye} />
      <circle cx="59" cy="29" r="2.7" fill={O.eye} />
      <rect x="50.6" y="26.6" width="0.9" height="4.8" rx="0.45" fill={O.line} />
      <rect x="58.6" y="26.6" width="0.9" height="4.8" rx="0.45" fill={O.line} />
      <path d="M47.6 25.4 l4.6 1.6 M62.4 25.4 l-4.6 1.6" stroke={O.line} strokeWidth={1.5} strokeLinecap="round" />
      <path d="M51.6 35 q3.4 5.4 6.8 0 z" fill="#7A2430" />
      <path d="M52.6 35.3 l0.9 1.9 l0.8 -1.9 z M55.8 35.3 l0.8 1.9 l0.9 -1.9 z" fill="#fff" />
      <path d="M45 34 l-6 -1 M45 36 l-6 1 M65 34 l5 -1 M65 36 l5 1" stroke="#fff" strokeOpacity={0.7} strokeWidth={0.9} strokeLinecap="round" />
    </svg>
  )
}

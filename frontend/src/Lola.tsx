import { useEffect, useState, type KeyboardEvent, type MouseEvent } from 'react'
import { createPortal } from 'react-dom'
import { DracoOnBar, DracoOnCard, Slithering } from './Draco'
import { reducedMotion } from './motion'
import { takeSummoned } from './summon'

/**
 * The pets (DECISIONS.md 295, 297). Each time a page opens, one roll of the dice:
 * - Lola, the orange tabby (1 in 30, at most once a day): asleep on top of a card or a button at any spot along it, with
 *   z's drifting off her. Tap her and she hisses, then runs off the screen. She sits inside the card she's on, so on a
 *   lower card she's drawn in front of the card above.
 * - Draco, the striped snake (1 in 60, at most once a day): coiled round a progress bar (Draco.tsx), or on a page with
 *   no bars, lying on a card or a button.
 * - Very rarely (1 in 400), Draco slithers up to Lola while she naps. Tap either: she hisses at him and they bolt
 *   opposite ways.
 * Add ?lola, ?draco or ?meet to the address to see one every time.
 */
const ODDS = { meet: 400, lola: 30, draco: 60 }

const today = () => new Date().toLocaleDateString('en-CA')
function seenToday(pet: 'lola' | 'draco') {
  try {
    return localStorage.getItem(`${pet}:day`) === today()
  } catch {
    return false
  }
}
function markSeen(pet: 'lola' | 'draco') {
  try {
    localStorage.setItem(`${pet}:day`, today())
  } catch {
    // Private mode or blocked storage: they may visit again today, which is fine.
  }
}

type Kind = 'lola' | 'meet' | 'draco'
type Visit =
  | { id: number; kind: 'lola' | 'meet' | 'draco-card'; perch: HTMLElement; spot: number }
  | { id: number; kind: 'draco'; left: number; mid: number; span: number }

const forced = (): Kind | null => {
  const q = window.location.search
  return q.includes('meet') ? 'meet' : q.includes('draco') ? 'draco' : q.includes('lola') ? 'lola' : null
}

/** Who visits this page, if anyone: one roll of the dice. */
function roll(): Kind | null {
  const r = Math.random()
  const [meet, lola, draco] = [1 / ODDS.meet, 1 / ODDS.lola, 1 / ODDS.draco]
  if (r < meet) return !seenToday('lola') && !seenToday('draco') ? 'meet' : null
  if (r < meet + lola) return !seenToday('lola') ? 'lola' : null
  if (r < meet + lola + draco) return !seenToday('draco') ? 'draco' : null
  return null
}

/**
 * Where on this page they go, or null when there's nowhere yet (the page may still be loading). Draco takes a bar when
 * there is one; with none, once the page has had time to load (anyCard), he lies on a card or a button instead.
 */
function place(kind: Kind, forced: boolean, anyCard: boolean): Visit | null {
  const id = Date.now()
  if (kind === 'draco') {
    const bar = findBar()
    const perch = bar || !anyCard ? null : findPerch(110)
    if ((bar || perch) && !forced) markSeen('draco')
    if (bar) return { id, kind, ...bar }
    return perch ? { id, kind: 'draco-card', perch, spot: Math.random() } : null
  }
  // Meeting: a wide card, with Lola near one end and room for Draco at the other. Lola fades in (index.css).
  const perch = findPerch(kind === 'meet' ? 230 : 90)
  if (!perch) return null
  if (!forced) {
    markSeen('lola')
    if (kind === 'meet') markSeen('draco')
  }
  const spot = kind === 'meet' ? (Math.random() < 0.5 ? 0.05 + Math.random() * 0.25 : 0.7 + Math.random() * 0.25) : Math.random()
  return { id, kind, perch, spot }
}

/** A bar he can coil round: a thin rounded track (budget, debt, savings, Home), preferring one in view. */
function findBar(): { left: number; mid: number; span: number } | null {
  const root = document.getElementById('root')
  if (!root) return null
  const fits = [...root.querySelectorAll<HTMLElement>('.neu-inset.rounded-full.overflow-hidden')].filter((el) => {
    if (el.closest('nav, [role="dialog"], .fixed')) return false
    const r = el.getBoundingClientRect()
    return r.width >= 110 && r.height > 0 && r.height <= 12
  })
  const inView = fits.filter((el) => {
    const top = el.getBoundingClientRect().top
    return top > 120 && top < window.innerHeight - 150
  })
  const pool = inView.length > 0 ? inView : fits
  const bar = pool[Math.floor(Math.random() * pool.length)]
  if (!bar) return null
  const r = bar.getBoundingClientRect()
  // He wraps part of it, anywhere along it.
  const span = Math.round(Math.min(105, Math.max(60, r.width * 0.35)))
  const start = r.left + Math.random() * (r.width - span)
  return { left: start + window.scrollX, mid: r.top + window.scrollY + r.height / 2, span }
}

/** A card or a button on the page for her to sit on (not a row in a list), preferring one in view. */
function findPerch(minWidth: number): HTMLElement | null {
  const root = document.getElementById('root')
  if (!root) return null
  const fits = [...root.querySelectorAll<HTMLElement>('.neu, .neu-sm, .shadow-neu-sm, a, button')].filter((el) => {
    if (el.closest('nav, [role="dialog"], [role="alert"], .fixed, summary, li, .lola')) return false
    const r = el.getBoundingClientRect()
    if (r.width < minWidth || r.height < 28) return false
    // A link or button only when it looks like one: raised or filled, not plain text.
    if (el.matches('a, button') && !el.matches('.neu, .neu-sm, .shadow-neu-sm')) {
      const s = getComputedStyle(el)
      const filled = !/^(transparent|rgba\(0, 0, 0, 0\))$/.test(s.backgroundColor)
      if (s.boxShadow === 'none' && !filled) return false
    }
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

export default function Pets() {
  const [visit, setVisit] = useState<Visit | null>(null)

  // A roll of the dice each time a page opens, once it has had a moment to load.
  useEffect(() => {
    let timer = 0
    // One roll per page; then, while the page is still loading, a few more looks for somewhere to be.
    const visitPage = () => {
      window.clearTimeout(timer)
      const force = forced() ?? takeSummoned()
      const kind = force ?? roll()
      if (!kind) return
      const look = (tries: number) => {
        const v = place(kind, !!force, tries <= 3)
        if (v) setVisit(v)
        else if (tries > 0) timer = window.setTimeout(() => look(tries - 1), 900)
      }
      timer = window.setTimeout(() => look(5), 1200)
    }
    const onPage = () => {
      setVisit(null)
      visitPage()
    }
    visitPage()
    window.addEventListener('hashchange', onPage)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('hashchange', onPage)
    }
  }, [])

  if (!visit) return null
  const gone = () => setVisit(null)
  return visit.kind === 'draco' ? (
    <DracoOnBar key={visit.id} left={visit.left} mid={visit.mid} span={visit.span} onGone={gone} />
  ) : visit.kind === 'draco-card' ? (
    <DracoOnCard key={visit.id} perch={visit.perch} spot={visit.spot} onGone={gone} />
  ) : (
    <LolaNap key={visit.id} perch={visit.perch} spot={visit.spot} meet={visit.kind === 'meet'} onGone={gone} />
  )
}

/** Lola asleep on a card; with meet, Draco slithers up beside her (from the side with more room). */
function LolaNap({ perch, spot, meet, onGone }: { perch: HTMLElement; spot: number; meet: boolean; onGone: () => void }) {
  const [mode, setMode] = useState<'sleep' | 'hiss' | 'run'>('sleep')
  const [dir, setDir] = useState<1 | -1>(1)
  // In a meeting, Draco comes from the end of the card she isn't at.
  const dracoLeft = spot > 0.5
  const at = `${spot * 100}% - ${spot * 70}px`

  function wake(e: MouseEvent | KeyboardEvent) {
    e.preventDefault()
    e.stopPropagation()
    if (mode !== 'sleep') return
    hiss()
    setMode('hiss')
    if (meet) setDir(dracoLeft ? 1 : -1) // away from him
    else {
      const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
      setDir(r.left + r.width / 2 < window.innerWidth / 2 ? -1 : 1)
    }
    if (reducedMotion()) {
      window.setTimeout(onGone, 700)
      return
    }
    window.setTimeout(() => setMode('run'), 650)
    window.setTimeout(onGone, 650 + 1050)
  }
  const keys = (e: KeyboardEvent) => (e.key === 'Enter' || e.key === ' ') && wake(e)
  // She faces right; she turns to hiss at Draco when he's on her left, and to run left.
  const flip = mode === 'run' ? dir < 0 : mode === 'hiss' && meet && dracoLeft
  // Draco faces right; he faces Lola until he flees back the way he came.
  const dracoFlip = mode === 'run' ? dracoLeft : !dracoLeft

  return createPortal(
    <>
      <div
        role="button"
        tabIndex={0}
        aria-label={mode === 'sleep' ? 'Lola the cat, asleep on this card. Tap her.' : 'Lola hisses and runs off'}
        className="lola"
        data-mode={mode}
        style={{ ['--lola-dir' as string]: dir, left: `calc(${at})` }}
        onClick={wake}
        onKeyDown={keys}
      >
        <div className="lola-face" style={{ transform: flip ? 'scaleX(-1)' : undefined }}>
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
          <span className="lola-hiss" aria-hidden style={meet && dracoLeft ? { right: 'auto', left: '-14px' } : undefined}>
            Hsss!
          </span>
        )}
      </div>
      {meet && (
        <div
          role="button"
          tabIndex={0}
          aria-label="Draco the snake, sneaking up on Lola"
          className="draco-ground"
          data-mode={mode}
          style={{
            left: dracoLeft ? `calc(${at} - 92px)` : `calc(${at} + 76px)`,
            ['--draco-from' as string]: dracoLeft ? '-110vw' : '110vw',
            ['--draco-dir' as string]: dracoLeft ? -1 : 1,
          }}
          onClick={wake}
          onKeyDown={keys}
        >
          <div style={{ transform: dracoFlip ? 'scaleX(-1)' : undefined }}>
            <Slithering />
          </div>
        </div>
      )}
    </>,
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
        <rect x="41.5" y="34" width="5" height="16" rx="2.2" fill={O.fur} />
        <rect x="48.5" y="34" width="5" height="16" rx="2.2" fill={O.fur} />
        <polygon points="25,18.5 27,10.5 30,16" fill={O.fur} />
        <polygon points="30,15.5 33,8 35.5,15" fill={O.fur} />
        <polygon points="36,16 40,10.5 40.5,18.5" fill={O.fur} />
        {/* Arched, with a round haunch behind and a round chest in front (not pointed ends). */}
        <path d="M12.5 38 C 11 31, 14 22, 20.5 18 C 27 14, 37 14, 43.5 18.5 C 49 22.5, 53 29, 53 36 C 53 41.5, 46 42.5, 44 37.5 C 40.5 31.5, 25 31.5, 21.5 37.5 C 19 42.5, 13 43, 12.5 38 Z" fill={O.fur} />
        <polygon points="47,27 41,19.5 51.5,22" fill={O.fur} />
        <polygon points="58.5,22 69,19.5 63,27" fill={O.fur} />
        <circle cx="55" cy="31" r="10.5" fill={O.fur} />
      </g>
      <path d="M23.5 19.5 q-1.4 4.6 0.8 8.8 M31 17 q-1.4 5 0.8 9.6 M38.5 17.5 q-1.4 4.8 0.8 9.2" fill="none" stroke={O.stripe} strokeWidth={2} strokeLinecap="round" />
      <path d="M10.5 22 l3.6 0.9 M10 15 l5 1.4" stroke={O.stripe} strokeWidth={1.6} strokeLinecap="round" />
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

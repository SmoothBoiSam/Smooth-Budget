import { useState, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { reducedMotion } from './motion'

/**
 * Draco, the pink and yellow striped snake with red eyes (DECISIONS.md 297). Now and then (rarer than Lola) he's coiled
 * round a progress bar, flicking his tongue; tap him and he pulls back and slithers off. Very rarely he slithers up to
 * Lola instead (see Lola.tsx). He makes no sound.
 */

// Drawn like the cat logo (DECISIONS.md 140): flat shapes, a white outline. Pink with yellow stripes, red eyes.
const D = { pink: '#F48FB1', under: '#D9668F', yellow: '#FFD54F', eye: '#E53935', tongue: '#D32F2F' }

const TAIL = 14 // room for his tail, left of the first coil
const PITCH = 12 // one wrap
const CY = 18 // the bar's middle, in his drawing

/** Coiled round a bar, facing right: front-of-bar wraps with a hook top and bottom where he goes behind it. */
function Coiled({ span }: { span: number }) {
  const k = Math.max(4, Math.floor(span / PITCH))
  const width = TAIL + k * PITCH + 34
  const fronts: string[] = []
  const hooks: string[] = []
  for (let i = 0; i < k; i++) {
    const x = TAIL + i * PITCH
    fronts.push(`M${x} ${CY - 7} L${x + 7} ${CY + 7}`)
    if (i > 0) hooks.push(`M${x - 3} ${CY - 3} Q${x - 2.5} ${CY - 8} ${x} ${CY - 7}`)
    if (i < k - 1) hooks.push(`M${x + 7} ${CY + 7} Q${x + 9.5} ${CY + 8} ${x + 9.5} ${CY + 3}`)
  }
  const xe = TAIL + (k - 1) * PITCH + 7
  // The tail narrows from the body's width to a point, with a stripe or two on the way.
  const tail = `M${TAIL + 1.6} ${CY - 9.4} Q${TAIL - 6} ${CY - 14.2} 1.8 ${CY - 4.6} Q${TAIL - 6} ${CY - 8} ${TAIL - 1.4} ${CY - 4.6} Z`
  const tailStripes = `M${TAIL - 1.8} ${CY - 10} L${TAIL - 3} ${CY - 6.4} M${TAIL - 5.7} ${CY - 10.1} L${TAIL - 6.3} ${CY - 6.9} M${TAIL - 8.6} ${CY - 8.4} L${TAIL - 9.4} ${CY - 6.9}`
  const neck = `M${xe} ${CY + 7} Q${xe + 9} ${CY + 10} ${xe + 11} ${CY - 1} Q${xe + 12} ${CY - 11} ${xe + 18} ${CY - 12}`
  const body = [...fronts, neck].join(' ')
  return (
    <svg viewBox={`0 0 ${width} 30`} width={width} height="30" aria-hidden>
      <path d={hooks.join(' ')} fill="none" stroke={D.under} strokeWidth={4.6} strokeLinecap="round" />
      <Tail d={tail} stripes={tailStripes}>
        <Striped d={body} />
      </Tail>
      <Head x={xe + 22} y={CY - 12} />
    </svg>
  )
}

/** Lying along a card in an S, facing right, head up. */
export function Slithering() {
  const body = 'M5 24 C 12 17, 18 31, 28 24 S 44 17, 52 24 Q 58 28 61 19 Q 63 13 68 12'
  return (
    <svg viewBox="0 0 92 30" width="92" height="30" aria-hidden className="draco-body">
      <g transform="translate(6 0)">
        <Tail d="M3 22 Q -0.6 25.4 -4.4 28 Q 1.2 29.3 7 26 Z" stripes="M3.6 27.4 L1.7 24.3 M-0.1 28.4 L-0.7 26.2">
          <Striped d={body} />
        </Tail>
        <Head x={72} y={12} />
      </g>
    </svg>
  )
}

/** Pink body with yellow bands across it, and the white outline. */
function Striped({ d }: { d: string }) {
  return (
    <>
      <path d={d} fill="none" stroke="#fff" strokeWidth={8.2} strokeLinecap="round" strokeLinejoin="round" />
      <path d={d} fill="none" stroke={D.pink} strokeWidth={5.6} strokeLinecap="round" strokeLinejoin="round" />
      <path d={d} fill="none" stroke={D.yellow} strokeWidth={5.6} strokeDasharray="2.6 2.8" strokeLinejoin="round" />
    </>
  )
}

/**
 * A tail that narrows to a point. Its outline goes under the body; it's filled again over the body, so the white edge of
 * the body's round end doesn't cut a line across where the tail joins.
 */
function Tail({ d, stripes, children }: { d: string; stripes: string; children: ReactNode }) {
  return (
    <>
      <path d={d} fill={D.pink} stroke="#fff" strokeWidth={2.6} strokeLinejoin="round" />
      {children}
      <path d={d} fill={D.pink} />
      <path d={stripes} fill="none" stroke={D.yellow} strokeWidth={2.1} />
    </>
  )
}

/** His head, facing right: red eye with a glint, a yellow stripe on top, and a forked tongue that flicks. */
function Head({ x, y }: { x: number; y: number }) {
  return (
    <g>
      <path className="draco-tongue" d={`M${x + 6} ${y + 0.6} h4.4 l2 -1.6 M${x + 10.4} ${y + 0.6} l2 1.6`} fill="none" stroke={D.tongue} strokeWidth={1.1} strokeLinecap="round" />
      <ellipse cx={x} cy={y} rx={6.8} ry={4.8} fill={D.pink} stroke="#fff" strokeWidth={2.4} style={{ paintOrder: 'stroke' }} />
      <path d={`M${x - 5} ${y - 2.4} q4 -2.6 8 -0.4`} fill="none" stroke={D.yellow} strokeWidth={1.8} strokeLinecap="round" />
      <circle cx={x + 2} cy={y - 1} r={1.6} fill={D.eye} />
      <circle cx={x + 2.5} cy={y - 1.5} r={0.45} fill="#fff" />
      <circle cx={x + 5.4} cy={y + 1.2} r={0.35} fill="#7A2430" />
    </g>
  )
}

/** Draco coiled round a bar; tapped, he pulls back and slithers off to the right. */
export function DracoOnBar({ left, mid, span, onGone }: { left: number; mid: number; span: number; onGone: () => void }) {
  const [mode, setMode] = useState<'coil' | 'startled' | 'run'>('coil')
  function startle(e: MouseEvent | KeyboardEvent) {
    e.preventDefault()
    e.stopPropagation()
    if (mode !== 'coil') return
    setMode('startled')
    if (reducedMotion()) {
      window.setTimeout(onGone, 400)
      return
    }
    window.setTimeout(() => setMode('run'), 420)
    window.setTimeout(onGone, 420 + 1100)
  }
  return createPortal(
    <div
      role="button"
      tabIndex={0}
      aria-label={mode === 'coil' ? 'Draco the snake, coiled round this bar. Tap him.' : 'Draco slithers off'}
      className="draco-bar"
      data-mode={mode}
      style={{ left: left - TAIL, top: mid - CY }}
      onClick={startle}
      onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && startle(e)}
    >
      <Coiled span={span} />
    </div>,
    document.body,
  )
}

/**
 * On a page with no bars, Draco lies stretched out on top of a card or a button at any spot along it (like Lola). Tapped,
 * he pulls back and slithers off toward the nearer side of the screen.
 */
export function DracoOnCard({ perch, spot, onGone }: { perch: HTMLElement; spot: number; onGone: () => void }) {
  const [mode, setMode] = useState<'rest' | 'startled' | 'run'>('rest')
  const [dir, setDir] = useState<1 | -1>(1)
  function startle(e: MouseEvent | KeyboardEvent) {
    e.preventDefault()
    e.stopPropagation()
    if (mode !== 'rest') return
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
    setDir(r.left + r.width / 2 < window.innerWidth / 2 ? -1 : 1)
    setMode('startled')
    if (reducedMotion()) {
      window.setTimeout(onGone, 400)
      return
    }
    window.setTimeout(() => setMode('run'), 420)
    window.setTimeout(onGone, 420 + 1100)
  }
  return createPortal(
    <div
      role="button"
      tabIndex={0}
      aria-label={mode === 'rest' ? 'Draco the snake, lying on this card. Tap him.' : 'Draco slithers off'}
      className="draco-ground draco-rest"
      data-mode={mode}
      style={{ left: `calc(${spot * 100}% - ${spot * 92}px)`, ['--draco-dir' as string]: dir }}
      onClick={startle}
      onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && startle(e)}
    >
      {/* He faces right; he turns round to slither off to the left. */}
      <div style={{ transform: mode === 'run' && dir < 0 ? 'scaleX(-1)' : undefined }}>
        <Slithering />
      </div>
    </div>,
    perch,
  )
}

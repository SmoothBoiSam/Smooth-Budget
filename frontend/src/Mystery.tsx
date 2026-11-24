import { useState, type CSSProperties } from 'react'
import { summonNext } from './summon'
import { reducedMotion } from './motion'

type Mark = { id: number; dx: number; rise: number; turn: number; delay: number; size: number }

/**
 * The "?" at the bottom of More (DECISIONS.md 311). It says nothing about what it does: tapped, purple question marks
 * float out of it and fade, and the next page you open has Lola, Draco, or the two of them meeting.
 */
export default function Mystery() {
  const [marks, setMarks] = useState<Mark[]>([])

  function tap() {
    summonNext()
    if (reducedMotion()) return
    const now = Date.now()
    const burst = Array.from({ length: 7 }, (_, i) => ({
      id: now + i,
      dx: Math.round((Math.random() - 0.5) * 90),
      rise: 50 + Math.round(Math.random() * 45),
      turn: Math.round((Math.random() - 0.5) * 50),
      delay: Math.round(Math.random() * 180),
      size: 14 + Math.round(Math.random() * 10),
    }))
    setMarks((m) => [...m, ...burst])
    window.setTimeout(() => setMarks((m) => m.filter((x) => x.id < now)), 1400)
  }

  return (
    <div className="mt-8 flex justify-center pb-2">
      <span className="relative">
        <button type="button" onClick={tap} className="neu-sm grid size-10 place-items-center rounded-full text-base font-semibold text-ink-soft">
          ?
        </button>
        {marks.map((m) => (
          <span
            key={m.id}
            aria-hidden
            className="mystery-mark pointer-events-none absolute left-1/2 top-1/2 font-bold"
            style={
              {
                '--dx': `${m.dx}px`,
                '--rise': `-${m.rise}px`,
                '--turn': `${m.turn}deg`,
                animationDelay: `${m.delay}ms`,
                fontSize: m.size,
              } as CSSProperties
            }
          >
            ?
          </span>
        ))}
      </span>
    </div>
  )
}

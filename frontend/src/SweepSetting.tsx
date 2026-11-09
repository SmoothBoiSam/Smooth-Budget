import { useEffect, useState } from 'react'
import { budgetEditApi, type SweepTarget } from './api'

const options: { value: SweepTarget; label: string }[] = [
  { value: 'Debt', label: 'Debt' },
  { value: 'Savings', label: 'Savings' },
  { value: 'Split', label: 'Split' },
]

/** Settings → where a month's leftover money goes (SPEC section 3; default: debt). */
export default function SweepSetting() {
  const [target, setTarget] = useState<SweepTarget | null>(null)
  const [percent, setPercent] = useState(50)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    budgetEditApi
      .settings()
      .then((s) => {
        setTarget(s.sweepTarget)
        setPercent(s.sweepSavingsPercent)
      })
      .catch(() => undefined)
  }, [])

  async function save(t: SweepTarget, p: number) {
    setTarget(t)
    setPercent(p)
    setSaved(false)
    await budgetEditApi.saveSettings(t, p)
    setSaved(true)
  }

  if (target === null) return null

  return (
    <div className="rounded-2xl neu px-4 py-3">
      <p id="sweep-label" className="text-sm font-medium">
        Leftover money goes to
      </p>
      <p className="text-xs text-ink-soft">When a month ends under budget, the app suggests where to send what's left.</p>
      <div role="radiogroup" aria-labelledby="sweep-label" className="mt-3 grid grid-cols-3 gap-1.5 rounded-lg neu-inset p-1">
        {options.map((o) => (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={target === o.value}
            onClick={() => save(o.value, percent)}
            className={`rounded-md py-2 text-sm font-medium ${target === o.value ? 'neu-sm text-accent' : 'text-ink-soft'}`}
          >
            {o.label}
          </button>
        ))}
      </div>
      {target === 'Split' && (
        <label className="mt-3 block text-sm">
          <span className="flex justify-between">
            <span>To savings</span>
            <span className="tabular-nums">
              {percent}% savings · {100 - percent}% debt
            </span>
          </span>
          <input
            type="range"
            min={0}
            max={100}
            step={5}
            value={percent}
            onChange={(e) => setPercent(Number(e.target.value))}
            onPointerUp={() => save('Split', percent)}
            onKeyUp={() => save('Split', percent)}
            className="mt-2 w-full"
          />
        </label>
      )}
      {target === 'Debt' && <p className="mt-2 text-xs text-ink-soft">Goes to the card with the largest balance.</p>}
      {saved && <p role="status" className="mt-2 text-xs text-good">Saved. Applies to months that close from now on.</p>}
    </div>
  )
}

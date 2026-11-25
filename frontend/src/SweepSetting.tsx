import { useEffect, useState } from 'react'
import { budgetEditApi, savingsShare, type SweepTarget } from './api'

/**
 * Budget settings → the one savings/debt split (DECISIONS.md 321): what's left each month after bills, spending and card
 * minimums is planned this way, and so is a closed month's leftover. Debt and Savings show what it gives them.
 */
export default function SweepSetting() {
  const [percent, setPercent] = useState<number | null>(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    budgetEditApi
      .settings()
      .then((s) => setPercent(savingsShare(s)))
      .catch(() => undefined)
  }, [])

  async function save(p: number) {
    setSaved(false)
    const target: SweepTarget = p === 0 ? 'Debt' : p === 100 ? 'Savings' : 'Split'
    await budgetEditApi.saveSettings(target, p)
    setSaved(true)
  }

  if (percent === null) return null

  return (
    <div id="split" className="mt-4 rounded-2xl neu px-4 py-3">
      <p id="split-label" className="text-sm font-medium">
        Extra money
      </p>
      <p className="text-xs text-ink-soft">
        What’s left each month after bills, spending and card minimums is shared this way. So is anything unspent when a month ends.
      </p>
      <label className="mt-3 block text-sm">
        <span className="flex justify-between tabular-nums">
          <span>
            Savings <span className="font-semibold">{percent}%</span>
          </span>
          <span>
            Debt <span className="font-semibold">{100 - percent}%</span>
          </span>
        </span>
        <input
          type="range"
          min={0}
          max={100}
          step={5}
          value={percent}
          aria-labelledby="split-label"
          aria-valuetext={`${percent}% to savings, ${100 - percent}% to debt`}
          onChange={(e) => setPercent(Number(e.target.value))}
          onPointerUp={() => void save(percent)}
          onKeyUp={() => void save(percent)}
          className="mt-2 w-full accent-accent"
        />
      </label>
      <p className="mt-1 text-xs text-ink-soft">
        Savings is shared between your savings accounts as set on Savings. Debt goes to your cards as extra beyond the minimums. With no card debt, it all
        goes to savings.
      </p>
      {saved && (
        <p role="status" className="mt-2 text-xs text-good">
          Saved. This month’s budget follows it now.
        </p>
      )}
    </div>
  )
}

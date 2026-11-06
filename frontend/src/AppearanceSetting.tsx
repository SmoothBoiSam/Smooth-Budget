import { useTheme, type ThemeChoice } from './theme'

const options: { value: ThemeChoice; label: string }[] = [
  { value: 'system', label: 'System' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
]

/** Settings → Appearance: follow the phone, or force light or dark. */
export default function AppearanceSetting() {
  const [choice, setChoice] = useTheme()

  return (
    <div className="rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
      <p id="appearance-label" className="text-sm font-medium">
        Appearance
      </p>
      <p className="text-xs text-slate-500">System follows your phone's light or dark setting.</p>
      <div role="radiogroup" aria-labelledby="appearance-label" className="mt-3 grid grid-cols-3 gap-1 rounded-lg bg-slate-100 p-1">
        {options.map((o) => {
          const selected = o.value === choice
          return (
            <button
              key={o.value}
              type="button"
              role="radio"
              aria-checked={selected}
              onClick={() => setChoice(o.value)}
              className={`rounded-md py-2 text-sm font-medium ${selected ? 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-200' : 'text-slate-500'}`}
            >
              {o.label}
            </button>
          )
        })}
      </div>
    </div>
  )
}

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
    <div className="flex items-center gap-3 px-5 py-3">
      <p id="appearance-label" className="flex-1 text-sm">
        Appearance
      </p>
      <div role="radiogroup" aria-labelledby="appearance-label" className="grid grid-cols-3 gap-1 rounded-full neu-inset p-1">
        {options.map((o) => {
          const selected = o.value === choice
          return (
            <button
              key={o.value}
              type="button"
              role="radio"
              aria-checked={selected}
              onClick={() => setChoice(o.value)}
              className={`rounded-full px-3 py-1 text-xs font-semibold ${selected ? 'neu-sm text-primary' : 'text-ink-soft'}`}
            >
              {o.label}
            </button>
          )
        })}
      </div>
    </div>
  )
}

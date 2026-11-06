import { useEffect, useState, type FormEvent } from 'react'
import { partnerApi } from './api'
import { formatCents } from './format'

/**
 * Settings → Partner: the name on her transfers ("Zelle payment from …"). Money in from that name is
 * tracked as partner contributions, separate from your own income (SPEC Appendix D).
 */
export default function PartnerSetting({ onChanged }: { onChanged: () => void }) {
  const [saved, setSaved] = useState<string | null | undefined>(undefined)
  const [name, setName] = useState('')
  const [suggestions, setSuggestions] = useState<{ name: string; count: number; totalCents: number }[]>([])
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    Promise.all([partnerApi.get(), partnerApi.suggestions()])
      .then(([p, s]) => {
        setSaved(p.partnerName)
        setName(p.partnerName ?? '')
        setSuggestions(s)
      })
      .catch(() => setSaved(null))
  }, [])

  async function save(value: string | null) {
    setBusy(true)
    setMessage(null)
    try {
      const r = await partnerApi.save(value)
      setSaved(value)
      setName(value ?? '')
      setMessage(value ? `Found ${r.partnerContributions} transfers from ${value}. They're no longer counted as your income.` : 'Partner name cleared.')
      onChanged()
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Could not save.')
    } finally {
      setBusy(false)
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    void save(name.trim() || null)
  }

  if (saved === undefined) return null

  return (
    <div className="rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
      <p className="text-sm font-medium">Partner contributions</p>
      <p className="text-xs text-slate-500">Her name as it appears on transfers (like "Zelle payment from …"). Her money is then kept apart from your income.</p>
      <form onSubmit={submit} className="mt-3 flex gap-2">
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={100}
          placeholder="Name on her transfers"
          aria-label="Partner's name on transfers"
          className="min-w-0 flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
        />
        <button type="submit" disabled={busy || (name.trim() || null) === saved} className="rounded-lg bg-slate-900 px-4 text-sm font-semibold text-white disabled:opacity-50">
          Save
        </button>
      </form>
      {!saved && suggestions.length > 0 && (
        <div className="mt-2">
          <p className="text-xs text-slate-500">People who sent you money this year:</p>
          <div className="mt-1 flex flex-wrap gap-2">
            {suggestions.map((s) => (
              <button
                key={s.name}
                type="button"
                disabled={busy}
                onClick={() => save(s.name)}
                className="rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700"
              >
                {s.name} · {s.count}× · {formatCents(s.totalCents)}
              </button>
            ))}
          </div>
        </div>
      )}
      {saved && (
        <button type="button" disabled={busy} onClick={() => save(null)} className="mt-2 text-xs font-medium text-slate-600 underline">
          Clear
        </button>
      )}
      {message && (
        <p role="status" className="mt-2 text-xs text-emerald-700">
          {message}
        </p>
      )}
    </div>
  )
}

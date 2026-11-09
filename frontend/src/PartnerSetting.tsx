import { useEffect, useState, type FormEvent } from 'react'
import { partnerApi } from './api'
import { formatCents } from './format'
import { smallBtn } from './ui'

/**
 * On a friend's page (DECISIONS.md 207): their name on their transfers ("Zelle payment from …"). Money in from that name
 * is theirs, kept apart from your own income (SPEC Appendix D), and their fixed contributions count toward your bills.
 */
export default function PartnerSetting({
  friendId,
  friendName,
  saved,
  onChanged,
}: {
  friendId: string
  friendName: string
  saved: string | null
  onChanged: () => void
}) {
  const [name, setName] = useState(saved ?? '')
  const [suggestions, setSuggestions] = useState<{ name: string; count: number; totalCents: number }[]>([])
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (saved) return
    partnerApi
      .suggestions()
      .then(setSuggestions)
      .catch(() => undefined)
  }, [saved])

  async function save(value: string | null) {
    setBusy(true)
    setMessage(null)
    try {
      const r = await partnerApi.save(value, friendId)
      setName(value ?? '')
      setMessage(value ? `Found ${r.partnerContributions} transfers from ${value}. They’re no longer counted as your income.` : 'Cleared. Their transfers count as your income again.')
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

  return (
    <div className="rounded-3xl neu px-5 py-4">
      <p className="text-sm font-semibold">Name on {friendName}’s transfers</p>
      <p className="text-xs text-ink-soft">As your bank shows it, like “Zelle payment from …”. Money from that name is theirs, not your income.</p>
      <form onSubmit={submit} className="mt-3 flex gap-3">
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={100}
          placeholder={`Name on ${friendName}’s transfers`}
          aria-label={`${friendName}’s name on transfers`}
          className="min-w-0 flex-1 rounded-2xl neu-inset px-4 py-3 text-sm"
        />
        <button type="submit" disabled={busy || (name.trim() || null) === saved} className="rounded-lg bg-primary shadow-neu-sm px-4 text-sm font-semibold text-primary-ink disabled:opacity-50">
          Save
        </button>
      </form>
      {!saved && suggestions.length > 0 && (
        <div className="mt-3">
          <p className="text-xs text-ink-soft">People who sent you money this year:</p>
          <div className="mt-1 flex flex-wrap gap-2">
            {suggestions.map((s) => (
              <button key={s.name} type="button" disabled={busy} onClick={() => void save(s.name)} className={`${smallBtn} text-ink-soft`}>
                {s.name} · {s.count}× · {formatCents(s.totalCents)}
              </button>
            ))}
          </div>
        </div>
      )}
      {saved && (
        <button type="button" disabled={busy} onClick={() => void save(null)} className={`${smallBtn} mt-3 text-danger`}>
          Clear
        </button>
      )}
      {message && (
        <p role="status" className="mt-2 text-xs text-good">
          {message}
        </p>
      )}
    </div>
  )
}

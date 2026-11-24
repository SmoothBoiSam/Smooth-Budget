import { useEffect, useState, type FormEvent } from 'react'
import { accountsApi, type AccountNickname } from './api'
import { Card, ErrorNotice, Loading, smallBtn, smallBtnOn } from './ui'

const typeLabel: Record<string, string> = { Checking: 'Checking', Savings: 'Savings', CreditCard: 'Credit card' }
const kinds = ['Checking', 'Savings', 'CreditCard']

/**
 * More → Account nicknames (DECISIONS.md 310): a name of your own for each account, shown everywhere in the app instead of
 * its name from the bank and its last four digits. Clear it to go back.
 */
export default function AccountNicknames() {
  const [list, setList] = useState<AccountNickname[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = () =>
    accountsApi
      .nicknames()
      .then((l) => {
        setList(l)
        setError(null)
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not load your accounts.'))
  useEffect(() => {
    void load()
  }, [])

  if (error) return <ErrorNotice message={error} onRetry={() => void load()} />
  if (!list) return <Loading shape="list" />
  if (list.length === 0) return <p className="mt-6 text-sm text-ink-soft">No accounts yet. Connect a bank or import a CSV first.</p>

  const saved = (a: AccountNickname) => setList((l) => l && l.map((x) => (x.id === a.id ? a : x)))
  return (
    <>
      <p className="mt-2 px-1 text-xs text-ink-soft">Give an account a name of your own. The app calls it that everywhere instead of its name from the bank.</p>
      {kinds
        .filter((k) => list.some((a) => a.type === k))
        .map((k) => (
          <Card key={k} title={typeLabel[k]}>
            <ul className="divide-y divide-ink/[0.07]">
              {list
                .filter((a) => a.type === k)
                .map((a) => (
                  <li key={a.id} className="px-5 py-3.5">
                    <NicknameRow account={a} onSaved={saved} />
                  </li>
                ))}
            </ul>
          </Card>
        ))}
    </>
  )
}

function NicknameRow({ account: a, onSaved }: { account: AccountNickname; onSaved: (a: AccountNickname) => void }) {
  const [value, setValue] = useState(a.nickname ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const changed = value.trim() !== (a.nickname ?? '')
  const imported = a.mask ? `${a.name} ••${a.mask}` : a.name

  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    if (!changed) return
    setBusy(true)
    setError(null)
    try {
      const next = await accountsApi.setNickname(a.id, value.trim())
      setValue(next.nickname ?? '')
      onSaved(next)
      setDone(true)
      window.setTimeout(() => setDone(false), 1800)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the nickname.')
    } finally {
      setBusy(false)
    }
  }

  const id = `nick-${a.id}`
  return (
    <form onSubmit={save}>
      <label htmlFor={id} className="block text-sm text-ink-soft">
        {imported}
      </label>
      <div className="mt-1.5 flex items-center gap-2">
        <input
          id={id}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          maxLength={40}
          placeholder="Nickname"
          autoComplete="off"
          className="block min-w-0 flex-1 rounded-xl neu-inset px-3 py-2 text-base"
        />
        {(changed || done) && (
          <button type="submit" disabled={busy || !changed} className={done && !changed ? smallBtnOn : smallBtn}>
            {busy ? 'Saving…' : done && !changed ? 'Saved' : value.trim() ? 'Save' : 'Clear'}
          </button>
        )}
      </div>
      {error && (
        <p role="alert" className="mt-1 text-xs text-danger">
          {error}
        </p>
      )}
    </form>
  )
}

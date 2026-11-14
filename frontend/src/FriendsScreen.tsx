import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, joinToken, sharedApi, type InvitePeek, type SharedOverview } from './api'
import { friendField, friendSaveBtn, friendStatus } from './friends'
import { Card, ErrorNotice, Loading, Page, PageHeader, Row, Rows, smallBtn, smallBtnOn } from './ui'

/**
 * Friends (More → Friends; DECISIONS.md 205, 207): the people you split costs with, as many as you like. Each opens its
 * own page. A friend is just a name until they link their own account with an invite. Opened from an invite while
 * signed in (#/join/<token>), it asks to link first.
 */
export default function FriendsScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [o, setO] = useState<SharedOverview | null>(null)
  const [token, setToken] = useState(joinToken)
  const [peek, setPeek] = useState<InvitePeek | null>(null)
  const [name, setName] = useState('')
  const [newFriend, setNewFriend] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )

  const show = useCallback((ov: SharedOverview) => {
    setO(ov)
    setName(ov.yourName ?? '')
  }, [])

  useEffect(() => {
    sharedApi.overview().then(show).catch(fail)
    if (token) sharedApi.peek(token).then(setPeek).catch(fail)
  }, [token, fail, show])

  /** Runs a change, then reloads the list. False when it failed (the error shows). */
  async function run(action: () => Promise<unknown>): Promise<boolean> {
    setBusy(true)
    setError(null)
    try {
      await action()
      show(await sharedApi.overview())
      return true
    } catch (e) {
      fail(e)
      return false
    } finally {
      setBusy(false)
    }
  }

  function saveName(e: FormEvent) {
    e.preventDefault()
    void run(() => sharedApi.setName(name))
  }

  async function addFriend(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const added = await sharedApi.addFriend(newFriend)
      window.location.hash = `#/friends/${added.id}`
    } catch (err) {
      fail(err)
    } finally {
      setBusy(false)
    }
  }

  function leaveJoin() {
    setToken(null)
    setPeek(null)
    window.history.replaceState(null, '', '#/friends')
  }

  const joining = !!token && !!peek && !!o

  return (
    <Page>
      <PageHeader title="Friends" sub="The people you split costs with" back={{ href: '#/more', label: 'More' }} />
      {error && <ErrorNotice message={error} />}
      {!o && !error && <Loading shape="list" />}

      {joining && (
        <section aria-label="Invite" className="mt-4 rounded-3xl neu p-4 text-sm">
          {peek.valid ? (
            <>
              <p>
                <span className="font-semibold">{peek.fromName ?? 'A friend'}</span> invited you to link your accounts. Each of you keeps your own budget; only
                what you send each other is shared.
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <button
                  type="button"
                  disabled={busy || !o.yourName}
                  onClick={() => void run(() => sharedApi.join(token)).then((ok) => ok && leaveJoin())}
                  className={smallBtnOn}
                >
                  Link accounts
                </button>
                <button type="button" onClick={leaveJoin} className={`${smallBtn} text-danger`}>
                  Not now
                </button>
              </div>
              {!o.yourName && <p className="mt-2 text-xs text-gold-ink">Add your name below first, so they know it’s you.</p>}
            </>
          ) : (
            <p className="text-danger">{peek.problem}</p>
          )}
        </section>
      )}

      {o && (
        <>
          {o.friends.length === 0 ? (
            <p className="mt-6 rounded-2xl neu-inset px-4 py-3 text-sm text-ink-soft">
              Add someone you split costs with: a partner, a roommate, a friend. They don’t need an account. If they make one, you can link it so you can send
              each other requests.
            </p>
          ) : (
            <Card>
              <Rows>
                {o.friends.map((f) => (
                  <Row key={f.id} label={<span className="font-semibold">{f.name}</span>} hint={friendStatus(f)} href={`#/friends/${f.id}`} />
                ))}
              </Rows>
            </Card>
          )}

          <form onSubmit={(e) => void addFriend(e)} className="mt-4 flex gap-3">
            <label className="min-w-0 flex-1">
              <span className="sr-only">New friend’s name</span>
              <input value={newFriend} maxLength={60} onChange={(e) => setNewFriend(e.target.value)} placeholder="Add a friend: name" className={friendField} />
            </label>
            <button type="submit" disabled={busy || !newFriend.trim()} className={friendSaveBtn}>
              Add
            </button>
          </form>

          <form onSubmit={saveName} className="mt-6 rounded-3xl neu p-4">
            <label className="block">
              <span className="text-sm font-semibold">Your name</span>
              <span className="block text-xs text-ink-soft">What linked friends see, like “Sam asks for $60”.</span>
              <div className="mt-2 flex gap-3">
                <input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} placeholder="Sam" className={friendField} />
                <button type="submit" disabled={busy || !name.trim() || name.trim() === (o.yourName ?? '')} className={friendSaveBtn}>
                  Save
                </button>
              </div>
            </label>
          </form>

          <p className="mt-4 px-1 text-xs text-ink-faint">
            Only what you send each other is shared with a linked friend: a name, a date, an amount and a note. Never your balances, accounts or spending.
          </p>
        </>
      )}
    </Page>
  )
}

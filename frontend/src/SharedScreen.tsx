import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, inviteLink, joinToken, sharedApi, type Friend, type InvitePeek, type SharedOverview } from './api'
import { formatDay } from './format'
import { ErrorNotice, Loading, Page, PageHeader, smallBtn, smallBtnOn } from './ui'

const field = 'block w-full rounded-xl neu-inset px-3 py-2.5 text-base outline-none focus:ring-2 focus:ring-accent'
const saveBtn = 'shrink-0 rounded-xl bg-primary px-4 text-sm font-semibold text-primary-ink shadow-neu-sm disabled:opacity-40'
const day = (iso: string) => formatDay(iso.slice(0, 10))

/**
 * The Shared page (DECISIONS.md 205): your friends, as many as you like. A friend is just a name until they link their
 * own account with an invite; then what you send each other shows up on both sides. Opened from an invite while signed
 * in (#/join/<token>), it asks to link first.
 */
export default function SharedScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [o, setO] = useState<SharedOverview | null>(null)
  const [token, setToken] = useState(joinToken)
  const [peek, setPeek] = useState<InvitePeek | null>(null)
  const [name, setName] = useState('')
  const [newFriend, setNewFriend] = useState('')
  const [open, setOpen] = useState<string | null>(null)
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
    const added = await run(async () => setOpen((await sharedApi.addFriend(newFriend)).id))
    if (added) setNewFriend('')
  }

  function leaveJoin() {
    setToken(null)
    setPeek(null)
    window.history.replaceState(null, '', '#/shared')
  }

  const joining = !!token && !!peek && !!o

  return (
    <Page>
      <PageHeader title="Shared" sub="The people you split costs with" back={{ href: '#/more', label: 'More' }} />
      {error && <ErrorNotice message={error} />}
      {!o && !error && <Loading />}

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
          <section aria-labelledby="friends-h" className="mt-5">
            <h2 id="friends-h" className="px-1 text-sm font-semibold text-ink-soft">
              Friends
            </h2>
            {o.friends.length === 0 ? (
              <p className="mt-2 rounded-2xl neu-inset px-4 py-3 text-sm text-ink-soft">
                Add someone you split costs with: a partner, a roommate, a friend. They don’t need an account. If they make one, you can link it so you can
                send each other requests.
              </p>
            ) : (
              <ul className="mt-2 space-y-3">
                {o.friends.map((f) => (
                  <FriendRow
                    key={f.id}
                    friend={f}
                    yourName={o.yourName}
                    open={open === f.id}
                    onToggle={() => setOpen(open === f.id ? null : f.id)}
                    busy={busy}
                    run={run}
                  />
                ))}
              </ul>
            )}

            <form onSubmit={(e) => void addFriend(e)} className="mt-4 flex gap-3">
              <label className="min-w-0 flex-1">
                <span className="sr-only">New friend’s name</span>
                <input value={newFriend} maxLength={60} onChange={(e) => setNewFriend(e.target.value)} placeholder="Add a friend: name" className={field} />
              </label>
              <button type="submit" disabled={busy || !newFriend.trim()} className={saveBtn}>
                Add
              </button>
            </form>
          </section>

          <form onSubmit={saveName} className="mt-6 rounded-3xl neu p-4">
            <label className="block">
              <span className="text-sm font-semibold">Your name</span>
              <span className="block text-xs text-ink-soft">What linked friends see, like “Sam asks for $60”.</span>
              <div className="mt-2 flex gap-3">
                <input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} placeholder="Sam" className={field} />
                <button type="submit" disabled={busy || !name.trim() || name.trim() === (o.yourName ?? '')} className={saveBtn}>
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

function FriendRow({
  friend: f,
  yourName,
  open,
  onToggle,
  busy,
  run,
}: {
  friend: Friend
  yourName: string | null
  open: boolean
  onToggle: () => void
  busy: boolean
  run: (action: () => Promise<unknown>) => Promise<boolean>
}) {
  const [link, setLink] = useState<{ url: string; expiresUtc: string } | null>(null)
  const [copied, setCopied] = useState(false)
  const [renaming, setRenaming] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<'unlink' | 'remove' | null>(null)

  const status = f.linked
    ? `Linked account${f.linkedSince ? ` since ${day(f.linkedSince)}` : ''}`
    : f.inviteExpiresUtc
      ? `Invite sent, works until ${day(f.inviteExpiresUtc)}`
      : 'No account'

  async function makeInvite() {
    await run(async () => {
      const made = await sharedApi.invite(f.id)
      setLink({ url: inviteLink(made.token), expiresUtc: made.expiresUtc })
      setCopied(false)
    })
  }

  async function share() {
    if (!link) return
    const text = `${yourName ?? 'I'} invited you to Smooth Budget, to split bills and purchases. Your budget stays your own.`
    try {
      if (navigator.share) await navigator.share({ title: 'Smooth Budget', text, url: link.url })
      else {
        await navigator.clipboard.writeText(link.url)
        setCopied(true)
      }
    } catch {
      // Closing the share sheet isn't an error.
    }
  }

  async function rename(e: FormEvent) {
    e.preventDefault()
    if (renaming !== null && (await run(() => sharedApi.renameFriend(f.id, renaming)))) setRenaming(null)
  }

  return (
    <li className="rounded-2xl neu">
      <button type="button" onClick={onToggle} aria-expanded={open} className="flex w-full items-center justify-between gap-3 px-4 py-3 text-left">
        <span className="min-w-0">
          <span className="block truncate text-sm font-semibold">{f.name}</span>
          <span className={`block text-xs ${f.linked ? 'text-good' : 'text-ink-soft'}`}>{status}</span>
        </span>
        <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden className={`size-4 shrink-0 text-ink-faint transition-transform ${open ? 'rotate-180' : ''}`}>
          <path d="m5 8 5 5 5-5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && (
        <div className="border-t border-ink/[0.06] px-4 pt-3 pb-4 text-sm">
          {!f.linked &&
            (link ? (
              <div className="rounded-2xl neu-inset px-4 py-3">
                <p>Send this link to {f.name}. It works once, until {day(link.expiresUtc)}.</p>
                <p className="mt-2 break-all text-xs text-ink-soft">{link.url}</p>
                <div className="mt-3 flex flex-wrap gap-2">
                  <button type="button" onClick={() => void share()} className={smallBtnOn}>
                    {copied ? 'Copied' : 'Share link'}
                  </button>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => void run(() => sharedApi.cancelInvite(f.id)).then((ok) => ok && setLink(null))}
                    className={`${smallBtn} text-danger`}
                  >
                    Cancel invite
                  </button>
                </div>
              </div>
            ) : (
              <div>
                <p className="text-ink-soft">
                  {f.name} doesn’t need an account. If they make one, link it so you can send each other requests; each of you keeps your own budget.
                </p>
                <button type="button" disabled={busy || !yourName} onClick={() => void makeInvite()} className={`${smallBtnOn} mt-3`}>
                  {f.inviteExpiresUtc ? 'Make a new invite link' : 'Invite to link'}
                </button>
                {!yourName && <p className="mt-2 text-xs text-gold-ink">Add your name below first, so they know who’s inviting them.</p>}
                {f.inviteExpiresUtc && <p className="mt-2 text-xs text-ink-faint">A new link replaces the one you sent.</p>}
              </div>
            ))}

          {renaming !== null ? (
            <form onSubmit={(e) => void rename(e)} className="mt-4 flex gap-3">
              <label className="min-w-0 flex-1">
                <span className="sr-only">Name</span>
                <input autoFocus value={renaming} maxLength={60} onChange={(e) => setRenaming(e.target.value)} className={field} />
              </label>
              <button type="submit" disabled={busy || !renaming.trim() || renaming.trim() === f.name} className={saveBtn}>
                Save
              </button>
            </form>
          ) : confirm ? (
            <div className="mt-4 rounded-2xl neu-inset px-4 py-3">
              <p>
                {confirm === 'unlink'
                  ? `Unlink from ${f.name}? You each keep the other as a friend without an account, and both budgets stay as they are.`
                  : f.linked
                    ? `Remove ${f.name}? This unlinks your accounts too. Both budgets stay as they are.`
                    : `Remove ${f.name} from your friends?`}
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => void run(() => (confirm === 'unlink' ? sharedApi.unlink(f.id) : sharedApi.removeFriend(f.id))).then((ok) => ok && setConfirm(null))}
                  className={`${smallBtn} text-danger`}
                >
                  {confirm === 'unlink' ? 'Unlink' : 'Remove'}
                </button>
                <button type="button" onClick={() => setConfirm(null)} className={`${smallBtn} text-ink`}>
                  Keep
                </button>
              </div>
            </div>
          ) : (
            <div className="mt-4 flex flex-wrap gap-2">
              <button type="button" onClick={() => setRenaming(f.name)} className={`${smallBtn} text-accent`}>
                Rename
              </button>
              {f.linked && (
                <button type="button" onClick={() => setConfirm('unlink')} className={`${smallBtn} text-danger`}>
                  Unlink
                </button>
              )}
              <button type="button" onClick={() => setConfirm('remove')} className={`${smallBtn} text-danger`}>
                Remove
              </button>
            </div>
          )}
        </div>
      )}
    </li>
  )
}

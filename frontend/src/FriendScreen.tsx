import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, inviteLink, sharedApi, type SharedOverview } from './api'
import FriendMoney from './FriendMoney'
import { friendField, friendSaveBtn, friendStatus } from './friends'
import { formatDay } from './format'
import PartnerSetting from './PartnerSetting'
import { ErrorNotice, Loading, Page, PageHeader, smallBtn, smallBtnOn } from './ui'

const day = (iso: string) => formatDay(iso.slice(0, 10))

/**
 * One friend's page (#/friends/<id>; DECISIONS.md 205, 207, 211): their account (invite, link, unlink), the name on their
 * transfers with their fixed contributions and recent transfers (what the Partner screen was), and rename or remove.
 */
export default function FriendScreen({ id, onSignedOut }: { id: string; onSignedOut: () => void }) {
  const [o, setO] = useState<SharedOverview | null>(null)
  const [link, setLink] = useState<{ url: string; expiresUtc: string } | null>(null)
  const [copied, setCopied] = useState(false)
  const [renaming, setRenaming] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<'unlink' | 'remove' | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )

  useEffect(() => {
    sharedApi.overview().then(setO).catch(fail)
  }, [fail])

  async function run(action: () => Promise<unknown>): Promise<boolean> {
    setBusy(true)
    setError(null)
    try {
      await action()
      setO(await sharedApi.overview())
      return true
    } catch (e) {
      fail(e)
      return false
    } finally {
      setBusy(false)
    }
  }

  // #/friends/transfers (where old Partner links land): the first friend whose transfers are tracked.
  const f = id === 'transfers' ? o?.friends.find((x) => x.transferName) : o?.friends.find((x) => x.id === id)
  const fid = f?.id ?? id

  if (o && !f)
    return (
      <Page>
        <PageHeader title="Friend" back={{ href: '#/friends', label: 'Friends' }} />
        <p className="mt-6 rounded-2xl neu-inset px-4 py-3 text-sm text-ink-soft">{id === 'transfers' ? 'No friend’s transfers are tracked yet. Open a friend and add the name on their transfers.' : 'That friend isn’t on your list anymore.'}</p>
      </Page>
    )

  async function makeInvite() {
    await run(async () => {
      const made = await sharedApi.invite(fid)
      setLink({ url: inviteLink(made.token), expiresUtc: made.expiresUtc })
      setCopied(false)
    })
  }

  async function share() {
    if (!link) return
    const text = `${o?.yourName ?? 'I'} invited you to Smooth Budget, to split bills and purchases. Your budget stays your own.`
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
    if (renaming !== null && (await run(() => sharedApi.renameFriend(fid, renaming)))) setRenaming(null)
  }

  async function remove() {
    if (await run(() => sharedApi.removeFriend(fid))) window.location.hash = '#/friends'
  }

  return (
    <Page>
      <PageHeader title={f?.name ?? 'Friend'} sub={f ? friendStatus(f) : undefined} back={{ href: '#/friends', label: 'Friends' }} />
      {error && <ErrorNotice message={error} />}
      {!o && !error && <Loading />}

      {f && o && (
        <>
          <section aria-labelledby="account-h" className="mt-6 rounded-3xl neu p-4 text-sm">
            <h2 id="account-h" className="font-semibold">
              Their account
            </h2>
            {f.linked ? (
              <>
                <p className="mt-1 text-ink-soft">
                  {f.name} has their own account, linked to yours{f.linkedSince ? ` since ${day(f.linkedSince)}` : ''}. Each of you keeps your own budget.
                </p>
                <button type="button" onClick={() => setConfirm('unlink')} className={`${smallBtn} mt-3 text-danger`}>
                  Unlink
                </button>
              </>
            ) : link ? (
              <div className="mt-2 rounded-2xl neu-inset px-4 py-3">
                <p>Send this link to {f.name}. It works once, until {day(link.expiresUtc)}.</p>
                <p className="mt-2 break-all text-xs text-ink-soft">{link.url}</p>
                <div className="mt-3 flex flex-wrap gap-2">
                  <button type="button" onClick={() => void share()} className={smallBtnOn}>
                    {copied ? 'Copied' : 'Share link'}
                  </button>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => void run(() => sharedApi.cancelInvite(fid)).then((ok) => ok && setLink(null))}
                    className={`${smallBtn} text-danger`}
                  >
                    Cancel invite
                  </button>
                </div>
              </div>
            ) : (
              <>
                <p className="mt-1 text-ink-soft">
                  {f.name} doesn’t need an account. If they make one, link it so you can send each other requests; each of you keeps your own budget.
                </p>
                <button type="button" disabled={busy || !o.yourName} onClick={() => void makeInvite()} className={`${smallBtnOn} mt-3`}>
                  {f.inviteExpiresUtc ? 'Make a new invite link' : 'Invite to link'}
                </button>
                {!o.yourName && (
                  <p className="mt-2 text-xs text-gold-ink">
                    Add your name on{' '}
                    <a href="#/friends" className="font-medium text-accent">
                      Friends
                    </a>{' '}
                    first, so they know who’s inviting them.
                  </p>
                )}
                {f.inviteExpiresUtc && <p className="mt-2 text-xs text-ink-faint">The invite you sent works until {day(f.inviteExpiresUtc)}. A new link replaces it.</p>}
              </>
            )}
          </section>

          <section aria-label="Money" className="mt-6">
            {/* Every friend can have their own transfers and fixed contributions (DECISIONS.md 211). */}
            <PartnerSetting
              key={f.transferName ?? ''}
              friendId={f.id}
              friendName={f.name}
              saved={f.transferName}
              onChanged={() => void sharedApi.overview().then(setO).catch(fail)}
            />
            {f.transferName && <FriendMoney friendId={f.id} who={f.name} reloadKey={f.transferName} onSignedOut={onSignedOut} />}
          </section>

          {renaming !== null ? (
            <form onSubmit={(e) => void rename(e)} className="mt-6 flex gap-3">
              <label className="min-w-0 flex-1">
                <span className="sr-only">Name</span>
                <input autoFocus value={renaming} maxLength={60} onChange={(e) => setRenaming(e.target.value)} className={friendField} />
              </label>
              <button type="submit" disabled={busy || !renaming.trim() || renaming.trim() === f.name} className={friendSaveBtn}>
                Save
              </button>
            </form>
          ) : confirm ? (
            <div className="mt-6 rounded-2xl neu-inset px-4 py-3 text-sm">
              <p>
                {confirm === 'unlink'
                  ? `Unlink from ${f.name}? You each keep the other as a friend without an account, and both budgets stay as they are.`
                  : `Remove ${f.name} from your friends?${f.linked ? ' This unlinks your accounts too.' : ''}${f.transferName ? ' Their transfers count as your income again, and their fixed contributions are removed.' : ''}`}
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => (confirm === 'unlink' ? void run(() => sharedApi.unlink(fid)).then((ok) => ok && setConfirm(null)) : void remove())}
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
            <div className="mt-6 flex flex-wrap gap-2">
              <button type="button" onClick={() => setRenaming(f.name)} className={`${smallBtn} text-accent`}>
                Rename
              </button>
              <button type="button" onClick={() => setConfirm('remove')} className={`${smallBtn} text-danger`}>
                Remove {f.name}
              </button>
            </div>
          )}
        </>
      )}
    </Page>
  )
}

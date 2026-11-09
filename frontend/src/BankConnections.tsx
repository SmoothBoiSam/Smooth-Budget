import { useCallback, useEffect, useState } from 'react'
import { usePlaidLink } from 'react-plaid-link'
import { plaidApi, type AccountMapping, type LinkResult, type PlaidConfig, type PlaidItem, type SyncResult } from './api'
import { formatTime } from './format'

const chip = 'neu-sm rounded-full px-3.5 py-1.5 text-ink'

const statusBadge: Record<PlaidItem['status'], { label: string; cls: string }> = {
  Active: { label: 'Connected', cls: 'neu-inset text-good' },
  PendingSetup: { label: 'Needs setup', cls: 'neu-inset text-gold-ink' },
  LoginRequired: { label: 'Sign in again', cls: 'neu-inset text-danger' },
  Error: { label: 'Error', cls: 'neu-inset text-danger' },
}

function describe(r: SyncResult): string {
  if (!r.ok) return `${r.institutionName ?? 'Bank'}: ${r.error ?? 'sync failed'}`
  const parts = [`${r.added} new`, `${r.updated} updated`]
  if (r.pendingPosted > 0) parts.push(`${r.pendingPosted} pending posted`)
  if (r.removed > 0) parts.push(`${r.removed} removed`)
  if (r.matchedToCsv > 0) parts.push(`${r.matchedToCsv} matched to your CSV imports`)
  if (r.skippedAsCsvHistory > 0) parts.push(`${r.skippedAsCsvHistory} older ones already covered by CSV`)
  return `${r.institutionName ?? 'Bank'}: ${parts.join(', ')}`
}

/** More → Bank connections (SPEC sections 4.1 and 4.9). */
export default function BankConnections({ onChanged }: { onChanged: () => void }) {
  const [config, setConfig] = useState<PlaidConfig | null>(null)
  const [items, setItems] = useState<PlaidItem[]>([])
  const [linkToken, setLinkToken] = useState<string | null>(null)
  const [updateItemId, setUpdateItemId] = useState<string | null>(null)
  // What an update-mode Link is for: signing in again, or adding accounts (DECISIONS.md 157).
  const [updateFor, setUpdateFor] = useState<'reconnect' | 'add'>('reconnect')
  const [keepBoth, setKeepBoth] = useState(false)
  // Set when the app opened from a bank's own sign-in: Plaid needs this address to finish (DECISIONS.md 185).
  const [returnedFrom, setReturnedFrom] = useState<string | null>(null)
  const [setup, setSetup] = useState<LinkResult | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [c, i] = await Promise.all([plaidApi.config(), plaidApi.items()])
      setConfig(c)
      setItems(i)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load bank connections.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  async function run<T>(label: string, action: () => Promise<T>): Promise<T | undefined> {
    setBusy(label)
    setError(null)
    setMessage(null)
    try {
      return await action()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Something went wrong.')
      return undefined
    } finally {
      setBusy(null)
    }
  }

  // Back from the bank: pick up the same Plaid session, then tidy the address (DECISIONS.md 185).
  useEffect(() => {
    const url = new URL(window.location.href)
    if (!url.pathname.startsWith('/plaid-oauth')) return
    const returned = url.origin + url.pathname + url.search
    window.history.replaceState(null, '', '/#/more/banks')
    if (!url.searchParams.has('oauth_state_id')) return
    void (async () => {
      try {
        const p = await plaidApi.oauthPending()
        setUpdateItemId(p.itemId)
        setUpdateFor(p.addAccounts ? 'add' : 'reconnect')
        setReturnedFrom(returned)
        setLinkToken(p.linkToken)
      } catch {
        setError('Plaid’s session ran out while you were at your bank. Tap Add accounts (or Connect a bank) again.')
      }
    })()
  }, [])

  function linkClosed() {
    setLinkToken(null)
    setUpdateItemId(null)
    if (returnedFrom) {
      setReturnedFrom(null)
      void plaidApi.clearOAuthPending().catch(() => undefined)
    }
  }

  const { open, ready } = usePlaidLink({
    token: linkToken,
    receivedRedirectUri: returnedFrom ?? undefined,
    onSuccess: (publicToken) => {
      const itemId = updateItemId
      linkClosed()
      void run('Finishing…', async () => {
        if (itemId && updateFor === 'add') {
          await showAccounts(itemId)
        } else if (itemId) {
          setMessage(describe(await plaidApi.reconnected(itemId)))
          onChanged()
        } else if (publicToken) {
          setKeepBoth(false)
          setSetup(await plaidApi.exchange(publicToken))
        }
        await load()
      })
    },
    onExit: () => {
      const itemId = updateItemId
      linkClosed()
      // With nothing changed in Plaid (the account was already ticked), Plaid closes without "success": still check
      // whether it shares an account the app hasn't imported yet (DECISIONS.md 184).
      if (itemId && updateFor === 'add') void run('Checking your accounts…', () => showAccounts(itemId))
    },
  })

  /**
   * The accounts Plaid shares from a connection. Any not imported yet open the import sheet; with none, say what Plaid
   * shares, so a missing account is clearly on Plaid's side.
   */
  async function showAccounts(itemId: string) {
    const r = await plaidApi.itemAccounts(itemId)
    const synced = new Set(r.existingAccounts.filter((a) => a.bankSynced).map((a) => a.id))
    const fresh = r.accounts.filter((a) => a.accountType !== null && !(a.suggestedAccountId && synced.has(a.suggestedAccountId)))
    if (fresh.length > 0) {
      setKeepBoth(false)
      setSetup(r)
      return
    }
    const names = r.accounts.map((a) => (a.mask ? `${a.name} ••${a.mask}` : a.name)).join(', ')
    setMessage(
      `${r.institutionName ?? 'Your bank'} shares ${names || 'no accounts'} with the app, and every one it can track is already imported. ` +
        'If an account is missing, tap Add accounts and make sure it’s ticked in Plaid, then tap Continue.',
    )
  }

  // Plaid Link opens once its token has loaded.
  useEffect(() => {
    if (linkToken && ready) open()
  }, [linkToken, ready, open])

  async function connect(itemId?: string, addAccounts = false) {
    const r = await run('Opening Plaid…', () => plaidApi.linkToken(itemId, addAccounts))
    if (r) {
      setUpdateItemId(itemId ?? null)
      setUpdateFor(addAccounts ? 'add' : 'reconnect')
      setLinkToken(r.linkToken)
    }
  }

  /** The bank was connected a second time: drop the new connection and add the account to the first one instead. */
  async function addToExisting(duplicateId: string, existingId: string) {
    setSetup(null)
    await run('Removing the extra connection…', () => plaidApi.removeItem(duplicateId))
    await load()
    await connect(existingId, true)
  }

  async function sandbox() {
    const r = await run('Linking test bank…', () => plaidApi.sandboxLink(true))
    if (r) setSetup(r)
    await load()
  }

  async function syncNow() {
    const results = await run('Syncing…', () => plaidApi.syncAll())
    if (results) {
      setMessage(results.map(describe).join(' · ') || 'Nothing to sync.')
      onChanged()
    }
    await load()
  }

  async function remove(item: PlaidItem) {
    if (!window.confirm(`Disconnect ${item.institutionName ?? 'this bank'}? Its accounts and history stay; they just stop syncing.`)) return
    await run('Disconnecting…', () => plaidApi.removeItem(item.id))
    await load()
    onChanged()
  }

  async function refreshSandbox(item: PlaidItem) {
    await run('Asking Plaid for new activity…', () => plaidApi.sandboxRefresh(item.id))
    setMessage('Plaid is generating new test activity. Sync in a few seconds to see pending purchases post.')
  }

  if (!config) return null

  return (
    <section aria-label="Bank connections" className="mt-6">
      {!config.enabled ? (
        <p className="neu rounded-3xl px-5 py-4 text-sm text-ink-soft">Bank sync isn’t set up here. Use Import a CSV instead.</p>
      ) : (
        <>
          {items.length > 0 && (
            <ul className="space-y-4">
              {items.map((i) => (
                <li key={i.id} className="neu rounded-3xl px-5 py-4">
                  <div className="flex items-center justify-between gap-2">
                    <p className="truncate font-semibold">{i.institutionName ?? 'Bank'}</p>
                    <span className={`shrink-0 rounded-full px-2.5 py-0.5 text-xs font-semibold ${statusBadge[i.status].cls}`}>
                      {statusBadge[i.status].label}
                    </span>
                  </div>
                  <p className="mt-0.5 text-xs text-ink-soft">
                    {i.accounts.length > 0 ? i.accounts.join(', ') : 'No accounts imported'}
                    {i.lastSyncedUtc && ` · synced ${formatTime(i.lastSyncedUtc)}`}
                  </p>
                  {i.statusMessage && <p className="mt-1 text-xs text-danger">{i.statusMessage}</p>}
                  <div className="mt-3 flex flex-wrap gap-2 text-xs font-semibold">
                    {(i.status === 'LoginRequired' || i.status === 'Error') && (
                      <button type="button" onClick={() => connect(i.id)} className="rounded-full bg-coral px-3.5 py-1.5 text-coral-ink shadow-neu-sm">
                        Reconnect
                      </button>
                    )}
                    {i.status === 'Active' && (
                      <button type="button" disabled={busy !== null} onClick={() => connect(i.id, true)} className={chip}>
                        Add accounts
                      </button>
                    )}
                    {i.status === 'Active' && (
                      <button
                        type="button"
                        disabled={busy !== null}
                        onClick={() => void run('Checking your accounts…', () => showAccounts(i.id))}
                        className={chip}
                      >
                        Import accounts
                      </button>
                    )}
                    {config.sandboxShortcut && i.status === 'Active' && (
                      <button type="button" onClick={() => refreshSandbox(i)} className={chip}>
                        Simulate activity
                      </button>
                    )}
                    {/* "Refresh categories from bank" moved to Budget → Categories (DECISIONS.md 176). */}
                    <button type="button" onClick={() => remove(i)} className={`${chip} text-danger!`}>
                      Disconnect
                    </button>
                  </div>
                </li>
              ))}
            </ul>
          )}

          <div className={`flex flex-col gap-3 ${items.length > 0 ? 'mt-6' : ''}`}>
            <button
              type="button"
              disabled={busy !== null}
              onClick={() => connect()}
              className="w-full rounded-2xl bg-coral py-3 text-sm font-semibold text-coral-ink shadow-neu-sm disabled:opacity-50"
            >
              Connect a bank
            </button>
            {items.length > 0 && (
              <button type="button" disabled={busy !== null} onClick={syncNow} className="neu w-full rounded-2xl py-3 text-sm font-semibold disabled:opacity-50">
                Sync now
              </button>
            )}
            {config.sandboxShortcut && (
              <button type="button" disabled={busy !== null} onClick={sandbox} className="text-xs font-medium text-ink-soft underline disabled:opacity-50">
                Add Plaid's sandbox test bank (dev only)
              </button>
            )}
          </div>
          <p className="mt-3 text-center text-xs text-ink-faint">
            Through Plaid{config.environment !== 'production' ? ` (${config.environment})` : ''}. The app never sees your bank password.
          </p>
        </>
      )}

      {busy && <p className="mt-3 text-sm text-ink-soft">{busy}</p>}
      {message && (
        <p role="status" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {message}
        </p>
      )}
      {error && (
        <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {error}
        </p>
      )}

      {setup && setup.duplicateOfItemId && !keepBoth && (
        <DuplicateBank
          name={setup.institutionName ?? 'This bank'}
          onAddThere={() => void addToExisting(setup.itemId, setup.duplicateOfItemId!)}
          onKeepBoth={() => setKeepBoth(true)}
        />
      )}
      {setup && (!setup.duplicateOfItemId || keepBoth) && (
        <AccountSetup
          setup={setup}
          onDone={async (result) => {
            setSetup(null)
            if (result) {
              setMessage(describe(result))
              onChanged()
            }
            await load()
          }}
        />
      )}
    </section>
  )
}

/** Connecting a bank that's already connected (to add a new savings, say): point to Add accounts instead. */
function DuplicateBank({ name, onAddThere, onKeepBoth }: { name: string; onAddThere: () => void; onKeepBoth: () => void }) {
  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40">
      <div role="dialog" aria-modal="true" aria-labelledby="dup-h" className="w-full max-w-md rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]">
        <h2 id="dup-h" className="text-lg font-semibold">
          {name} is already connected
        </h2>
        <p className="mt-1 text-sm text-ink-soft">
          To add an account there, like a new savings account, add it to your existing {name} connection. This extra connection will be removed, and
          Plaid will open so you can tick the new account.
        </p>
        <button type="button" onClick={onAddThere} className="mt-4 w-full rounded-2xl bg-coral shadow-neu-sm py-3 font-semibold text-coral-ink text-sm">
          Add accounts to my {name} connection
        </button>
        <button type="button" onClick={onKeepBoth} className="mt-2 w-full rounded-2xl neu py-3 text-sm font-semibold text-ink-soft">
          This is a different login (keep both)
        </button>
      </div>
    </div>
  )
}

/** After linking: choose, for each account at the bank, a new account, an existing one (e.g. CSV), or skip. */
function AccountSetup({ setup, onDone }: { setup: LinkResult; onDone: (r: SyncResult | null) => void }) {
  const [choices, setChoices] = useState<Record<string, string>>(() =>
    Object.fromEntries(
      setup.accounts.map((a) => [a.plaidAccountId, a.accountType === null ? 'skip' : (a.suggestedAccountId ?? 'new')]),
    ),
  )
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function save() {
    setBusy(true)
    setError(null)
    const mappings: AccountMapping[] = setup.accounts.map((a) => {
      const c = choices[a.plaidAccountId]
      return c === 'new' || c === 'skip'
        ? { plaidAccountId: a.plaidAccountId, action: c, accountId: null }
        : { plaidAccountId: a.plaidAccountId, action: 'existing', accountId: c }
    })
    try {
      onDone(await plaidApi.confirmAccounts(setup.itemId, mappings))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save.')
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="setup-h"
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="setup-h" className="text-lg font-semibold">
          {setup.institutionName ?? 'Your bank'} is connected
        </h2>
        <p className="mt-1 text-sm text-ink-soft">
          Choose what to import. If you already imported CSVs for an account, link it to that account so nothing is counted twice.
        </p>
        <ul className="mt-4 space-y-3">
          {setup.accounts.map((a) => {
            const sameType = setup.existingAccounts.filter((e) => e.type === a.accountType)
            return (
              <li key={a.plaidAccountId} className="rounded-xl neu-inset p-3">
                <p className="text-sm font-medium">
                  {a.name}
                  {a.mask && <span className="text-ink-soft"> ••{a.mask}</span>}
                </p>
                <p className="text-xs text-ink-soft">{a.subtype ?? a.type}</p>
                {a.accountType === null ? (
                  <p className="mt-1 text-xs text-ink-soft">Not tracked by the budget (loans and investments come later).</p>
                ) : (
                  <select
                    aria-label={`Import ${a.name} as`}
                    value={choices[a.plaidAccountId]}
                    onChange={(e) => setChoices({ ...choices, [a.plaidAccountId]: e.target.value })}
                    className="mt-2 block w-full rounded-2xl neu-inset px-4 py-3 text-sm"
                  >
                    <option value="new">Import as a new account</option>
                    {sameType.map((e) => (
                      <option key={e.id} value={e.id}>
                        Same as {e.name}
                        {e.mask ? ` ••${e.mask}` : ''}
                      </option>
                    ))}
                    <option value="skip">Don't import</option>
                  </select>
                )}
              </li>
            )
          })}
        </ul>
        {error && (
          <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {error}
          </p>
        )}
        <button
          type="button"
          disabled={busy}
          onClick={save}
          className="mt-5 w-full rounded-2xl bg-coral shadow-neu-sm py-3 font-semibold text-coral-ink disabled:opacity-50 text-sm"
        >
          {busy ? 'Importing transactions…' : 'Import and sync'}
        </button>
      </div>
    </div>
  )
}

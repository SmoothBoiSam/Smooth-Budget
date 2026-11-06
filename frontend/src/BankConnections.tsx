import { useCallback, useEffect, useState } from 'react'
import { usePlaidLink } from 'react-plaid-link'
import { plaidApi, type AccountMapping, type LinkResult, type PlaidConfig, type PlaidItem, type SyncResult } from './api'
import { formatTime } from './format'

const statusBadge: Record<PlaidItem['status'], { label: string; cls: string }> = {
  Active: { label: 'Connected', cls: 'bg-emerald-100 text-emerald-800' },
  PendingSetup: { label: 'Needs setup', cls: 'bg-amber-100 text-amber-800' },
  LoginRequired: { label: 'Sign in again', cls: 'bg-red-100 text-red-800' },
  Error: { label: 'Error', cls: 'bg-red-100 text-red-800' },
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

/** "Bank connections" card on the More screen (SPEC sections 4.1 and 4.9). */
export default function BankConnections({ onChanged }: { onChanged: () => void }) {
  const [config, setConfig] = useState<PlaidConfig | null>(null)
  const [items, setItems] = useState<PlaidItem[]>([])
  const [linkToken, setLinkToken] = useState<string | null>(null)
  const [updateItemId, setUpdateItemId] = useState<string | null>(null)
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

  const { open, ready } = usePlaidLink({
    token: linkToken,
    onSuccess: (publicToken) => {
      const itemId = updateItemId
      setLinkToken(null)
      setUpdateItemId(null)
      void run('Finishing…', async () => {
        if (itemId) {
          setMessage(describe(await plaidApi.reconnected(itemId)))
          onChanged()
        } else if (publicToken) {
          setSetup(await plaidApi.exchange(publicToken))
        }
        await load()
      })
    },
    onExit: () => {
      setLinkToken(null)
      setUpdateItemId(null)
    },
  })

  // Plaid Link opens once its token has loaded.
  useEffect(() => {
    if (linkToken && ready) open()
  }, [linkToken, ready, open])

  async function connect(itemId?: string) {
    const r = await run('Opening Plaid…', () => plaidApi.linkToken(itemId))
    if (r) {
      setUpdateItemId(itemId ?? null)
      setLinkToken(r.linkToken)
    }
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
    <section aria-labelledby="banks-h" className="rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <div className="flex items-baseline justify-between gap-3">
        <h2 id="banks-h" className="font-semibold">
          Bank connections
        </h2>
        {config.enabled && config.environment !== 'production' && (
          <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600">Plaid {config.environment}</span>
        )}
      </div>

      {!config.enabled ? (
        <p className="mt-1 text-sm text-slate-500">Plaid isn't set up on this server. Use CSV import below instead.</p>
      ) : (
        <>
          <p className="mt-1 text-sm text-slate-500">Connect checking, savings and credit cards to sync automatically. The app never sees your bank password.</p>

          {items.length > 0 && (
            <ul className="mt-3 divide-y divide-slate-100 rounded-xl ring-1 ring-slate-200">
              {items.map((i) => (
                <li key={i.id} className="px-3 py-3">
                  <div className="flex items-center justify-between gap-2">
                    <p className="truncate text-sm font-medium">{i.institutionName ?? 'Bank'}</p>
                    <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-semibold ${statusBadge[i.status].cls}`}>
                      {statusBadge[i.status].label}
                    </span>
                  </div>
                  <p className="mt-0.5 text-xs text-slate-500">
                    {i.accounts.length > 0 ? i.accounts.join(', ') : 'No accounts imported'}
                    {i.lastSyncedUtc && ` · synced ${formatTime(i.lastSyncedUtc)}`}
                  </p>
                  {i.statusMessage && <p className="mt-1 text-xs text-red-700">{i.statusMessage}</p>}
                  <div className="mt-2 flex flex-wrap gap-3 text-sm font-medium">
                    {(i.status === 'LoginRequired' || i.status === 'Error') && (
                      <button type="button" onClick={() => connect(i.id)} className="text-slate-900 underline">
                        Reconnect
                      </button>
                    )}
                    {config.sandboxShortcut && i.status === 'Active' && (
                      <button type="button" onClick={() => refreshSandbox(i)} className="text-slate-600">
                        Simulate new activity
                      </button>
                    )}
                    <button type="button" onClick={() => remove(i)} className="text-red-600">
                      Disconnect
                    </button>
                  </div>
                </li>
              ))}
            </ul>
          )}

          <div className="mt-3 flex flex-col gap-2">
            <button
              type="button"
              disabled={busy !== null}
              onClick={() => connect()}
              className="w-full rounded-xl bg-slate-900 py-3 font-semibold text-white disabled:opacity-50"
            >
              Connect a bank
            </button>
            {items.length > 0 && (
              <button type="button" disabled={busy !== null} onClick={syncNow} className="w-full rounded-xl bg-slate-100 py-3 font-semibold disabled:opacity-50">
                Sync now
              </button>
            )}
            {config.sandboxShortcut && (
              <button type="button" disabled={busy !== null} onClick={sandbox} className="text-sm font-medium text-slate-600 underline disabled:opacity-50">
                Add Plaid's sandbox test bank (dev only)
              </button>
            )}
          </div>
        </>
      )}

      {busy && <p className="mt-3 text-sm text-slate-500">{busy}</p>}
      {message && (
        <p role="status" className="mt-3 rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-900">
          {message}
        </p>
      )}
      {error && (
        <p role="alert" className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
          {error}
        </p>
      )}

      {setup && (
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
        className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-white p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]"
      >
        <h2 id="setup-h" className="text-lg font-semibold">
          {setup.institutionName ?? 'Your bank'} is connected
        </h2>
        <p className="mt-1 text-sm text-slate-500">
          Choose what to import. If you already imported CSVs for an account, link it to that account so nothing is counted twice.
        </p>
        <ul className="mt-4 space-y-3">
          {setup.accounts.map((a) => {
            const sameType = setup.existingAccounts.filter((e) => e.type === a.accountType)
            return (
              <li key={a.plaidAccountId} className="rounded-xl bg-slate-50 p-3">
                <p className="text-sm font-medium">
                  {a.name}
                  {a.mask && <span className="text-slate-500"> ••{a.mask}</span>}
                </p>
                <p className="text-xs text-slate-500">{a.subtype ?? a.type}</p>
                {a.accountType === null ? (
                  <p className="mt-1 text-xs text-slate-500">Not tracked by the budget (loans and investments come later).</p>
                ) : (
                  <select
                    aria-label={`Import ${a.name} as`}
                    value={choices[a.plaidAccountId]}
                    onChange={(e) => setChoices({ ...choices, [a.plaidAccountId]: e.target.value })}
                    className="mt-2 block w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
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
          <p role="alert" className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </p>
        )}
        <button
          type="button"
          disabled={busy}
          onClick={save}
          className="mt-5 w-full rounded-xl bg-slate-900 py-3 font-semibold text-white disabled:opacity-50"
        >
          {busy ? 'Importing transactions…' : 'Import and sync'}
        </button>
      </div>
    </div>
  )
}

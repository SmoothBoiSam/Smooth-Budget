import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ask } from './confirm'
import { ApiError, api, budgetApi, type Account, type ImportBatch, type ImportResult, type Me } from './api'
import AppearanceSetting from './AppearanceSetting'
import NotificationSetting from './NotificationSetting'
import BankConnections from './BankConnections'
import { formatCents, formatTime } from './format'
import { Card, Chevron, ErrorNotice, headerActionCls, Page, PageHeader, Row, Rows, smallBtn } from './ui'

const accountTypeLabel: Record<Account['type'], string> = { Checking: 'Checking', Savings: 'Savings', CreditCard: 'Credit card' }

export default function MoreScreen({ me, onChanged, onSignedOut }: { me: Me; onChanged: () => void; onSignedOut: () => void }) {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [batches, setBatches] = useState<ImportBatch[]>([])
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [a, b] = await Promise.all([budgetApi.accounts(), budgetApi.batches()])
      setAccounts(a)
      setBatches(b)
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Could not load.')
    }
  }, [onSignedOut])

  useEffect(() => {
    void load()
  }, [load])

  function changed() {
    onChanged()
    void load()
  }

  async function undo(b: ImportBatch) {
    if (!(await ask(`Remove the ${b.importedCount} transactions added by ${b.fileName}?`, { yes: 'Remove', danger: true }))) return
    await budgetApi.undoBatch(b.id).catch((e) => setError(e.message))
    changed()
  }

  async function signOut() {
    await api.logout().catch(() => undefined)
    onSignedOut()
  }

  // One menu; each part that grows over time has its own page (DECISIONS.md 156, 187).
  const [sub, setSub] = useState(subFromHash)
  useEffect(() => {
    const onHash = () => {
      setSub(subFromHash())
      window.scrollTo(0, 0)
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const back = { href: '#/more', label: 'More' }
  const errorBox = error && <ErrorNotice message={error} onRetry={() => void load()} />

  if (sub === 'banks')
    return (
      <Page>
        <PageHeader title="Bank connections" back={back} />
        <BankConnections onChanged={changed} />
      </Page>
    )

  if (sub === 'notifications')
    return (
      <Page>
        <PageHeader title="Notifications" back={back} />
        <Card>
          <NotificationSetting />
        </Card>
      </Page>
    )

  if (sub === 'import')
    return (
      <Page>
        <PageHeader title="Import a CSV" back={back} />
        {errorBox}
        <ImportCard accounts={accounts} onImported={changed} onAccountAdded={load} />
        {/* Past files and their undo sit right under the import they came from (DECISIONS.md 184, 187), in a dropdown by month (293). */}
        <ImportedFiles batches={batches} onUndo={undo} />
      </Page>
    )

  return (
    <Page>
      <PageHeader title="More" />
      {errorBox}

      <Card title="Accounts">
        <Rows>
          <Row label="Bank connections" href="#/more/banks" />
          <Row label="Import a CSV" href="#/more/import" />
          <Row label="Friends" href="#/friends" />
        </Rows>
      </Card>

      <Card title="Settings">
        <Rows>
          <li>
            <AppearanceSetting />
          </li>
          <Row label="Notifications" href="#/more/notifications" />
          <li className="flex items-center gap-3 px-5 py-3.5 text-sm">
            <span className="min-w-0 flex-1 truncate text-ink-soft">{me.email}</span>
            <button type="button" onClick={signOut} className={headerActionCls + ' text-danger!'}>
              Sign out
            </button>
          </li>
        </Rows>
      </Card>
    </Page>
  )
}

const files = (n: number) => `${n} ${n === 1 ? 'file' : 'files'}`

/**
 * Imported files (DECISIONS.md 293): one dropdown, closed until you open it, with a dropdown for each month a file was
 * imported in, newest first. The newest month opens with it.
 */
function ImportedFiles({ batches, onUndo }: { batches: ImportBatch[]; onUndo: (b: ImportBatch) => void }) {
  if (batches.length === 0)
    return (
      <Card title="Imported files">
        <p className="px-5 py-4 text-sm text-ink-soft">Nothing imported yet.</p>
      </Card>
    )
  const months: { key: string; label: string; items: ImportBatch[] }[] = []
  for (const b of [...batches].sort((x, y) => y.importedUtc.localeCompare(x.importedUtc))) {
    const d = new Date(b.importedUtc)
    const key = `${d.getFullYear()}-${d.getMonth()}`
    const last = months.at(-1)
    if (last?.key === key) last.items.push(b)
    else months.push({ key, label: d.toLocaleDateString('en-US', { month: 'long', year: 'numeric' }), items: [b] })
  }
  return (
    <Card>
      <details className="group/all">
        <summary className="flex cursor-pointer list-none items-center gap-3 px-5 py-3.5 text-sm">
          <span className="min-w-0 flex-1 font-semibold">Imported files</span>
          <span className="shrink-0 text-xs text-ink-soft">{files(batches.length)}</span>
          <span className="transition-transform group-open/all:rotate-90">
            <Chevron />
          </span>
        </summary>
        <ul className="divide-y divide-ink/[0.07] border-t border-ink/[0.07]">
          {months.map((m, i) => (
            <li key={m.key}>
              <details open={i === 0} className="group/month">
                <summary className="flex cursor-pointer list-none items-center gap-3 px-5 py-3 text-sm">
                  <span className="min-w-0 flex-1 font-medium">{m.label}</span>
                  <span className="shrink-0 text-xs text-ink-soft">{files(m.items.length)}</span>
                  <span className="transition-transform group-open/month:rotate-90">
                    <Chevron />
                  </span>
                </summary>
                <ul className="divide-y divide-ink/[0.07] border-t border-ink/[0.07]">
                  {m.items.map((b) => (
                    <li key={b.id} className="flex items-center gap-3 py-3.5 pr-5 pl-8">
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-sm font-medium">{b.fileName}</p>
                        <p className="text-xs text-ink-soft">
                          {b.accountName} · {formatTime(b.importedUtc)} · {b.importedCount} new
                          {b.errorCount > 0 && `, ${b.errorCount} unreadable`}
                        </p>
                      </div>
                      {b.importedCount > 0 && (
                        <button type="button" onClick={() => onUndo(b)} className={headerActionCls + ' text-danger!'}>
                          Undo
                        </button>
                      )}
                    </li>
                  ))}
                </ul>
              </details>
            </li>
          ))}
        </ul>
      </details>
    </Card>
  )
}

const subFromHash = () => {
  const h = window.location.hash
  // Merchant rules are gone (DECISIONS.md 259): the most likely category learns from what you confirm.
  return h.startsWith('#/more/import') // #/more/imports, the old list page, lands here too
    ? 'import'
    : h.startsWith('#/more/banks')
      ? 'banks'
      : h.startsWith('#/more/notifications')
        ? 'notifications'
        : null
}

function ImportCard({ accounts, onImported, onAccountAdded }: { accounts: Account[]; onImported: () => void; onAccountAdded: () => void }) {
  const [accountId, setAccountId] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<ImportResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)

  const selectedId = accountId || accounts[0]?.id || ''

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    if (!file || !selectedId) return
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      setResult(await budgetApi.importCsv(selectedId, file))
      onImported()
      e.currentTarget?.reset()
      setFile(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Import failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section aria-label="Import a CSV" className="neu mt-6 rounded-3xl p-5">
      <p className="text-xs text-ink-soft">Chase checking, savings or credit card, or Apple Card. Overlapping files are safe.</p>

      {adding ? (
        <AddAccountForm
          onDone={(a) => {
            setAdding(false)
            if (a) {
              setAccountId(a.id)
              onAccountAdded()
            }
          }}
        />
      ) : (
        <form onSubmit={submit} className="mt-4 space-y-4">
          <label className="block">
            <span className="text-sm font-medium text-ink-soft">Account</span>
            <div className="mt-1 flex gap-2">
              <select
                value={selectedId}
                onChange={(e) => setAccountId(e.target.value)}
                className="min-w-0 flex-1 rounded-xl neu-inset px-3 py-3 text-base"
              >
                {accounts.length === 0 && <option value="">Add an account first</option>}
                {accounts.map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.name}
                    {a.mask ? ` ••${a.mask}` : ''} ({accountTypeLabel[a.type]}{a.bankSynced ? ', bank-synced' : ''})
                  </option>
                ))}
              </select>
              <button type="button" onClick={() => setAdding(true)} className="neu-sm rounded-xl px-3.5 text-sm font-semibold text-accent">
                New
              </button>
            </div>
          </label>
          <label className="block">
            <span className="text-sm font-medium text-ink-soft">File</span>
            <input
              type="file"
              accept=".csv,text/csv"
              required
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              className="mt-1 block w-full text-sm file:mr-3 file:rounded-lg file:border-0 file:bg-primary file:text-primary-ink file:px-3 file:py-2 file:font-medium"
            />
          </label>
          <button
            type="submit"
            disabled={busy || !file || !selectedId}
            className="w-full rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-50 text-sm"
          >
            {busy ? 'Importing…' : 'Import'}
          </button>
        </form>
      )}

      {error && (
        <p role="alert" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {error}
        </p>
      )}
      {result && (
        <div role="status" className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          <p className="font-semibold">
            {result.format}: {result.imported} new, {result.duplicates} already imported
          </p>
          {result.balanceSetCents !== null && <p>Balance set to {formatCents(result.balanceSetCents)}, from the file’s newest line.</p>}
          {(result.alreadySynced > 0 || result.coveredBySync > 0) && (
            <p>
              Skipped so nothing counts twice: {result.alreadySynced} already synced from your bank
              {result.coveredBySync > 0 && `, ${result.coveredBySync} in a period bank sync already covers`}.
            </p>
          )}
          <p>
            {result.transfers} transfers or card payments, {result.categorized} categorized, {result.needsCategory} need a category
            {result.needsCategory > 0 && (
              <>
                {' '}
                ·{' '}
                <a href="#/budget" className={`${smallBtn} py-1! text-accent`}>
                  Review
                </a>
              </>
            )}
          </p>
          {result.errors.length > 0 && (
            <ul className="mt-1 text-danger">
              {result.errors.slice(0, 5).map((e) => (
                <li key={e.rowNumber}>
                  Row {e.rowNumber}: {e.message}
                </li>
              ))}
              {result.errors.length > 5 && <li>…and {result.errors.length - 5} more</li>}
            </ul>
          )}
        </div>
      )}
    </section>
  )
}

function AddAccountForm({ onDone }: { onDone: (a: Account | null) => void }) {
  const [name, setName] = useState('')
  const [type, setType] = useState<Account['type']>('Checking')
  const [mask, setMask] = useState('')
  const [error, setError] = useState<string | null>(null)

  async function submit(e: FormEvent) {
    e.preventDefault()
    try {
      onDone(await budgetApi.createAccount(name, type, mask || null))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not add account.')
    }
  }

  return (
    <form onSubmit={submit} className="mt-4 space-y-3 rounded-xl neu-inset p-3">
      <input
        value={name}
        onChange={(e) => setName(e.target.value)}
        required
        maxLength={100}
        placeholder="Name, e.g. Chase Checking"
        className="block w-full rounded-xl neu-inset px-3 py-2"
      />
      <div className="flex gap-2">
        <select
          value={type}
          onChange={(e) => setType(e.target.value as Account['type'])}
          className="flex-1 rounded-xl neu-inset px-3 py-2"
        >
          <option value="Checking">Checking</option>
          <option value="Savings">Savings</option>
          <option value="CreditCard">Credit card</option>
        </select>
        <input
          value={mask}
          onChange={(e) => setMask(e.target.value.replace(/\D/g, '').slice(0, 4))}
          inputMode="numeric"
          placeholder="Last 4"
          aria-label="Last 4 digits (optional)"
          className="w-24 rounded-xl neu-inset px-3 py-2"
        />
      </div>
      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex gap-3">
        <button type="button" onClick={() => onDone(null)} className="neu flex-1 rounded-2xl py-3 text-sm font-semibold text-danger">
          Cancel
        </button>
        <button type="submit" className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 text-sm font-semibold text-primary-ink">
          Add account
        </button>
      </div>
    </form>
  )
}

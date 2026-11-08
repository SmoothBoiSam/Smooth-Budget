import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, api, budgetApi, type Account, type ImportBatch, type ImportResult, type Me, type Rule } from './api'
import AppearanceSetting from './AppearanceSetting'
import NotificationSetting from './NotificationSetting'
import BankConnections from './BankConnections'
import PartnerSetting from './PartnerSetting'
import SweepSetting from './SweepSetting'
import { formatTime } from './format'

const accountTypeLabel: Record<Account['type'], string> = { Checking: 'Checking', Savings: 'Savings', CreditCard: 'Credit card' }

export default function MoreScreen({ me, onChanged, onSignedOut }: { me: Me; onChanged: () => void; onSignedOut: () => void }) {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [batches, setBatches] = useState<ImportBatch[]>([])
  const [rules, setRules] = useState<Rule[]>([])
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [a, b, r] = await Promise.all([budgetApi.accounts(), budgetApi.batches(), budgetApi.rules()])
      setAccounts(a)
      setBatches(b)
      setRules(r)
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
    if (!window.confirm(`Remove the ${b.importedCount} transactions added by ${b.fileName}?`)) return
    await budgetApi.undoBatch(b.id).catch((e) => setError(e.message))
    changed()
  }

  async function removeRule(r: Rule) {
    await budgetApi.deleteRule(r.id).catch((e) => setError(e.message))
    void load()
  }

  async function signOut() {
    await api.logout().catch(() => undefined)
    onSignedOut()
  }

  return (
    <div className="mx-auto min-h-screen max-w-md space-y-8 bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">
      <h1 className="text-2xl font-bold tracking-tight">More</h1>
      {error && (
        <p role="alert" className="rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">
          {error}
        </p>
      )}

      <a href="#/partner" className="flex items-center justify-between rounded-2xl bg-white px-4 py-4 ring-1 ring-slate-200">
        <span>
          <span className="block font-semibold">Partner</span>
          <span className="block text-sm text-slate-500">Fixed contributions, split categories, and who owes whom</span>
        </span>
        <span aria-hidden className="font-semibold text-slate-400">›</span>
      </a>

      <a href="#/bills" className="flex items-center justify-between rounded-2xl bg-white px-4 py-4 ring-1 ring-slate-200">
        <span>
          <span className="block font-semibold">Bills</span>
          <span className="block text-sm text-slate-500">Found from your transactions: prices, due dates, and what stopped</span>
        </span>
        <span aria-hidden className="font-semibold text-slate-400">›</span>
      </a>

      <BankConnections onChanged={changed} />

      <ImportCard accounts={accounts} onImported={changed} onAccountAdded={load} />

      <section aria-labelledby="imports-h">
        <h2 id="imports-h" className="text-sm font-semibold text-slate-700">
          Recent imports
        </h2>
        {batches.length === 0 ? (
          <p className="mt-2 text-sm text-slate-500">Nothing imported yet.</p>
        ) : (
          <ul className="mt-2 divide-y divide-slate-100 rounded-xl bg-white ring-1 ring-slate-200">
            {batches.map((b) => (
              <li key={b.id} className="flex items-center gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium">{b.fileName}</p>
                  <p className="text-xs text-slate-500">
                    {b.accountName} · {formatTime(b.importedUtc)} · {b.importedCount} new, {b.duplicateCount} already there
                    {b.errorCount > 0 && `, ${b.errorCount} unreadable`}
                  </p>
                </div>
                {b.importedCount > 0 && (
                  <button type="button" onClick={() => undo(b)} className="text-sm font-medium text-red-600">
                    Undo
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby="rules-h">
        <h2 id="rules-h" className="text-sm font-semibold text-slate-700">
          Merchant rules
        </h2>
        {rules.length === 0 ? (
          <p className="mt-2 text-sm text-slate-500">
            No rules yet. When you recategorize a transaction, tick "Always use this category" to create one.
          </p>
        ) : (
          <ul className="mt-2 divide-y divide-slate-100 rounded-xl bg-white ring-1 ring-slate-200">
            {rules.map((r) => (
              <li key={r.id} className="flex items-center gap-3 px-4 py-3">
                <p className="min-w-0 flex-1 text-sm">
                  <span className="font-medium">{r.pattern}</span> <span className="text-slate-400">→</span> {r.categoryName}
                </p>
                <button type="button" onClick={() => removeRule(r)} className="text-sm font-medium text-slate-500">
                  Delete
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby="settings-h" className="space-y-3">
        <h2 id="settings-h" className="text-sm font-semibold text-slate-700">
          Settings
        </h2>
        <AppearanceSetting />
        <NotificationSetting />
        <SweepSetting />
        <PartnerSetting onChanged={changed} />
        <div className="flex items-center justify-between rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
          <span className="truncate text-sm text-slate-500">{me.email}</span>
          <button type="button" onClick={signOut} className="text-sm font-medium text-slate-700">
            Sign out
          </button>
        </div>
      </section>
    </div>
  )
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
    <section aria-labelledby="import-h" className="rounded-2xl bg-white p-4 ring-1 ring-slate-200">
      <h2 id="import-h" className="font-semibold">
        Import a CSV
      </h2>
      <p className="mt-1 text-sm text-slate-500">
        Chase checking, Chase credit card or Apple Card exports. Re-importing an overlapping file is safe.
      </p>

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
        <form onSubmit={submit} className="mt-4 space-y-3">
          <label className="block">
            <span className="text-sm font-medium text-slate-700">Account</span>
            <div className="mt-1 flex gap-2">
              <select
                value={selectedId}
                onChange={(e) => setAccountId(e.target.value)}
                className="min-w-0 flex-1 rounded-xl border border-slate-300 bg-white px-3 py-3 text-base"
              >
                {accounts.length === 0 && <option value="">Add an account first</option>}
                {accounts.map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.name}
                    {a.mask ? ` ••${a.mask}` : ''} ({accountTypeLabel[a.type]}{a.bankSynced ? ', bank-synced' : ''})
                  </option>
                ))}
              </select>
              <button type="button" onClick={() => setAdding(true)} className="rounded-xl bg-slate-100 px-3 text-sm font-medium">
                Add
              </button>
            </div>
          </label>
          <label className="block">
            <span className="text-sm font-medium text-slate-700">File</span>
            <input
              type="file"
              accept=".csv,text/csv"
              required
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              className="mt-1 block w-full text-sm file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:font-medium"
            />
          </label>
          <button
            type="submit"
            disabled={busy || !file || !selectedId}
            className="w-full rounded-xl bg-slate-900 py-3 font-semibold text-white disabled:opacity-50"
          >
            {busy ? 'Importing…' : 'Import'}
          </button>
        </form>
      )}

      {error && (
        <p role="alert" className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
          {error}
        </p>
      )}
      {result && (
        <div role="status" className="mt-3 rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-900">
          <p className="font-semibold">
            {result.format}: {result.imported} new, {result.duplicates} already imported
          </p>
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
                <a href="#/budget" className="font-semibold underline">
                  Review
                </a>
              </>
            )}
          </p>
          {result.errors.length > 0 && (
            <ul className="mt-1 text-red-700">
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
    <form onSubmit={submit} className="mt-4 space-y-3 rounded-xl bg-slate-50 p-3">
      <input
        value={name}
        onChange={(e) => setName(e.target.value)}
        required
        maxLength={100}
        placeholder="Name, e.g. Chase Checking"
        className="block w-full rounded-lg border border-slate-300 bg-white px-3 py-2"
      />
      <div className="flex gap-2">
        <select
          value={type}
          onChange={(e) => setType(e.target.value as Account['type'])}
          className="flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2"
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
          className="w-24 rounded-lg border border-slate-300 bg-white px-3 py-2"
        />
      </div>
      {error && <p className="text-sm text-red-700">{error}</p>}
      <div className="flex gap-2">
        <button type="button" onClick={() => onDone(null)} className="flex-1 rounded-lg bg-white py-2 text-sm font-medium ring-1 ring-slate-300">
          Cancel
        </button>
        <button type="submit" className="flex-1 rounded-lg bg-slate-900 py-2 text-sm font-semibold text-white">
          Add account
        </button>
      </div>
    </form>
  )
}

import { useCallback, useEffect, useState } from 'react'
import { ApiError, budgetApi, type Category, type Transaction } from './api'
import { formatDay, formatSigned, titleCase } from './format'
import TransactionSheet from './TransactionSheet'

export default function BudgetScreen({ onChanged, onSignedOut }: { onChanged: () => void; onSignedOut: () => void }) {
  const [inbox, setInbox] = useState<Transaction[] | null>(null)
  const [month, setMonth] = useState<{ from: string; to: string; items: Transaction[] } | null>(null)
  const [categories, setCategories] = useState<Category[]>([])
  const [selected, setSelected] = useState<Transaction | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [i, m, c] = await Promise.all([budgetApi.inbox(), budgetApi.monthTransactions(), budgetApi.categories()])
      setInbox(i)
      setMonth(m)
      setCategories(c)
      setError(null)
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Could not load transactions.')
    }
  }, [onSignedOut])

  useEffect(() => {
    void load()
  }, [load])

  function saved(message: string) {
    setSelected(null)
    setNotice(message)
    onChanged()
    void load()
  }

  return (
    <div className="mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">
      <h1 className="text-2xl font-bold tracking-tight">Budget</h1>
      {month && (
        <p className="text-sm text-slate-500">
          {formatDay(month.from)} – {formatDay(month.to)}
        </p>
      )}

      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && (
        <p role="alert" className="mt-4 rounded-xl bg-red-50 px-4 py-3 text-sm text-red-800">
          {error}
        </p>
      )}
      {!inbox && !error && <p className="mt-10 text-center text-slate-400">Loading…</p>}

      {inbox && (
        <section aria-labelledby="inbox-h" className="mt-6">
          <h2 id="inbox-h" className="flex items-center gap-2 text-sm font-semibold text-slate-700">
            Needs a category
            <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs font-bold text-amber-800">{inbox.length}</span>
          </h2>
          {inbox.length === 0 ? (
            <p className="mt-2 rounded-xl bg-white px-4 py-3 text-sm text-slate-500 ring-1 ring-slate-200">
              All caught up. Every purchase has a category.
            </p>
          ) : (
            <TransactionList items={inbox} onSelect={setSelected} />
          )}
        </section>
      )}

      {month && (
        <section aria-labelledby="month-h" className="mt-8">
          <h2 id="month-h" className="text-sm font-semibold text-slate-700">
            This month
          </h2>
          {month.items.length === 0 ? (
            <p className="mt-2 rounded-xl bg-white px-4 py-3 text-sm text-slate-500 ring-1 ring-slate-200">
              No transactions yet. Import a CSV from More.
            </p>
          ) : (
            <TransactionList items={month.items} onSelect={setSelected} />
          )}
        </section>
      )}

      {selected && (
        <TransactionSheet transaction={selected} categories={categories} onClose={() => setSelected(null)} onSaved={saved} />
      )}
    </div>
  )
}

function TransactionList({ items, onSelect }: { items: Transaction[]; onSelect: (t: Transaction) => void }) {
  return (
    <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white ring-1 ring-slate-200">
      {items.map((t) => (
        <li key={t.id}>
          <button type="button" onClick={() => onSelect(t)} className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-slate-50">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{titleCase(t.merchant)}</p>
              <p className="truncate text-xs text-slate-500">
                {formatDay(t.date)} · {t.accountName}
                {t.pending && ' · Pending'}
              </p>
            </div>
            <div className="shrink-0 text-right">
              <p className={`text-sm font-semibold tabular-nums ${t.amountCents < 0 ? 'text-emerald-600' : ''}`}>
                {formatSigned(t.amountCents)}
              </p>
              <Tag t={t} />
            </div>
          </button>
        </li>
      ))}
    </ul>
  )
}

function Tag({ t }: { t: Transaction }) {
  if (t.isTransfer) return <span className="text-xs text-slate-400">{t.categoryName ?? 'Transfer'}</span>
  if (t.categoryName) return <span className="text-xs text-slate-500">{t.categoryName}</span>
  if (t.kind === 'Income') return <span className="text-xs text-emerald-600">Income</span>
  return <span className="text-xs font-medium text-amber-600">Needs category</span>
}

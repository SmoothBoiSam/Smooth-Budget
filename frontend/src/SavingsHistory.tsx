import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, savingsApi, type SavingsHistoryItem } from './api'
import { ErrorNotice, Loading, Page, PageHeader } from './ui'
import { formatCents, formatDay, titleCase } from './format'

const longMonth = (ym: string) => new Date(ym + '-01T12:00:00').toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
const signed = (cents: number) => `${cents < 0 ? '−' : '+'}${formatCents(Math.abs(cents))}`

/**
 * Savings → Past savings transactions (DECISIONS.md 239): every move into or out of savings, newest first, by month with
 * each month's net. Into savings is green; out of it is plain, with a minus.
 */
export default function SavingsHistoryScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [items, setItems] = useState<SavingsHistoryItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      savingsApi
        .transactions()
        .then((x) => {
          setItems(x)
          setError(null)
        })
        .catch((e) => {
          if (e instanceof ApiError && e.status === 401) return onSignedOut()
          setError(e instanceof Error ? e.message : 'Something went wrong.')
        }),
    [onSignedOut],
  )
  useEffect(() => {
    void load()
  }, [load])

  const byMonth = useMemo(() => {
    const groups: { month: string; net: number; items: SavingsHistoryItem[] }[] = []
    for (const t of items ?? []) {
      const month = t.date.slice(0, 7)
      const g = groups.at(-1)
      if (g && g.month === month) {
        g.items.push(t)
        g.net += t.cents
      } else groups.push({ month, net: t.cents, items: [t] })
    }
    return groups
  }, [items])

  return (
    <Page>
      <PageHeader title="Savings transactions" back={{ href: '#/savings', label: 'Savings' }} />
      {!items ? (
        error ? (
          <ErrorNotice message={error} onRetry={() => void load()} />
        ) : (
          <Loading shape="list" />
        )
      ) : byMonth.length === 0 ? (
        <p className="mt-4 rounded-3xl neu px-5 py-4 text-sm text-ink-soft">No savings transactions yet.</p>
      ) : (
        byMonth.map((g) => (
          <section key={g.month} className="mt-5">
            <h2 className="flex items-baseline justify-between px-1 text-sm font-semibold text-ink-soft">
              <span>{longMonth(g.month)}</span>
              <span className={`tabular-nums ${g.net > 0 ? 'text-good' : ''}`}>{signed(g.net)}</span>
            </h2>
            <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-3xl neu">
              {g.items.map((t) => (
                <li key={t.id} className="flex items-center gap-3 px-5 py-3">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{/^Moved (to|out of) savings$/.test(t.description) ? t.description : titleCase(t.description)}</p>
                    <p className="truncate text-xs text-ink-faint">
                      {formatDay(t.date)} · {t.account}
                      {t.goal ? ` · ${t.goal}` : ''}
                    </p>
                  </div>
                  <span className={`shrink-0 text-sm font-semibold tabular-nums ${t.cents > 0 ? 'text-good' : ''}`}>{signed(t.cents)}</span>
                </li>
              ))}
            </ul>
          </section>
        ))
      )}
    </Page>
  )
}

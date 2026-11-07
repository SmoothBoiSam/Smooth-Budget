import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, budgetApi, partnerPlanApi, type Category, type ContributionStatus, type PartnerOverview } from './api'
import { ErrorNotice, Loading } from './ui'
import { centsToInput, formatCents, formatDay, parseDollars, titleCase } from './format'

const purposeLabel: Record<PartnerOverview['recent'][number]['purpose'], string> = {
  waiting: 'Waiting for a label',
  contribution: 'Contribution',
  payback: 'Paid you back',
  yourShare: 'Your share',
}

/**
 * The Partner screen (SPEC 4.8): her fixed contributions with this month's expected vs received and the late flag, and
 * recent transfers between you. There's no running tally (DECISIONS.md 100): each transfer is labeled in Review with
 * what it was for, and lands in that category.
 */
export default function PartnerScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const [o, setO] = useState<PartnerOverview | null>(null)
  const [categories, setCategories] = useState<Category[]>([])
  const [editing, setEditing] = useState<PartnerOverview['contributions'][number] | null | undefined>(undefined) // null = adding
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) return onSignedOut()
      setError(e instanceof Error ? e.message : 'Something went wrong.')
    },
    [onSignedOut],
  )

  const load = useCallback(async () => {
    try {
      const [ov, cats] = await Promise.all([partnerPlanApi.overview(), budgetApi.categories()])
      setO(ov)
      setCategories(cats)
      setError(null)
    } catch (e) {
      fail(e)
    }
  }, [fail])

  useEffect(() => {
    void load()
  }, [load])

  async function act(action: () => Promise<unknown>, message: string) {
    try {
      await action()
      setNotice(message)
      setEditing(undefined)
      await load()
    } catch (e) {
      fail(e)
    }
  }

  const first = o?.partnerName ? titleCase(o.partnerName.split(' ')[0]) : null
  const who = first ?? 'Your partner'

  return (
    <div className="mx-auto min-h-screen max-w-md bg-slate-50 px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-28">
      <a href="#/more" className="text-sm font-medium text-slate-500">
        ‹ More
      </a>
      <h1 className="mt-1 text-2xl font-bold tracking-tight">Partner</h1>

      {notice && (
        <p role="status" className="mt-4 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!o && !error && <Loading />}

      {o && !o.partnerName && (
        <p className="mt-4 rounded-xl bg-amber-50 px-4 py-3 text-sm text-amber-900 ring-1 ring-amber-200">
          Set your partner’s name as it shows on their transfers (More → Settings) so the app can recognize them.
        </p>
      )}

      {o && (
        <>
          <section aria-labelledby="fixed-h" className="mt-5">
            <h2 id="fixed-h" className="text-sm font-semibold text-slate-700">
              Fixed contributions
            </h2>
            <p className="text-xs text-slate-500">
              Money {who} sends every month toward a bill. It counts from the start of the month and is subtracted from that bill’s line; if it’s late, Left to Spend
              counts without it.
            </p>
            {o.contributions.length === 0 ? (
              <button type="button" onClick={() => setEditing(null)} className="mt-2 w-full rounded-xl bg-white px-4 py-3 text-left text-sm ring-1 ring-slate-200">
                <span className="font-medium">+ Add one</span>
                <span className="block text-xs text-slate-500">For example, $800 a month toward Rent.</span>
              </button>
            ) : (
              <ul className="mt-2 space-y-2">
                {o.contributions.map((c) => (
                  <li key={c.id}>
                    <button type="button" onClick={() => setEditing(c)} className="block w-full rounded-xl bg-white px-4 py-3 text-left ring-1 ring-slate-200">
                      <span className="flex items-baseline justify-between gap-3">
                        <span className="text-sm font-medium">
                          {formatCents(c.amountCents)} toward {c.categoryName}
                        </span>
                        <span className="text-xs text-slate-500">
                          usually the {ordinal(c.windowFromDay)}–{ordinal(c.windowToDay)}
                        </span>
                      </span>
                      {c.thisMonth && <MonthStatus s={c.thisMonth} />}
                    </button>
                  </li>
                ))}
              </ul>
            )}
            {o.contributions.length > 0 && (
              <button type="button" onClick={() => setEditing(null)} className="mt-2 text-xs font-medium text-slate-600 underline">
                Add another
              </button>
            )}
          </section>

          {o.recent.length > 0 && (
            <section aria-labelledby="recent-h" className="mt-6">
              <h2 id="recent-h" className="text-sm font-semibold text-slate-700">
                Recent
              </h2>
              <ul className="mt-2 divide-y divide-slate-100 overflow-hidden rounded-xl bg-white text-sm ring-1 ring-slate-200">
                {o.recent.map((r) => (
                  <li key={r.id} className="flex items-baseline justify-between gap-3 px-4 py-2.5">
                    <span className="min-w-0 truncate">
                      {formatDay(r.date)} · {purposeLabel[r.purpose]}
                      {r.categoryName && <span className="text-slate-500"> · {r.categoryName}</span>}
                      {r.purpose === 'waiting' && (
                        <a href="#/review" className="ml-1 text-xs text-amber-600 underline">
                          label it
                        </a>
                      )}
                    </span>
                    <span className={`shrink-0 tabular-nums ${r.amountCents < 0 ? 'text-emerald-600' : ''}`}>
                      {r.amountCents < 0 ? '+' : ''}
                      {formatCents(Math.abs(r.amountCents))}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}

      {editing !== undefined && (
        <ContributionSheet
          existing={editing}
          categories={categories}
          who={who}
          onClose={() => setEditing(undefined)}
          onSave={(categoryId, cents, from, to) =>
            act(
              () =>
                editing ? partnerPlanApi.updateContribution(editing.id, categoryId, cents, from, to) : partnerPlanApi.addContribution(categoryId, cents, from, to),
              'Saved. Your budget now counts it as an offset to that bill.',
            )
          }
          onRemove={editing ? () => act(() => partnerPlanApi.removeContribution(editing.id), 'Removed.') : undefined}
        />
      )}
    </div>
  )
}

function ordinal(n: number) {
  const s = n % 100 >= 11 && n % 100 <= 13 ? 'th' : ({ 1: 'st', 2: 'nd', 3: 'rd' } as Record<number, string>)[n % 10] ?? 'th'
  return `${n}${s}`
}

function MonthStatus({ s }: { s: ContributionStatus }) {
  const [text, cls] =
    s.state === 'received'
      ? [`This month: received ${formatCents(s.receivedCents)}.`, 'text-emerald-700']
      : s.state === 'expected'
        ? [`This month: expected by ${formatDay(s.windowEnd)}. It already counts.`, 'text-slate-500']
        : s.state === 'late'
          ? [`This month: late. Left to Spend counts without ${formatCents(s.shortfallCents)}.`, 'font-medium text-red-600']
          : [
              `This month: ${formatCents(s.receivedCents)} came, not ${formatCents(s.expectedCents)}${s.shortfallCents > 0 ? `. Left to Spend counts without ${formatCents(s.shortfallCents)}.` : '.'}`,
              'font-medium text-amber-600',
            ]
  return <span className={`mt-1 block text-xs ${cls}`}>{text}</span>
}

const sheetCls = 'max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-white p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]'
const inputCls = 'mt-1 block w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-base'

function ContributionSheet({
  existing,
  categories,
  who,
  onClose,
  onSave,
  onRemove,
}: {
  existing: PartnerOverview['contributions'][number] | null
  categories: Category[]
  who: string
  onClose: () => void
  onSave: (categoryId: string, cents: number, from: number, to: number) => void
  onRemove?: () => void
}) {
  const bills = [...categories].sort((a, b) => Number(b.type === 'FixedBill') - Number(a.type === 'FixedBill'))
  const [categoryId, setCategoryId] = useState(existing?.categoryId ?? bills.find((c) => c.name === 'Rent')?.id ?? bills[0]?.id ?? '')
  const [amount, setAmount] = useState(existing ? centsToInput(existing.amountCents) : '')
  const [from, setFrom] = useState(String(existing?.windowFromDay ?? 18))
  const [to, setTo] = useState(String(existing?.windowToDay ?? 24))
  const [err, setErr] = useState<string | null>(null)

  function submit(e: FormEvent) {
    e.preventDefault()
    const cents = parseDollars(amount)
    const f = Number(from)
    const t = Number(to)
    if (!categoryId) return setErr('Pick the bill it goes toward.')
    if (cents === null || cents <= 0) return setErr('Enter the amount, like 800.')
    if (![f, t].every((d) => Number.isInteger(d) && d >= 1 && d <= 31) || f > t) return setErr('Use days of the month, 1 to 31, from first to last.')
    onSave(categoryId, cents, f, t)
  }

  return (
    <div className="fixed inset-0 z-20 flex items-end justify-center bg-slate-900/40" onClick={onClose}>
      <form role="dialog" aria-modal="true" aria-labelledby="contrib-h" onSubmit={submit} onClick={(e) => e.stopPropagation()} className={sheetCls}>
        <h2 id="contrib-h" className="text-lg font-semibold">
          {existing ? 'Fixed contribution' : `Add what ${who} sends`}
        </h2>
        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Toward</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputCls}>
            {bills.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Amount each month</span>
          <div className={`${inputCls} flex items-center`}>
            <span className="text-sm text-slate-500">$</span>
            <input aria-label="Amount" value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" className="w-full bg-transparent px-1 text-right outline-none" />
          </div>
        </label>
        <fieldset className="mt-4">
          <legend className="text-sm font-medium text-slate-700">Usually arrives between</legend>
          <div className="mt-1 grid grid-cols-2 gap-3">
            <input aria-label="From day" value={from} onChange={(e) => setFrom(e.target.value)} inputMode="numeric" className={inputCls} />
            <input aria-label="To day" value={to} onChange={(e) => setTo(e.target.value)} inputMode="numeric" className={inputCls} />
          </div>
          <p className="mt-1 text-xs text-slate-500">The largest transfer from {who} in these days is matched to it. One you tag in Review counts on any day.</p>
        </fieldset>
        {err && (
          <p role="alert" className="mt-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
            {err}
          </p>
        )}
        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-xl bg-slate-100 py-3 font-semibold text-slate-700">
            Cancel
          </button>
          <button type="submit" className="flex-1 rounded-xl bg-slate-900 py-3 font-semibold text-white">
            Save
          </button>
        </div>
        {onRemove && (
          <button type="button" onClick={onRemove} className="mt-3 w-full py-2 text-sm font-medium text-red-600">
            Remove
          </button>
        )}
      </form>
    </div>
  )
}


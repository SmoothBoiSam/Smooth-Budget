import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ApiError, budgetApi, partnerPlanApi, type Category, type ContributionStatus, type PartnerOverview } from './api'
import { ErrorNotice, Loading, Page, PageHeader, smallBtn } from './ui'
import PartnerSetting from './PartnerSetting'
import { centsToInput, formatCents, formatDay, ordinal, parseDollars, titleCase } from './format'

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
    <Page>
      <PageHeader title="Partner" back={{ href: '#/more', label: 'More' }} />
      <div className="mt-4">
        {/* The name on their transfers, moved here from More → Settings (DECISIONS.md 156). */}
        <PartnerSetting onChanged={() => void load()} />
      </div>

      {notice && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {notice}
        </p>
      )}
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!o && !error && <Loading />}

      {o && !o.partnerName && (
        <p className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-gold-ink">
          Set your partner’s name as it shows on their transfers (above) so the app can recognize them.
        </p>
      )}

      {o && (
        <>
          <section aria-labelledby="fixed-h" className="mt-5">
            <h2 id="fixed-h" className="px-1 text-sm font-semibold text-ink-soft">
              Fixed contributions
            </h2>
            <p className="px-1 text-xs text-ink-faint">What {who} sends each month toward a bill.</p>
            {o.contributions.length === 0 ? (
              <button type="button" onClick={() => setEditing(null)} className="mt-2 w-full rounded-2xl neu px-4 py-3 text-left text-sm">
                <span className="font-medium">+ Add one</span>
                <span className="block text-xs text-ink-soft">For example, $800 a month toward Rent.</span>
              </button>
            ) : (
              <ul className="mt-2 space-y-3">
                {o.contributions.map((c) => (
                  <li key={c.id}>
                    <button type="button" onClick={() => setEditing(c)} className="block w-full rounded-2xl neu px-4 py-3 text-left">
                      <span className="flex items-baseline justify-between gap-3">
                        <span className="text-sm font-medium">
                          {formatCents(c.amountCents)} toward {c.categoryName}
                        </span>
                        <span className="text-xs text-ink-soft">
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
              <button type="button" onClick={() => setEditing(null)} className={`${smallBtn} mt-3 text-ink`}>
                Add another
              </button>
            )}
            {/* Sent early (DECISIONS.md 166): when she's paid before you, this month's money can land before the month starts. */}
            {o.earlyCandidates.length > 0 && (
              <div className="mt-3 rounded-2xl neu-inset px-4 py-3 text-sm text-ink">
                <p className="font-medium">Did {who} send this month’s early?</p>
                <p className="mt-0.5 text-xs">
                  Money from just before this month started can count toward it.
                </p>
                <ul className="mt-2 space-y-1.5">
                  {o.earlyCandidates.map((d) => (
                    <li key={d.id}>
                      <button
                        type="button"
                        onClick={() => void act(() => partnerPlanApi.countEarly(d.id), `The ${formatCents(d.amountCents)} from ${formatDay(d.date)} counts toward this month now.`)}
                        className="w-full rounded-lg bg-surface px-3 py-2 text-left text-sm font-medium"
                      >
                        Count the {formatCents(d.amountCents)} from {formatDay(d.date)} for this month
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            )}
            {o.countedEarly.map((d) => (
              <p key={d.id} className="mt-3 text-xs text-ink-soft">
                The {formatCents(d.amountCents)} from {formatDay(d.date)} was sent early and counts toward this month.{' '}
                <button
                  type="button"
                  onClick={() => void act(() => partnerPlanApi.undoEarly(d.id), `The ${formatCents(d.amountCents)} from ${formatDay(d.date)} counts in the month it came again.`)}
                  className={`${smallBtn} py-1! text-danger`}
                >
                  Undo
                </button>
              </p>
            ))}
          </section>

          {o.recent.length > 0 && (
            <section aria-labelledby="recent-h" className="mt-6">
              <h2 id="recent-h" className="px-1 text-sm font-semibold text-ink-soft">
                Recent
              </h2>
              <ul className="mt-2 divide-y divide-ink/[0.07] overflow-hidden rounded-2xl neu text-sm">
                {o.recent.map((r) => (
                  <li key={r.id} className="flex items-baseline justify-between gap-3 px-4 py-2.5">
                    <span className="min-w-0 truncate">
                      {formatDay(r.date)} · {purposeLabel[r.purpose]}
                      {r.categoryName && <span className="text-ink-soft"> · {r.categoryName}</span>}
                      {r.purpose === 'waiting' && (
                        <a href="#/review" className={`${smallBtn} ml-1 py-1! text-gold-ink`}>
                          label it
                        </a>
                      )}
                    </span>
                    <span className={`shrink-0 tabular-nums ${r.amountCents < 0 ? 'text-good' : ''}`}>
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
    </Page>
  )
}

function MonthStatus({ s }: { s: ContributionStatus }) {
  const [text, cls] =
    s.state === 'received'
      ? [`This month: received ${formatCents(s.receivedCents)}.`, 'text-good']
      : s.state === 'expected'
        ? [`This month: expected by ${formatDay(s.windowEnd)}. It already counts.`, 'text-ink-soft']
        : s.state === 'late'
          ? [`This month: late. Left to Spend counts without ${formatCents(s.shortfallCents)}.`, 'font-medium text-danger']
          : [
              `This month: ${formatCents(s.receivedCents)} came, not ${formatCents(s.expectedCents)}${s.shortfallCents > 0 ? `. Left to Spend counts without ${formatCents(s.shortfallCents)}.` : '.'}`,
              'font-medium text-gold-ink',
            ]
  return <span className={`mt-1 block text-xs ${cls}`}>{text}</span>
}

const sheetCls = 'max-h-[90vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-surface p-5 pb-[max(1.25rem,env(safe-area-inset-bottom))]'
const inputCls = 'mt-1 block w-full rounded-xl neu-inset px-3 py-2.5 text-base'

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
          <span className="text-sm font-medium text-ink-soft">Toward</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputCls}>
            {bills.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label className="mt-4 block">
          <span className="text-sm font-medium text-ink-soft">Amount each month</span>
          <div className={`${inputCls} flex items-center`}>
            <span className="text-sm text-ink-soft">$</span>
            <input aria-label="Amount" value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" className="w-full bg-transparent px-1 text-right outline-none" />
          </div>
        </label>
        <fieldset className="mt-4">
          <legend className="text-sm font-medium text-ink-soft">Usually arrives between</legend>
          <div className="mt-1 grid grid-cols-2 gap-3">
            <input aria-label="From day" value={from} onChange={(e) => setFrom(e.target.value)} inputMode="numeric" className={inputCls} />
            <input aria-label="To day" value={to} onChange={(e) => setTo(e.target.value)} inputMode="numeric" className={inputCls} />
          </div>
          <p className="mt-1 text-xs text-ink-soft">The largest transfer from {who} in these days counts.</p>
        </fieldset>
        {err && (
          <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {err}
          </p>
        )}
        <div className="mt-5 flex gap-3">
          <button type="button" onClick={onClose} className="flex-1 rounded-2xl neu py-3 font-semibold text-danger text-sm">
            Cancel
          </button>
          <button type="submit" className="flex-1 rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink text-sm">
            Save
          </button>
        </div>
        {onRemove && (
          <button type="button" onClick={onRemove} className="mt-3 w-full py-2 text-sm font-medium text-danger">
            Remove
          </button>
        )}
      </form>
    </div>
  )
}


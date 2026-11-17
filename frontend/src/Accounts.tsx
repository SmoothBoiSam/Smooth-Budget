import { useEffect, useState } from 'react'
import { accountsApi, type AccountBalance, type AccountKind, type Cash } from './api'
import { formatCents } from './format'
import { CardLink, Page, PageHeader } from './ui'

/** Where each kind of account lives (DECISIONS.md 256): its page under Money, and what that page is called. */
export type AccountSection = 'debit' | 'debt' | 'savings'
const sections: Record<AccountSection, { kind: AccountKind; title: string; empty: string }> = {
  debit: { kind: 'checking', title: 'Debit', empty: 'No checking account yet. Connect your bank in More → Bank connections, or import a CSV.' },
  debt: { kind: 'card', title: 'Debt', empty: 'No credit cards yet. Connect your bank in More → Bank connections, or import a CSV.' },
  savings: { kind: 'savings', title: 'Savings', empty: 'No savings account yet. Connect your bank in More → Bank connections, or import a CSV.' },
}

const label = (a: { name: string; mask: string | null }) => (a.mask ? `${a.name} ••${a.mask}` : a.name)

/** A card owes when its balance is below zero; any other account holds money. */
function Balance({ a, kind }: { a: AccountBalance; kind: AccountKind }) {
  if (kind === 'card')
    return (
      <span className="shrink-0 text-right text-sm font-semibold tabular-nums">
        {a.balanceCents < 0 ? formatCents(-a.balanceCents) : formatCents(0)}
        <span className="block text-[11px] font-normal text-ink-faint">{a.balanceCents > 0 ? `${formatCents(a.balanceCents)} credit` : 'owed'}</span>
      </span>
    )
  return <span className={`shrink-0 text-sm font-semibold tabular-nums ${a.balanceCents < 0 ? 'text-danger' : ''}`}>{formatCents(a.balanceCents)}</span>
}

/**
 * The Accounts card on Debit, Debt and Savings (DECISIONS.md 256): every account of that kind, from the bank or tracked by
 * you, at its balance now with pending charges already off. Tap one for its transactions; the button shows them all.
 */
export function AccountsCard({ section }: { section: AccountSection }) {
  const { kind, empty } = sections[section]
  const [accounts, setAccounts] = useState<AccountBalance[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let live = true
    accountsApi
      .balances(kind)
      .then((a) => live && setAccounts(a))
      .catch(() => live && setFailed(true))
    return () => {
      live = false
    }
  }, [kind])

  return (
    <section aria-labelledby={`accounts-${section}-h`} className="mt-4 rounded-3xl neu p-5">
      <h2 id={`accounts-${section}-h`} className="text-sm font-semibold">
        Accounts
      </h2>
      {accounts === null ? (
        failed ? (
          <p className="mt-2 text-xs text-ink-soft">Your accounts couldn’t load. Come back to try again.</p>
        ) : (
          <div className="mt-3 h-16 animate-pulse rounded-2xl neu-inset" />
        )
      ) : accounts.length === 0 ? (
        <p className="mt-2 text-xs text-ink-soft">{empty}</p>
      ) : (
        <ul className="mt-2 divide-y divide-ink/[0.07]">
          {accounts.map((a) => (
            <li key={a.id}>
              <a href={`#/transactions?accountId=${a.id}&back=${section}`} className="flex items-center gap-3 py-2.5">
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium">{label(a)}</span>
                  <span className="block truncate text-xs text-ink-faint">
                    {a.bankSynced ? 'From your bank' : 'Tracked by you'}
                    {a.pendingCents > 0 && ` · ${formatCents(a.pendingCents)} pending, already off`}
                    {a.pendingCents < 0 && ` · ${formatCents(-a.pendingCents)} pending in, already added`}
                    {!a.includedInBudget && ' · not in your budget'}
                  </span>
                </span>
                <Balance a={a} kind={kind} />
              </a>
            </li>
          ))}
        </ul>
      )}
      <CardLink href={`#/transactions?accountType=${sections[section].kind}&back=${section}`}>Transactions</CardLink>
    </section>
  )
}

export default function DebitScreen() {
  return (
    <Page>
      <PageHeader title="Debit" back={{ href: '#/payday', label: 'Money' }} />
      <CashCard />
      <AccountsCard section="debit" />
    </Page>
  )
}

const shortDay = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
const longDay = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })
function niceStep(cents: number) {
  const dollars = Math.max(1, cents / 100)
  const pow = 10 ** Math.floor(Math.log10(dollars))
  return ([1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].map((m) => m * pow).find((v) => v >= dollars) ?? 10 * pow) * 100
}
function shortMoney(cents: number) {
  const d = Math.abs(cents) / 100
  const s = d >= 1000 ? `$${(d / 1000).toFixed(d % 1000 === 0 ? 0 : 1)}k` : `$${Math.round(d)}`
  return cents < 0 ? `−${s}` : s
}

/**
 * Debit's Cash card (DECISIONS.md 257): the money in all your checking accounts now, pending charges already off, and a
 * line of each day's closing total over the last three months (tap or hover a day for its number).
 */
export function CashCard() {
  const [cash, setCash] = useState<Cash | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    let live = true
    accountsApi
      .cash()
      .then((c) => live && setCash(c))
      .catch(() => live && setFailed(true))
    return () => {
      live = false
    }
  }, [])

  return (
    <section aria-labelledby="cash-h" className="mt-4 rounded-3xl neu p-5">
      <h2 id="cash-h" className="text-sm font-semibold">
        Cash
      </h2>
      {cash === null ? (
        failed ? (
          <p className="mt-2 text-xs text-ink-soft">Your cash couldn’t load. Come back to try again.</p>
        ) : (
          <div className="mt-2 h-40 animate-pulse rounded-2xl neu-inset" />
        )
      ) : (
        <>
          <p className={`text-4xl font-bold tabular-nums tracking-tight ${cash.totalCents < 0 ? 'text-danger' : ''}`}>{formatCents(cash.totalCents)}</p>
          <p className="text-xs text-ink-soft">In all your checking accounts, pending charges already off.</p>
          {cash.history.length > 1 && cash.history.some((p) => p.balanceCents !== cash.totalCents) && <CashChart points={cash.history} />}
        </>
      )}
    </section>
  )
}

/** Each day's closing cash, one line (the card's title names it). */
function CashChart({ points }: { points: { date: string; balanceCents: number }[] }) {
  const [hover, setHover] = useState<number | null>(null)
  const W = 340
  const H = 140
  const pad = { l: 44, r: 8, t: 10, b: 22 }
  const values = points.map((p) => p.balanceCents)
  const hi = Math.max(...values, 0)
  const lo = Math.min(...values, 0)
  // The range rounded out to even steps, so gridlines land on round amounts; below zero only when cash went below zero.
  const step = niceStep((hi - lo) / 2 || 100)
  const top = Math.max(step, Math.ceil(hi / step) * step)
  const bottom = Math.floor(lo / step) * step
  const ticks = Array.from({ length: Math.round((top - bottom) / step) + 1 }, (_, i) => bottom + i * step)
  const x = (i: number) => pad.l + (i / (points.length - 1)) * (W - pad.l - pad.r)
  const y = (c: number) => pad.t + (1 - (c - bottom) / (top - bottom)) * (H - pad.t - pad.b)
  const line = values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join('')
  const base = y(Math.max(bottom, 0)).toFixed(1)
  const area = `${line}L${x(points.length - 1).toFixed(1)},${base}L${x(0)},${base}Z`
  const h = hover === null ? null : points[hover]

  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    const box = e.currentTarget.getBoundingClientRect()
    const px = ((e.clientX - box.left) / box.width) * W
    const i = Math.round(((px - pad.l) / (W - pad.l - pad.r)) * (points.length - 1))
    setHover(Math.max(0, Math.min(points.length - 1, i)))
  }

  return (
    <figure className="mt-3">
      <figcaption className="flex min-h-4 items-baseline justify-between gap-3 text-xs text-ink-soft">
        <span>The last 3 months</span>
        <span className="tabular-nums" aria-live="polite">
          {h ? `${longDay(h.date)}: ${formatCents(h.balanceCents)}` : ''}
        </span>
      </figcaption>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        className="mt-2 w-full touch-none select-none"
        role="img"
        aria-label={`Cash went from ${formatCents(values[0])} on ${shortDay(points[0].date)} to ${formatCents(values[values.length - 1])} today.`}
        onPointerMove={onMove}
        onPointerDown={onMove}
        onPointerLeave={() => setHover(null)}
      >
        {ticks.map((t) => (
          <g key={t}>
            <line x1={pad.l} x2={W - pad.r} y1={y(t)} y2={y(t)} className={t === 0 ? 'stroke-slate-400' : 'stroke-slate-200'} strokeWidth={1} />
            <text x={pad.l - 6} y={y(t) + 3} textAnchor="end" className="fill-slate-500 text-[10px] tabular-nums">
              {shortMoney(t)}
            </text>
          </g>
        ))}
        <path d={area} className="fill-emerald-600" fillOpacity={0.12} />
        <path d={line} className="stroke-emerald-600" strokeWidth={2} fill="none" strokeLinejoin="round" strokeLinecap="round" />
        {[0, points.length - 1].map((i) => (
          <text key={i} x={x(i)} y={H - 6} textAnchor={i === 0 ? 'start' : 'end'} className="fill-slate-500 text-[10px]">
            {i === points.length - 1 ? 'Today' : shortDay(points[i].date)}
          </text>
        ))}
        {h && hover !== null && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={pad.t} y2={H - pad.b} className="stroke-slate-400" strokeWidth={1} strokeDasharray="3 3" />
            <circle cx={x(hover)} cy={y(h.balanceCents)} r={4} className="fill-emerald-600 stroke-white" strokeWidth={2} />
          </g>
        )}
      </svg>
    </figure>
  )
}

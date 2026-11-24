import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ask } from './confirm'
import { accountsApi, budgetApi, type Account, type AccountImpact, type ManagedAccount } from './api'
import { formatCents, formatDay } from './format'
import { Card, ErrorNotice, Loading, Page, PageHeader, Row, Rows, reviewPrimary, reviewSecondary, smallBtn, smallBtnOn } from './ui'

const kinds: { type: ManagedAccount['type']; title: string }[] = [
  { type: 'Checking', title: 'Checking' },
  { type: 'Savings', title: 'Savings' },
  { type: 'CreditCard', title: 'Credit cards' },
]
const kindLabel: Record<ManagedAccount['type'], string> = { Checking: 'Checking', Savings: 'Savings', CreditCard: 'Credit card' }
const where = (a: ManagedAccount) => (a.bankSynced ? (a.bankName ?? 'Bank') : 'Uploads')
/** A card owes money: show what's owed, not a negative balance. */
const money = (a: { type: ManagedAccount['type']; balanceCents: number }) =>
  a.type === 'CreditCard' ? (a.balanceCents < 0 ? `${formatCents(-a.balanceCents)} owed` : formatCents(a.balanceCents)) : formatCents(a.balanceCents)

function useAccounts() {
  const [list, setList] = useState<ManagedAccount[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(
    () =>
      accountsApi
        .manage()
        .then((l) => {
          setList(l)
          setError(null)
        })
        .catch((e) => setError(e instanceof Error ? e.message : 'Could not load your accounts.')),
    [],
  )
  useEffect(() => {
    void load()
  }, [load])
  return { list, error, load }
}

/**
 * More → Accounts (DECISIONS.md 318): every account in one place, by kind, with its bank and balance. Each opens its own
 * page. Banks, uploads and adding an account are here too.
 */
export function AccountsList({ back }: { back: { href: string; label: string } }) {
  const { list, error, load } = useAccounts()
  const [adding, setAdding] = useState(false)

  const open = list?.filter((a) => !a.closedOn) ?? []
  const closed = list?.filter((a) => a.closedOn) ?? []
  return (
    <Page>
      <PageHeader title="Accounts" back={back} />
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!list && !error && <Loading shape="list" />}
      {list && list.length === 0 && <p className="mt-6 px-1 text-sm text-ink-soft">No accounts yet. Connect a bank, or add one you’ll upload files for.</p>}
      {kinds
        .filter((k) => open.some((a) => a.type === k.type))
        .map((k) => (
          <Card key={k.type} title={k.title}>
            <Rows>
              {open
                .filter((a) => a.type === k.type)
                .map((a) => (
                  <Row key={a.id} label={a.label} hint={where(a)} value={money(a)} href={`#/more/accounts/${a.id}`} />
                ))}
            </Rows>
          </Card>
        ))}
      {closed.length > 0 && (
        <Card title="Closed">
          <Rows>
            {closed.map((a) => (
              <Row
                key={a.id}
                label={<span className="text-ink-soft">{a.label}</span>}
                hint={`Closed ${formatDay(a.closedOn!)}${a.replacedBy ? ` · now ${a.replacedBy}` : ''}`}
                href={`#/more/accounts/${a.id}`}
              />
            ))}
          </Rows>
        </Card>
      )}

      <Card title="Add an account">
        {adding ? (
          <div className="px-5 pb-4">
            <AddAccountForm
              onDone={(a) => {
                setAdding(false)
                if (a) window.location.hash = `#/more/accounts/${a.id}`
              }}
            />
          </div>
        ) : (
          <Rows>
            <Row label="Connect a bank" hint="Syncs on its own, through Plaid" href="#/more/banks" />
            <Row label="Add one you upload files for" hint="CSV exports from your bank" onClick={() => setAdding(true)} />
          </Rows>
        )}
      </Card>

      <Card title="Banks and files">
        <Rows>
          <Row label="Bank connections" hint="Sync, reconnect, add accounts, disconnect" href="#/more/banks" />
          <Row label="Upload a CSV" hint="And the files you’ve uploaded" href="#/more/import" />
        </Rows>
      </Card>
    </Page>
  )
}

/** One account's page: its name, balance, files, and closing or deleting it (DECISIONS.md 318). */
export function AccountPage({ id, onChanged }: { id: string; onChanged: () => void }) {
  const { list, error, load } = useAccounts()
  const [ending, setEnding] = useState<'close' | 'delete' | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const back = { href: '#/more/accounts', label: 'Accounts' }
  const a = list?.find((x) => x.id === id)

  function changed(note?: string) {
    setEnding(null)
    setMessage(note ?? null)
    onChanged()
    void load()
  }

  async function reopen() {
    if (!a) return
    await accountsApi.reopen(a.id)
    changed('Reopened. It counts in your balances and tasks again.')
  }

  if (error) return <Page><PageHeader title="Account" back={back} /><ErrorNotice message={error} onRetry={() => void load()} /></Page>
  if (!list) return <Page><PageHeader title="Account" back={back} /><Loading shape="list" /></Page>
  if (!a)
    return (
      <Page>
        <PageHeader title="Account" back={back} />
        <p className="mt-6 px-1 text-sm text-ink-soft">{message ?? 'This account isn’t here any more.'}</p>
      </Page>
    )

  const span = a.firstDate && a.lastDate ? ` from ${formatDay(a.firstDate)} to ${formatDay(a.lastDate)}` : ''
  return (
    <Page>
      <PageHeader title={a.label} sub={`${kindLabel[a.type]} · ${where(a)}${a.closedOn ? ` · closed ${formatDay(a.closedOn)}` : ''}`} back={back} />
      {message && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {message}
        </p>
      )}

      <Card title="Name">
        <div className="px-5 py-3.5">
          <NicknameField account={a} onSaved={() => changed()} />
        </div>
      </Card>

      <Card title="Balance">
        <Rows>
          <Row
            label={a.type === 'CreditCard' ? 'Owed' : 'Balance'}
            hint={a.bankSynced ? `From ${a.bankName ?? 'your bank'}` : a.balanceAsOf ? `As of ${formatDay(a.balanceAsOf)}, moved by activity since` : 'From its transactions'}
            value={a.type === 'CreditCard' ? formatCents(Math.max(0, -a.balanceCents)) : formatCents(a.balanceCents)}
          />
          {!a.bankSynced && a.type !== 'CreditCard' && !a.closedOn && (
            <li className="px-5 py-3.5">
              <BalanceField account={a} onSaved={() => changed('Balance saved.')} />
            </li>
          )}
          {!a.bankSynced && a.type === 'CreditCard' && !a.closedOn && <Row label="Set what it owes on Debt" href="#/debt" />}
        </Rows>
      </Card>

      <Card title="Activity">
        <Rows>
          <Row label={`${a.transactionCount} ${a.transactionCount === 1 ? 'transaction' : 'transactions'}`} hint={span ? span.trim() : undefined} />
          {!a.bankSynced && <Row label="Upload a CSV" hint="Files from your bank for this account" href={`#/more/import/${a.id}`} />}
          {a.bankId && <Row label={a.bankName ?? 'Bank connection'} hint="Sync, reconnect or disconnect" href="#/more/banks" />}
        </Rows>
      </Card>

      {ending ? (
        <EndAccount account={a} mode={ending} onCancel={() => setEnding(null)} onDone={(note) => changed(note)} />
      ) : (
        <Card>
          <div className="flex flex-col gap-3 p-5">
            {a.closedOn ? (
              <>
                <p className="text-sm text-ink-soft">
                  Closed accounts keep their history: past months, transfers and bills still count what happened on them.
                  {a.replacedBy && ` What pointed at it goes to ${a.replacedBy}.`}
                </p>
                <button type="button" onClick={() => void reopen()} className={reviewSecondary}>
                  Reopen
                </button>
              </>
            ) : (
              <button type="button" onClick={() => setEnding('close')} className={reviewSecondary}>
                Close account
              </button>
            )}
            <button type="button" onClick={() => setEnding('delete')} className={`${reviewSecondary} text-danger!`}>
              Delete account
            </button>
          </div>
        </Card>
      )}
    </Page>
  )
}

/**
 * Closing or deleting, after saying what it touches. Closing keeps its history and takes it out of everything about now;
 * deleting takes its transactions too. Either way its goals, its savings share and a leftover move still to make go to the
 * account you pick.
 */
function EndAccount({ account: a, mode, onCancel, onDone }: { account: ManagedAccount; mode: 'close' | 'delete'; onCancel: () => void; onDone: (note: string) => void }) {
  const [impact, setImpact] = useState<AccountImpact | null>(null)
  const [moveTo, setMoveTo] = useState<string>('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    accountsApi
      .impact(a.id)
      .then((i) => {
        setImpact(i)
        setMoveTo(i.moveTo[0]?.id ?? '')
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not check this account.'))
  }, [a.id])

  if (error) return <ErrorNotice message={error} />
  if (!impact) return <Loading shape="list" />

  const owes = a.type === 'CreditCard' ? Math.max(0, -impact.balanceCents) : 0
  const holds = a.type !== 'CreditCard' && impact.balanceCents !== 0
  const pointing = [
    impact.goals.length > 0 && `${impact.goals.length === 1 ? 'its goal' : 'its goals'} (${impact.goals.join(', ')})`,
    impact.savingsPercent > 0 && `its ${impact.savingsPercent}% of each month’s savings`,
    impact.openSweeps > 0 && `${impact.openSweeps === 1 ? 'a leftover move' : `${impact.openSweeps} leftover moves`} still to make`,
  ].filter(Boolean) as string[]
  const moveName = impact.moveTo.find((m) => m.id === moveTo)?.label

  async function go() {
    if (mode === 'delete' && !(await ask(`Delete ${a.label} and its ${impact!.transactionCount} transactions? This can’t be undone.`, { yes: 'Delete', danger: true }))) return
    setBusy(true)
    setError(null)
    try {
      if (mode === 'close') await accountsApi.close(a.id, moveTo || null)
      else await accountsApi.remove(a.id, moveTo || null)
      const bank = impact!.lastOnBank && impact!.bankName
        ? ` It was the last open account on ${impact!.bankName}: once its last transactions are in, you can disconnect the bank under Bank connections.`
        : ''
      onDone(mode === 'close' ? `Closed.${bank}` : `Deleted.${bank}`)
      if (mode === 'delete') window.location.hash = '#/more/accounts'
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Something went wrong.')
      setBusy(false)
    }
  }

  return (
    <Card title={mode === 'close' ? 'Close this account' : 'Delete this account'}>
      <div className="space-y-3 p-5 text-sm">
        {mode === 'close' ? (
          <p>
            It leaves your balances, Debt, Savings, the pay period tasks and the choices for new transactions. Its {impact.transactionCount}{' '}
            transactions stay, so past months and transfers to and from it don’t change.{' '}
            {a.bankSynced ? 'It keeps syncing, so anything still on its way shows up.' : 'You can still upload a late statement for it.'}
          </p>
        ) : (
          <p>
            Its {impact.transactionCount} transactions{impact.firstDate ? ` since ${formatDay(impact.firstDate)}` : ''}
            {impact.importCount > 0 && ` and ${impact.importCount} uploaded ${impact.importCount === 1 ? 'file' : 'files'}`} are deleted with it. Past
            months lose what they counted from it, and transfers to it are left without their other side.{' '}
            {a.closedOn ? 'Being closed already keeps all of that; deleting doesn’t.' : 'To keep its history, close it instead.'}
          </p>
        )}
        {holds && (
          <p className="rounded-2xl neu-inset px-4 py-3 text-danger">
            It still shows {formatCents(impact.balanceCents)}. Move the money out first, or that money drops out of your totals.
          </p>
        )}
        {owes > 0 && (
          <p className="rounded-2xl neu-inset px-4 py-3 text-danger">
            It still owes {formatCents(owes)}. Once it’s {mode === 'close' ? 'closed' : 'gone'}, Debt and the pay period tasks stop planning payments to it.
          </p>
        )}
        {(pointing.length > 0 || impact.moveTo.length > 0) && (
          <label className="block">
            <span className="text-ink-soft">
              {pointing.length > 0 ? `Move ${pointing.join(', ')} and any bills paid from it to` : 'Bills paid from it go to'}
            </span>
            <select value={moveTo} onChange={(e) => setMoveTo(e.target.value)} className="mt-1 block w-full rounded-xl neu-inset px-3 py-3 text-base">
              {impact.moveTo.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.label}
                </option>
              ))}
              <option value="">{impact.moveTo.length > 0 ? 'None of these' : `No other open ${kindLabel[a.type].toLowerCase()} account`}</option>
            </select>
            {!moveName && impact.goals.length > 0 && <span className="mt-1 block text-xs text-ink-soft">Its goals keep what they’ve saved, tracked by hand.</span>}
          </label>
        )}
        {error && (
          <p role="alert" className="text-danger">
            {error}
          </p>
        )}
        <div className="flex gap-3 pt-1">
          <button type="button" onClick={onCancel} className={`${reviewSecondary} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={busy} onClick={() => void go()} className={`${reviewPrimary} flex-1 ${mode === 'delete' ? 'bg-danger!' : ''}`}>
            {busy ? 'Working…' : mode === 'close' ? 'Close it' : 'Delete it'}
          </button>
        </div>
      </div>
    </Card>
  )
}

/** A name of your own (DECISIONS.md 310), shown everywhere instead of its name from the bank and its last four digits. */
function NicknameField({ account: a, onSaved }: { account: ManagedAccount; onSaved: () => void }) {
  const [value, setValue] = useState(a.nickname ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const changed = value.trim() !== (a.nickname ?? '')
  const imported = a.mask ? `${a.name} ••${a.mask}` : a.name

  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    if (!changed) return
    setBusy(true)
    setError(null)
    try {
      await accountsApi.setNickname(a.id, value.trim())
      onSaved()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the nickname.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={save}>
      <label htmlFor="nickname" className="block text-xs text-ink-soft">
        Nickname · from the bank: {imported}
      </label>
      <div className="mt-1.5 flex items-center gap-2">
        <input
          id="nickname"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          maxLength={40}
          placeholder="A name of your own"
          autoComplete="off"
          className="block min-w-0 flex-1 rounded-xl neu-inset px-3 py-2 text-base"
        />
        {changed && (
          <button type="submit" disabled={busy} className={smallBtnOn}>
            {busy ? 'Saving…' : value.trim() ? 'Save' : 'Clear'}
          </button>
        )}
      </div>
      {error && (
        <p role="alert" className="mt-1 text-xs text-danger">
          {error}
        </p>
      )}
    </form>
  )
}

/** The balance of an account the bank doesn't sync, as of today: only later activity moves it (DECISIONS.md 189). */
function BalanceField({ account: a, onSaved }: { account: ManagedAccount; onSaved: () => void }) {
  const [value, setValue] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const s = value.trim().replace(/[$,\s]/g, '')
    const m = /^(-)?(\d+)(?:\.(\d{0,2}))?$/.exec(s)
    if (!m) return setError('Enter the balance in dollars, like 1250.00.')
    const cents = (Number(m[2]) * 100 + Number((m[3] ?? '').padEnd(2, '0'))) * (m[1] ? -1 : 1)
    setBusy(true)
    setError(null)
    try {
      await accountsApi.setBalance(a.id, cents)
      setValue('')
      onSaved()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the balance.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={save}>
      <label htmlFor="balance" className="block text-xs text-ink-soft">
        What your bank shows today
      </label>
      <div className="mt-1.5 flex items-center gap-2">
        <input
          id="balance"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          inputMode="decimal"
          placeholder={(a.balanceCents / 100).toFixed(2)}
          autoComplete="off"
          className="block min-w-0 flex-1 rounded-xl neu-inset px-3 py-2 text-base"
        />
        <button type="submit" disabled={busy || !value.trim()} className={smallBtn}>
          {busy ? 'Saving…' : 'Set'}
        </button>
      </div>
      {error && (
        <p role="alert" className="mt-1 text-xs text-danger">
          {error}
        </p>
      )}
    </form>
  )
}

/** A new account you'll upload files for: a name, its kind, and optionally its last four digits (never the full number). */
export function AddAccountForm({ onDone }: { onDone: (a: Account | null) => void }) {
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
        <select value={type} onChange={(e) => setType(e.target.value as Account['type'])} className="flex-1 rounded-xl neu-inset px-3 py-2">
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

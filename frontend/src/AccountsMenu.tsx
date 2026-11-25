import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { ask } from './confirm'
import { accountsApi, budgetApi, debtApi, plaidApi, type Account, type AccountImpact, type Debt, type ImportBatch, type ManagedAccount } from './api'
import { DebtForm } from './DebtForm'
import { formatCents, formatDay, formatTime } from './format'
import { Switch } from './NotificationSetting'
import { Card, Chevron, ErrorNotice, Loading, Page, PageHeader, Row, Rows, headerActionCls, reviewPrimary, reviewSecondary, smallBtn, smallBtnOn } from './ui'

const kinds: { type: ManagedAccount['type']; title: string }[] = [
  { type: 'Checking', title: 'Checking' },
  { type: 'Savings', title: 'Savings' },
  { type: 'CreditCard', title: 'Credit cards' },
]
const kindLabel: Record<ManagedAccount['type'], string> = { Checking: 'Checking', Savings: 'Savings', CreditCard: 'Credit card' }
/** Where its numbers come from: its bank, the files you upload, or you. */
const where = (a: ManagedAccount) => (a.bankSynced ? (a.bankName ?? 'Bank') : a.source === 'Manual' ? 'You' : 'Uploads')
/** A card owes money: show what's owed, not a negative balance. */
const money = (a: { type: ManagedAccount['type']; balanceCents: number }) =>
  a.type === 'CreditCard' ? (a.balanceCents < 0 ? `${formatCents(-a.balanceCents)} owed` : formatCents(a.balanceCents)) : formatCents(a.balanceCents)
const accountsBack = { href: '#/more/accounts', label: 'Accounts' }
/** A page opened from Debt goes back there (DECISIONS.md 326); otherwise back to Accounts. */
function backFromHash() {
  return new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('back') === 'debt' ? { href: '#/debt', label: 'Debt' } : accountsBack
}

/** Your cards and loans as Debt sees them, for their payoff details (DECISIONS.md 326). */
function useDebts() {
  const [debts, setDebts] = useState<Debt[] | null>(null)
  const load = useCallback(
    () =>
      debtApi
        .summary()
        .then((s) => setDebts(s.debts))
        .catch(() => setDebts((d) => d ?? [])),
    [],
  )
  useEffect(() => {
    void load()
  }, [load])
  return { debts, load }
}

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
 * More → Accounts (DECISIONS.md 318, 319): every account in one place, by kind, with where its numbers come from, its balance
 * and anything it needs from you. Each opens its own page. Adding an account, banks and uploads are here too.
 */
export function AccountsList({ back }: { back: { href: string; label: string } }) {
  const { list, error, load } = useAccounts()
  const { debts } = useDebts()
  const [adding, setAdding] = useState<'csv' | 'manual' | 'loan' | null>(null)

  const open = list?.filter((a) => !a.closedOn) ?? []
  const closed = list?.filter((a) => a.closedOn) ?? []
  const loans = debts?.filter((d) => !d.isCard) ?? []
  return (
    <Page>
      <PageHeader title="Accounts" back={back} />
      {error && <ErrorNotice message={error} onRetry={() => void load()} />}
      {!list && !error && <Loading shape="list" />}
      {list && list.length === 0 && loans.length === 0 && <p className="mt-6 px-1 text-sm text-ink-soft">No accounts yet. Connect a bank, or add one below.</p>}
      {kinds
        .filter((k) => open.some((a) => a.type === k.type))
        .map((k) => (
          <Card key={k.type} title={k.title}>
            <Rows>
              {open
                .filter((a) => a.type === k.type)
                .map((a) => (
                  <Row
                    key={a.id}
                    label={a.label}
                    hint={a.attention ? <span className="text-danger">{a.attention}</span> : `${where(a)}${a.includedInBudget ? '' : ' · not in your budget'}`}
                    value={money(a)}
                    href={`#/more/accounts/${a.id}`}
                  />
                ))}
            </Rows>
          </Card>
        ))}
      {/* Loans you track by hand live here with your accounts (DECISIONS.md 326). */}
      {loans.length > 0 && (
        <Card title="Loans">
          <Rows>
            {loans.map((d) => (
              <Row
                key={d.id}
                label={d.name}
                hint={`You${d.isHidden ? ' · left out of the plan' : ''}`}
                value={`${formatCents(d.balanceCents)} owed`}
                href={`#/more/accounts/loan/${d.id}`}
              />
            ))}
          </Rows>
        </Card>
      )}

      <Card title="Add an account">
        {adding === 'loan' ? (
          <DebtForm debt={null} onCancel={() => setAdding(null)} onSaved={(_, id) => (window.location.hash = `#/more/accounts/loan/${id}`)} />
        ) : adding ? (
          <div className="px-5 pb-4">
            <AddAccountForm
              withBalance={adding === 'manual'}
              onDone={(a) => {
                setAdding(null)
                if (a) window.location.hash = `#/more/accounts/${a.id}`
              }}
            />
          </div>
        ) : (
          <Rows>
            <Row label="Connect a bank" hint="It syncs on its own, through Plaid" href="#/more/banks" />
            <Row label="From a CSV" hint="Files you download from your bank" onClick={() => setAdding('csv')} />
            <Row label="Track it yourself" hint="A balance you update by hand" onClick={() => setAdding('manual')} />
            <Row label="A loan" hint="Car, student or personal, tracked by hand" onClick={() => setAdding('loan')} />
          </Rows>
        )}
      </Card>

      <Card title="Banks and files">
        <Rows>
          <Row label="Bank connections" hint="Sync, reconnect, add accounts, disconnect" href="#/more/banks" />
          <Row label="Upload a CSV" hint="And the files you’ve uploaded" href="#/more/import" />
        </Rows>
      </Card>

      {closed.length > 0 && (
        <section className="mt-6">
          <details className="group neu rounded-3xl">
            <summary className="flex cursor-pointer list-none items-center gap-3 px-5 py-3.5 text-sm">
              <span className="min-w-0 flex-1 font-semibold text-ink-soft">Closed accounts ({closed.length})</span>
              <span className="transition-transform group-open:rotate-90">
                <Chevron />
              </span>
            </summary>
            <div className="border-t border-ink/[0.07]">
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
            </div>
          </details>
        </section>
      )}
    </Page>
  )
}

/** One account's page (DECISIONS.md 318, 319): its name and kind, balance, data, its part in the budget, and ending it. */
export function AccountPage({ id, onChanged }: { id: string; onChanged: () => void }) {
  const { list, error, load } = useAccounts()
  const { debts, load: loadDebts } = useDebts()
  const [batches, setBatches] = useState<ImportBatch[]>([])
  const [ending, setEnding] = useState<'close' | 'delete' | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [version, setVersion] = useState(0)
  const [payoffNote, setPayoffNote] = useState<string | null>(null)
  const back = backFromHash()
  const a = list?.find((x) => x.id === id)
  const debt = debts?.find((d) => d.accountId === id)

  const loadBatches = useCallback(() => budgetApi.batches().then((b) => setBatches(b.filter((x) => x.accountId === id))).catch(() => undefined), [id])
  useEffect(() => {
    void loadBatches()
  }, [loadBatches])

  function changed(note?: string) {
    setEnding(null)
    setMessage(note ?? null)
    setPayoffNote(null)
    onChanged()
    void load()
    void loadBatches()
    void loadDebts().then(() => setVersion((v) => v + 1))
  }

  async function run(label: string, action: () => Promise<string | undefined>) {
    setBusy(label)
    setProblem(null)
    setMessage(null)
    try {
      const note = await action()
      changed(note)
    } catch (e) {
      setProblem(e instanceof Error ? e.message : 'Something went wrong.')
    } finally {
      setBusy(null)
    }
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

  async function changeType(type: ManagedAccount['type']) {
    if (!a || type === a.type) return
    const note =
      a.type === 'Savings'
        ? ' Its goals keep what they’ve saved, tracked by hand, and its share of your savings goes to your other savings.'
        : ''
    if (!(await ask(`Make ${a.label} a ${kindLabel[type].toLowerCase()} account? Transfers to and from it are filed again, so what counts as saving can change in past months.${note}`, { yes: 'Change it' })))
      return
    await run('Changing…', async () => {
      await accountsApi.setType(a.id, type)
      return `It’s a ${kindLabel[type].toLowerCase()} account now.`
    })
  }

  async function toggleBudget() {
    if (!a) return
    const leaving = a.includedInBudget
    if (
      leaving &&
      !(await ask(
        `Leave ${a.label} out of your budget? Every month leaves it out, past ones too, and so do the pay period tasks, Debt and Savings. You still see it here.`,
        { yes: 'Leave it out' },
      ))
    )
      return
    await run('Saving…', async () => {
      await accountsApi.setInBudget(a.id, !leaving)
      return leaving ? 'It’s out of your budget now.' : 'It counts in your budget again.'
    })
  }

  async function undo(b: ImportBatch) {
    if (!(await ask(`Remove the ${b.importedCount} transactions added by ${b.fileName}?`, { yes: 'Remove', danger: true }))) return
    await run('Removing…', async () => {
      await budgetApi.undoBatch(b.id)
      return `Removed what ${b.fileName} added.`
    })
  }

  const span = a.firstDate && a.lastDate ? `${formatDay(a.firstDate)} to ${formatDay(a.lastDate)}` : undefined
  const isCard = a.type === 'CreditCard'
  return (
    <Page>
      <PageHeader title={a.label} sub={`${kindLabel[a.type]} · ${where(a)}${a.closedOn ? ` · closed ${formatDay(a.closedOn)}` : ''}`} back={back} />
      {a.attention && (
        <p className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {a.attention}{' '}
          {a.bankId && (
            <a href="#/more/banks" className="font-semibold underline">
              Bank connections
            </a>
          )}
        </p>
      )}
      {busy && <p className="mt-4 px-1 text-sm text-ink-soft">{busy}</p>}
      {message && (
        <p role="status" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-good">
          {message}
        </p>
      )}
      {problem && (
        <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {problem}
        </p>
      )}

      <Card title="Name and kind">
        <Rows>
          <li className="px-5 py-3.5">
            <NicknameField account={a} onSaved={() => changed()} />
          </li>
          <li className="flex items-center gap-3 px-5 py-3.5 text-sm">
            <span className="min-w-0 flex-1">
              <span className="block">Kind</span>
              {isCard && <span className="mt-0.5 block text-xs text-ink-soft">A card can’t become a bank account</span>}
            </span>
            {isCard ? (
              <span className="font-semibold">Credit card</span>
            ) : (
              <select
                aria-label="Kind"
                value={a.type}
                disabled={busy !== null}
                onChange={(e) => void changeType(e.target.value as ManagedAccount['type'])}
                className="rounded-xl neu-inset px-3 py-2 text-base"
              >
                <option value="Checking">Checking</option>
                <option value="Savings">Savings</option>
              </select>
            )}
          </li>
        </Rows>
      </Card>

      <Card title="Balance">
        <Rows>
          <Row
            label={isCard ? 'Owed' : 'Balance'}
            hint={
              a.bankSynced
                ? `From ${a.bankName ?? 'your bank'}${a.lastSyncedUtc ? `, updated ${formatTime(a.lastSyncedUtc)}` : ''}`
                : a.balanceAsOf
                  ? `As of ${formatDay(a.balanceAsOf)}, moved by activity since`
                  : 'No balance given yet'
            }
            value={isCard ? formatCents(Math.max(0, -a.balanceCents)) : formatCents(a.balanceCents)}
          />
          {!a.bankSynced && !isCard && !a.closedOn && (
            <li className="px-5 py-3.5">
              <BalanceField account={a} onSaved={() => changed('Balance saved.')} />
            </li>
          )}
        </Rows>
      </Card>

      <Card title="Data">
        <Rows>
          {a.bankId && (
            <li className="flex items-center gap-3 px-5 py-3.5 text-sm">
              <span className="min-w-0 flex-1">
                <span className="block">{a.bankName ?? 'Bank'}</span>
                <span className="mt-0.5 block text-xs text-ink-soft">{a.lastSyncedUtc ? `Synced ${formatTime(a.lastSyncedUtc)}` : 'Not synced yet'}</span>
              </span>
              <button
                type="button"
                disabled={busy !== null}
                onClick={() =>
                  void run('Syncing…', async () => {
                    const r = await plaidApi.syncAll()
                    const failed = r.find((x) => !x.ok)
                    return failed ? `${failed.institutionName ?? 'A bank'}: ${failed.error ?? 'sync failed'}` : 'Synced.'
                  })
                }
                className={headerActionCls}
              >
                Update now
              </button>
            </li>
          )}
          {a.bankId && <Row label="Reconnect or disconnect" hint="Under Bank connections" href="#/more/banks" />}
          {!a.bankSynced && <Row label="Upload a CSV for this account" hint="Files from your bank. Overlapping ones are safe." href={`#/more/import/${a.id}`} />}
          {batches.map((b) => (
            <li key={b.id} className="flex items-center gap-3 py-3 pr-5 pl-8">
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm">{b.fileName}</span>
                <span className="block text-xs text-ink-soft">
                  {formatTime(b.importedUtc)} · {b.importedCount} new
                </span>
              </span>
              {b.importedCount > 0 && (
                <button type="button" disabled={busy !== null} onClick={() => void undo(b)} className={headerActionCls + ' text-danger!'}>
                  Undo
                </button>
              )}
            </li>
          ))}
          <Row
            label="Transactions"
            hint={`${a.transactionCount} ${a.transactionCount === 1 ? 'transaction' : 'transactions'}${span ? ` · ${span}` : ''}`}
            href={`#/transactions?accountId=${a.id}&back=more/accounts/${a.id}`}
          />
        </Rows>
      </Card>

      {!a.closedOn && (
        <Card title="In your budget">
          <Rows>
            <li className="flex items-center gap-3 px-5 py-3.5 text-sm">
              <span className="min-w-0 flex-1">
                <span id="in-budget" className="block">
                  Count in budget
                </span>
                <span className="mt-0.5 block text-xs text-ink-soft">
                  {a.includedInBudget ? 'Its spending, bills and money count' : 'Shown here, left out of every month'}
                </span>
              </span>
              <Switch on={a.includedInBudget} disabled={busy !== null} onClick={() => void toggleBudget()} labelledBy="in-budget" />
            </li>
            {a.type === 'Savings' && a.includedInBudget && (
              <Row
                label="Savings"
                hint={a.goals && a.goals.length > 0 ? `Goals here: ${a.goals.join(', ')}` : 'No goals kept here'}
                value={`${a.savingsPercent}% of each month`}
                href="#/savings"
              />
            )}
          </Rows>
        </Card>
      )}

      {/* What Debt's pop-up used to edit, on the card's own page (DECISIONS.md 326). */}
      {isCard && !a.closedOn && debt && (
        <Card title="Payoff" action={<a href="#/debt" className="text-xs font-semibold text-accent">Debt ›</a>}>
          <DebtForm
            key={`${debt.id}-${version}`}
            debt={debt}
            onSaved={(note) => {
              changed()
              setPayoffNote(note)
            }}
          />
          {payoffNote && (
            <p role="status" className="-mt-2 px-5 pb-4 text-sm text-good">
              {payoffNote}
            </p>
          )}
        </Card>
      )}

      {ending ? (
        <EndAccount account={a} mode={ending} onCancel={() => setEnding(null)} onDone={(note) => changed(note)} />
      ) : (
        <section className="mt-6">
          {a.closedOn ? (
            <div className="neu rounded-3xl p-5">
              <p className="text-sm text-ink-soft">
                Closed accounts keep their history: past months, transfers and bills still count what happened on them.
                {a.replacedBy && ` What pointed at it goes to ${a.replacedBy}.`}
              </p>
              <button
                type="button"
                disabled={busy !== null}
                onClick={() =>
                  void run('Reopening…', async () => {
                    await accountsApi.reopen(a.id)
                    return 'Reopened. It counts in your balances and tasks again.'
                  })
                }
                className={`${reviewSecondary} mt-3 w-full`}
              >
                Reopen
              </button>
            </div>
          ) : (
            <button type="button" onClick={() => setEnding('close')} className={`${reviewSecondary} w-full`}>
              Close this account
            </button>
          )}
          {/* Close is the main way out; deleting is for mistakes, like an account imported twice (DECISIONS.md 318). */}
          <div className="mt-3 text-center">
            <button type="button" onClick={() => setEnding('delete')} className={`${smallBtn} bg-transparent! text-danger`}>
              Delete this account
            </button>
          </div>
        </section>
      )}
    </Page>
  )
}

/** A loan you track by hand (DECISIONS.md 326): what Debt's pop-up edited, and removing it. */
export function LoanPage({ id, onChanged }: { id: string; onChanged: () => void }) {
  const { debts, load } = useDebts()
  const [message, setMessage] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [version, setVersion] = useState(0)
  const back = backFromHash()
  const d = debts?.find((x) => x.id === id && !x.isCard)

  if (!debts) return <Page><PageHeader title="Loan" back={back} /><Loading shape="list" /></Page>
  if (!d)
    return (
      <Page>
        <PageHeader title="Loan" back={back} />
        <p className="mt-6 px-1 text-sm text-ink-soft">This loan isn’t here any more.</p>
      </Page>
    )

  async function remove() {
    if (!d || !(await ask(`Remove ${d.name}? Its details are deleted.`, { yes: 'Remove', danger: true }))) return
    try {
      await debtApi.remove(d.id)
      onChanged()
      window.location.hash = '#/more/accounts'
    } catch (e) {
      setProblem(e instanceof Error ? e.message : 'Could not remove it.')
    }
  }

  return (
    <Page>
      <PageHeader title={d.name} sub={`Loan · You${d.isHidden ? ' · left out of the plan' : ''}`} back={back} />
      {problem && (
        <p role="alert" className="mt-4 rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
          {problem}
        </p>
      )}
      <Card title="Payoff" action={<a href="#/debt" className="text-xs font-semibold text-accent">Debt ›</a>}>
        <DebtForm
          key={`${d.id}-${version}`}
          debt={d}
          onSaved={(note) => {
            setMessage(note)
            setProblem(null)
            onChanged()
            void load().then(() => setVersion((v) => v + 1))
          }}
        />
        {message && (
          <p role="status" className="-mt-2 px-5 pb-4 text-sm text-good">
            {message}
          </p>
        )}
      </Card>
      <div className="mt-6 text-center">
        <button type="button" onClick={() => void remove()} className={`${smallBtn} bg-transparent! text-danger`}>
          Remove this loan
        </button>
      </div>
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
  const [disconnect, setDisconnect] = useState(false)

  useEffect(() => {
    accountsApi
      .impact(a.id)
      .then((i) => {
        setImpact(i)
        setMoveTo(i.moveTo[0]?.id ?? '')
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not check this account.'))
  }, [a.id])

  if (error && !impact) return <ErrorNotice message={error} />
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
      if (disconnect && impact!.bankId) await plaidApi.removeItem(impact!.bankId)
      const bank = disconnect && impact!.bankName ? ` ${impact!.bankName} is disconnected.` : ''
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
            {impact.importCount > 0 && ` and ${impact.importCount} uploaded ${impact.importCount === 1 ? 'file' : 'files'}`}
            {a.type === 'CreditCard' && ' and its payoff details'} are deleted with it. Past months lose what they counted from it, and transfers to it become
            transfers to an account not in Smooth Budget.{' '}
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
            It still owes {formatCents(owes)}. Once it’s {mode === 'close' ? 'closed' : 'gone'}, it leaves your payoff plan, and the pay period tasks stop planning
            payments to it.
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
        {impact.lastOnBank && impact.bankName && (
          <label className="flex items-start gap-3">
            <input type="checkbox" checked={disconnect} onChange={(e) => setDisconnect(e.target.checked)} className="mt-1 size-4 accent-[var(--color-accent)]" />
            <span>
              Disconnect {impact.bankName} too. It’s the last open account there.
              {mode === 'close' && <span className="block text-xs text-ink-soft">Leave it connected for a few days if anything is still on its way.</span>}
            </span>
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
      <label htmlFor="nickname" className="block text-sm">
        Nickname
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
      <p className='mt-1 text-xs text-ink-soft'>{a.bankSynced ? 'From the bank' : 'Named when added'}: {imported}</p>
      {error && (
        <p role="alert" className="mt-1 text-xs text-danger">
          {error}
        </p>
      )}
    </form>
  )
}

/** Dollars typed in, optionally below zero: "-12.50" -> -1250. Null when it isn't a dollar amount. */
function centsFrom(input: string): number | null {
  const m = /^(-)?(\d+)(?:\.(\d{0,2}))?$/.exec(input.trim().replace(/[$,\s]/g, ''))
  return m ? (Number(m[2]) * 100 + Number((m[3] ?? '').padEnd(2, '0'))) * (m[1] ? -1 : 1) : null
}

/** The balance of an account the bank doesn't sync, as of today: only later activity moves it (DECISIONS.md 189). */
function BalanceField({ account: a, onSaved }: { account: ManagedAccount; onSaved: () => void }) {
  const [value, setValue] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const cents = centsFrom(value)
    if (cents === null) return setError('Enter the balance in dollars, like 1250.00.')
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

/**
 * A new account the bank doesn't sync: a name, its kind, optionally its last four digits (never the full number), for
 * one you track yourself its balance today, and for a card what it owes today (DECISIONS.md 324), required.
 */
export function AddAccountForm({ onDone, withBalance = false }: { onDone: (a: Account | null) => void; withBalance?: boolean }) {
  const [name, setName] = useState('')
  const [type, setType] = useState<Account['type']>('Checking')
  const [mask, setMask] = useState('')
  const [balance, setBalance] = useState('')
  const [error, setError] = useState<string | null>(null)
  const card = type === 'CreditCard'
  const asksBalance = withBalance || card

  async function submit(e: FormEvent) {
    e.preventDefault()
    const cents = asksBalance ? centsFrom(balance) : null
    if (asksBalance && cents === null) return setError(card ? 'Enter what it owes in dollars, like 1250.00 (0 if nothing).' : 'Enter its balance in dollars, like 1250.00.')
    if (card && cents! < 0) return setError('Enter what it owes as a positive amount.')
    try {
      onDone(await budgetApi.createAccount(name, type, mask || null, card ? null : cents, card ? cents : null))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not add account.')
    }
  }

  const note: ReactNode = card ? (
    <p className="text-xs text-ink-soft">From the card’s app or statement. Its charges and payments count from here, and you can change it on Debt.</p>
  ) : null
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
      {asksBalance && (
        <input
          value={balance}
          onChange={(e) => setBalance(e.target.value)}
          required
          inputMode="decimal"
          placeholder={card ? 'What it owes now, e.g. 1250.00' : 'Balance today, e.g. 1250.00'}
          aria-label={card ? 'What it owes now' : 'Balance today'}
          className="block w-full rounded-xl neu-inset px-3 py-2"
        />
      )}
      {note}
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

import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, api, budgetApi, needsLook, openPlanItems, paydayApi, recurringApi, type Me } from './api'
import BottomNav, { type Route } from './BottomNav'
import BudgetScreen from './BudgetScreen'
import DebtScreen from './DebtScreen'
import HomeScreen from './HomeScreen'
import LoginScreen from './LoginScreen'
import MoreScreen from './MoreScreen'
import PartnerScreen from './PartnerScreen'
import SharedScreen from './SharedScreen'
import ReviewScreen from './ReviewScreen'
import TasksScreen from './TasksScreen'
import PayTasksScreen from './PayTasksScreen'
import PaydayScreen from './PaydayScreen'
import PaySetupScreen from './PaySetupScreen'
import SavingsScreen from './SavingsScreen'
import { inViewTransition, reducedMotion } from './motion'
import { saved, useOnline } from './offline'
import { OfflineBar } from './ui'

// Back from a bank's own sign-in (Plaid OAuth, DECISIONS.md 185): open More, where Bank connections picks Plaid up again.
// The path and ?oauth_state_id stay in the address for Plaid to read.
if (window.location.pathname.startsWith('/plaid-oauth') && !window.location.hash.startsWith('#/more/banks')) window.location.hash = '#/more/banks'

function currentRoute(): Route {
  let hash = window.location.hash.replace(/^#\/?/, '')
  // Old links land where their page lives now: Bills moved into Review (DECISIONS.md 152), the inbox became Review, and
  // Review became Tasks (DECISIONS.md 206), whose plain #/review meant transactions.
  const moved =
    hash === 'bills' || hash.startsWith('bills/')
      ? 'tasks/' + hash
      : hash === 'budget/inbox' || hash === 'review'
        ? 'tasks/transactions'
        : hash.startsWith('review/')
          ? 'tasks/' + hash.slice('review/'.length)
          : null
  if (moved) {
    window.history.replaceState(null, '', '#/' + moved)
    hash = moved
  }
  if (hash === 'transactions') return 'transactions' // all transactions and search, from Home (DECISIONS.md 171)
  if (hash === 'tasks') return 'tasks'
  if (hash === 'tasks/payday') return 'paytasks'
  if (hash.startsWith('tasks/')) return 'review' // tasks/transactions, tasks/bills[/<key>]
  return hash.startsWith('budget') ? 'budget' : hash === 'debt' ? 'debt' : hash === 'savings' ? 'savings' : hash === 'payday' ? 'payday' : hash.startsWith('payday/setup') ? 'paysetup' : hash === 'partner' ? 'partner' : hash === 'shared' || hash.startsWith('join/') ? 'shared' : hash === 'more' || hash.startsWith('more/') ? 'more' : 'home'
}

export default function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined) // undefined = still checking
  const [route, setRoute] = useState<Route>(currentRoute)
  const [inboxCount, setInboxCount] = useState(0)

  useEffect(() => {
    // Offline (or the server down), the account saved on this phone opens the app to the last known numbers.
    // Anything else (401) lands on the sign-in screen, which reports its own errors.
    api
      .me()
      .then((m) => {
        saved.setMe(m)
        setMe(m)
      })
      .catch((e) => {
        const cached = e instanceof ApiError && e.offline ? saved.me() : null
        if (!cached) saved.clear()
        setMe(cached)
      })
    const onHash = () => setRoute(currentRoute())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const refreshInbox = useCallback(() => {
    budgetApi
      .inboxCount()
      .then((r) => setInboxCount(r.count + r.review))
      .catch(() => undefined)
  }, [])

  useEffect(() => {
    if (me) refreshInbox()
  }, [me, route, refreshInbox])

  // Bills that need a look count on the Tasks badge too; worked out once per sign-in, then kept current by Tasks and Bills.
  const [billsCount, setBillsCount] = useState(0)
  useEffect(() => {
    if (!me) return
    recurringApi
      .summary()
      .then((s) => setBillsCount(s.items.filter(needsLook).length))
      .catch(() => undefined)
  }, [me])

  // Payday steps still to do on the check you're on count on the Tasks badge (DECISIONS.md 188, 206). Checked as you move
  // around, since a sync can tick steps off on its own; the screens that show the plan report their own count.
  const [planCount, setPlanCount] = useState(0)
  useEffect(() => {
    if (!me || route === 'payday' || route === 'tasks' || route === 'paytasks') return
    paydayApi
      .summary()
      .then((s) => setPlanCount(openPlanItems(s)))
      .catch(() => undefined)
  }, [me, route])

  // Review passes the count it already has, so the badge doesn't ask the server again.
  const reviewChanged = useCallback((known?: number) => (typeof known === 'number' ? setInboxCount(known) : refreshInbox()), [refreshInbox])

  const online = useOnline()

  // A page change the tap's view transition didn't animate (the back button, or a phone without view transitions):
  // the new screen fades up on its own (DECISIONS.md 190).
  const screen = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const onHash = () => {
      if (inViewTransition() || reducedMotion()) return
      screen.current?.animate([{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }], {
        duration: 200,
        easing: 'cubic-bezier(0.2, 0.8, 0.2, 1)',
      })
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  const signedOut = useCallback(() => {
    saved.clear()
    setMe(null)
  }, [])
  const signedIn = useCallback((m: Me) => {
    saved.setMe(m)
    setMe(m)
  }, [])

  if (me === undefined) {
    return (
      <div role="status" className="flex min-h-screen items-center justify-center text-ink-faint">
        Loading…
      </div>
    )
  }

  if (me === null) return <LoginScreen onSignedIn={signedIn} />

  return (
    <>
      {!online && <OfflineBar />}
      <div ref={screen}>
      {route === 'home' && <HomeScreen me={me} onSignedOut={signedOut} />}
      {(route === 'budget' || route === 'transactions') && <BudgetScreen key={route} onChanged={refreshInbox} onSignedOut={signedOut} />}
      {route === 'tasks' && (
        <TasksScreen
          inboxCount={inboxCount}
          billsCount={billsCount}
          planCount={planCount}
          onInboxCount={setInboxCount}
          onBillsCount={setBillsCount}
          onPlanCount={setPlanCount}
          onSignedOut={signedOut}
        />
      )}
      {route === 'paytasks' && <PayTasksScreen onSignedOut={signedOut} onOpenItems={setPlanCount} />}
      {route === 'review' && <ReviewScreen onChanged={reviewChanged} onSignedOut={signedOut} billsCount={billsCount} onBillsCount={setBillsCount} />}
      {route === 'debt' && <DebtScreen onSignedOut={signedOut} />}
      {route === 'savings' && <SavingsScreen onSignedOut={signedOut} />}
      {route === 'payday' && <PaydayScreen onSignedOut={signedOut} onOpenItems={setPlanCount} />}
      {route === 'paysetup' && <PaySetupScreen onSignedOut={signedOut} />}
      {route === 'partner' && <PartnerScreen onSignedOut={signedOut} />}
      {route === 'shared' && <SharedScreen onSignedOut={signedOut} />}
      {route === 'more' && <MoreScreen me={me} onChanged={refreshInbox} onSignedOut={signedOut} />}
      </div>
      <BottomNav route={route} badges={{ tasks: inboxCount + billsCount + planCount }} />
    </>
  )
}

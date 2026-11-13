import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, api, budgetApi, onDataChanged, payBadge, paydayApi, type Me } from './api'
import BottomNav, { type Route } from './BottomNav'
import BudgetScreen from './BudgetScreen'
import DebtScreen from './DebtScreen'
import InterestScreen, { PaymentsScreen } from './DebtInterest'
import SavingsHistoryScreen from './SavingsHistory'
import HomeScreen from './HomeScreen'
import LoginScreen from './LoginScreen'
import MoreScreen from './MoreScreen'
import FriendsScreen from './FriendsScreen'
import FriendScreen from './FriendScreen'
import ReviewScreen from './ReviewScreen'
import TasksScreen from './TasksScreen'
import PayTasksScreen from './PayTasksScreen'
import PaydayScreen from './PaydayScreen'
import PaySetupScreen from './PaySetupScreen'
import SavingsScreen from './SavingsScreen'
import { inViewTransition, reducedMotion } from './motion'
import { saved, useOnline, useResume } from './offline'
import { ConfirmHost, DemoBar, OfflineBar } from './ui'

// Back from a bank's own sign-in (Plaid OAuth, DECISIONS.md 185): open More, where Bank connections picks Plaid up again.
// The path and ?oauth_state_id stay in the address for Plaid to read.
if (window.location.pathname.startsWith('/plaid-oauth') && !window.location.hash.startsWith('#/more/banks')) window.location.hash = '#/more/banks'

function currentRoute(): Route {
  let hash = window.location.hash.replace(/^#\/?/, '')
  // Old links land where their page lives now: Bills moved into Review (DECISIONS.md 152), the inbox became Review, and
  // Review became Tasks (DECISIONS.md 206), whose plain #/review meant transactions.
  const moved =
    hash === 'bills' || hash.startsWith('bills/') || hash === 'tasks/bills' || hash.startsWith('tasks/bills/')
      ? 'budget' // All bills is gone (DECISIONS.md 250): bills are edited from their category on Budget
      : hash === 'budget/inbox' || hash === 'review'
        ? 'tasks/transactions'
        : hash.startsWith('review/')
          ? 'tasks/' + hash.slice('review/'.length)
          : hash === 'shared'
            ? 'friends' // Shared was renamed Friends (DECISIONS.md 207)
            : hash === 'partner'
              ? 'friends/transfers' // Partner became the friend whose transfers are tracked
              : null
  if (moved) {
    window.history.replaceState(null, '', '#/' + moved)
    hash = moved
  }
  if (hash === 'transactions') return 'transactions' // all transactions and search, from Home (DECISIONS.md 171)
  if (hash === 'tasks') return 'tasks'
  if (hash === 'tasks/payday') return 'paytasks'
  if (hash.startsWith('tasks/')) return 'review' // tasks/transactions
  return hash.startsWith('budget') ? 'budget' : hash === 'debt' ? 'debt' : hash === 'debt/interest' ? 'debtinterest' : hash === 'debt/payments' ? 'debtpayments' : hash === 'savings' ? 'savings' : hash === 'savings/history' ? 'savingshistory' : hash === 'payday' ? 'payday' : hash.startsWith('payday/setup') ? 'paysetup' : hash === 'friends' || hash.startsWith('join/') ? 'friends' : hash.startsWith('friends/') ? 'friend' : hash === 'more' || hash.startsWith('more/') ? 'more' : 'home'
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

  // Payday steps still to do on the check you're on count on the Tasks badge (DECISIONS.md 188, 206). Checked as you move
  // around, since a sync can tick steps off on its own; the screens that show the plan report their own count.
  const [planCount, setPlanCount] = useState(0)
  const refreshPlan = useCallback(() => {
    paydayApi
      .summary()
      .then((s) => setPlanCount(payBadge(s)))
      .catch(() => undefined)
  }, [])
  const reportsPlan = route === 'payday' || route === 'tasks' || route === 'paytasks'
  useEffect(() => {
    if (me && !reportsPlan) refreshPlan()
  }, [me, route, reportsPlan, refreshPlan])

  // The badges follow every save, whichever screen made it (DECISIONS.md 251): the count to review right away, the
  // payday steps once you pause (they take a budget refresh on the server, and reviewing saves every few seconds).
  useEffect(() => {
    if (!me) return
    let soon: number | undefined
    let later: number | undefined
    const stop = onDataChanged(() => {
      window.clearTimeout(soon)
      window.clearTimeout(later)
      soon = window.setTimeout(refreshInbox, 300)
      if (!reportsPlan) later = window.setTimeout(refreshPlan, 2500)
    })
    return () => {
      stop()
      window.clearTimeout(soon)
      window.clearTimeout(later)
    }
  }, [me, reportsPlan, refreshInbox, refreshPlan])
  // And every return to the app after a while away: a sync may have brought new purchases in.
  useResume(
    useCallback(() => {
      if (!me) return
      refreshInbox()
      if (!reportsPlan) refreshPlan()
    }, [me, reportsPlan, refreshInbox, refreshPlan]),
  )

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
        duration: 420,
        easing: 'cubic-bezier(0.22, 1, 0.36, 1)',
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
    // The sign-in screen set the address (Home, or Friends from an invite) without a hashchange, so read it again;
    // otherwise you'd land back on the tab you signed out from (DECISIONS.md 219).
    setRoute(currentRoute())
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
      <ConfirmHost />
      {me.demo && <DemoBar onLeave={() => void api.logout().finally(() => { window.history.replaceState(null, '', '#/'); signedOut() })} />}
      <div ref={screen}>
      {route === 'home' && <HomeScreen me={me} onSignedOut={signedOut} />}
      {(route === 'budget' || route === 'transactions') && <BudgetScreen key={route} onChanged={refreshInbox} onSignedOut={signedOut} />}
      {route === 'tasks' && (
        <TasksScreen
          inboxCount={inboxCount}
          onInboxCount={setInboxCount}
          onPlanCount={setPlanCount}
          onSignedOut={signedOut}
        />
      )}
      {route === 'paytasks' && <PayTasksScreen onSignedOut={signedOut} onOpenItems={setPlanCount} />}
      {route === 'review' && <ReviewScreen onChanged={reviewChanged} onSignedOut={signedOut} />}
      {route === 'debt' && <DebtScreen onSignedOut={signedOut} />}
      {route === 'debtinterest' && <InterestScreen onSignedOut={signedOut} />}
      {route === 'debtpayments' && <PaymentsScreen onSignedOut={signedOut} />}
      {route === 'savings' && <SavingsScreen onSignedOut={signedOut} />}
      {route === 'savingshistory' && <SavingsHistoryScreen onSignedOut={signedOut} />}
      {route === 'payday' && <PaydayScreen onSignedOut={signedOut} onOpenItems={setPlanCount} />}
      {route === 'paysetup' && <PaySetupScreen onSignedOut={signedOut} />}
      {route === 'friends' && <FriendsScreen onSignedOut={signedOut} />}
      {route === 'friend' && <FriendScreen key={window.location.hash} id={window.location.hash.slice('#/friends/'.length)} onSignedOut={signedOut} />}
      {route === 'more' && <MoreScreen me={me} onChanged={refreshInbox} onSignedOut={signedOut} />}
      </div>
      <BottomNav route={route} badges={{ tasks: inboxCount + planCount }} />
    </>
  )
}

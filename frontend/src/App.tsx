import { useCallback, useEffect, useState } from 'react'
import { ApiError, api, budgetApi, type Me } from './api'
import BottomNav, { type Route } from './BottomNav'
import BillsScreen from './BillsScreen'
import BudgetScreen from './BudgetScreen'
import DebtScreen from './DebtScreen'
import HomeScreen from './HomeScreen'
import LoginScreen from './LoginScreen'
import MoreScreen from './MoreScreen'
import PartnerScreen from './PartnerScreen'
import ReviewScreen from './ReviewScreen'
import PaydayScreen from './PaydayScreen'
import PaySetupScreen from './PaySetupScreen'
import SavingsScreen from './SavingsScreen'
import { saved, useOnline } from './offline'
import { OfflineBar } from './ui'

function currentRoute(): Route {
  const hash = window.location.hash.replace(/^#\/?/, '')
  if (hash === 'budget/inbox') {
    // Reviewing moved to its own tab; old links land there.
    window.history.replaceState(null, '', '#/review')
    return 'review'
  }
  return hash.startsWith('budget') ? 'budget' : hash === 'review' ? 'review' : hash === 'debt' ? 'debt' : hash === 'savings' ? 'savings' : hash === 'payday' ? 'payday' : hash === 'payday/setup' ? 'paysetup' : hash === 'bills' || hash.startsWith('bills/') ? 'bills' : hash === 'partner' ? 'partner' : hash === 'more' ? 'more' : 'home'
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

  // Review passes the count it already has, so the badge doesn't ask the server again.
  const reviewChanged = useCallback((known?: number) => (typeof known === 'number' ? setInboxCount(known) : refreshInbox()), [refreshInbox])

  const online = useOnline()
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
      <div role="status" className="flex min-h-screen items-center justify-center text-slate-400">
        Loading…
      </div>
    )
  }

  if (me === null) return <LoginScreen onSignedIn={signedIn} />

  return (
    <>
      {!online && <OfflineBar />}
      {route === 'home' && <HomeScreen me={me} onSignedOut={signedOut} />}
      {route === 'budget' && <BudgetScreen onChanged={refreshInbox} onSignedOut={signedOut} />}
      {route === 'review' && <ReviewScreen onChanged={reviewChanged} onSignedOut={signedOut} />}
      {route === 'debt' && <DebtScreen onSignedOut={signedOut} />}
      {route === 'savings' && <SavingsScreen onSignedOut={signedOut} />}
      {route === 'payday' && <PaydayScreen onSignedOut={signedOut} />}
      {route === 'paysetup' && <PaySetupScreen onSignedOut={signedOut} />}
      {route === 'bills' && <BillsScreen onSignedOut={signedOut} />}
      {route === 'partner' && <PartnerScreen onSignedOut={signedOut} />}
      {route === 'more' && <MoreScreen me={me} onChanged={refreshInbox} onSignedOut={signedOut} />}
      <BottomNav route={route} inboxCount={inboxCount} />
    </>
  )
}

import { useCallback, useEffect, useState } from 'react'
import { api, budgetApi, type Me } from './api'
import BottomNav, { type Route } from './BottomNav'
import BudgetScreen from './BudgetScreen'
import HomeScreen from './HomeScreen'
import LoginScreen from './LoginScreen'
import MoreScreen from './MoreScreen'

function currentRoute(): Route {
  const hash = window.location.hash.replace(/^#\/?/, '')
  return hash === 'budget' || hash === 'more' ? hash : 'home'
}

export default function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined) // undefined = still checking
  const [route, setRoute] = useState<Route>(currentRoute)
  const [inboxCount, setInboxCount] = useState(0)

  useEffect(() => {
    // Any failure (401, or the API being down) lands on the sign-in screen, which reports its own errors.
    api
      .me()
      .then(setMe)
      .catch(() => setMe(null))
    const onHash = () => setRoute(currentRoute())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  const refreshInbox = useCallback(() => {
    budgetApi
      .inboxCount()
      .then((r) => setInboxCount(r.count))
      .catch(() => undefined)
  }, [])

  useEffect(() => {
    if (me) refreshInbox()
  }, [me, route, refreshInbox])

  const signedOut = useCallback(() => setMe(null), [])

  if (me === undefined) {
    return <div className="flex min-h-screen items-center justify-center text-slate-400">Loading…</div>
  }

  if (me === null) return <LoginScreen onSignedIn={setMe} />

  return (
    <>
      {route === 'home' && <HomeScreen me={me} onSignedOut={signedOut} />}
      {route === 'budget' && <BudgetScreen onChanged={refreshInbox} onSignedOut={signedOut} />}
      {route === 'more' && <MoreScreen me={me} onChanged={refreshInbox} onSignedOut={signedOut} />}
      <BottomNav route={route} inboxCount={inboxCount} />
    </>
  )
}

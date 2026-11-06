import { useEffect, useState } from 'react'
import { api, type Me } from './api'
import HomeScreen from './HomeScreen'
import LoginScreen from './LoginScreen'

export default function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined) // undefined = still checking

  useEffect(() => {
    // Any failure (401, or the API being down) lands on the sign-in screen, which reports its own errors.
    api
      .me()
      .then(setMe)
      .catch(() => setMe(null))
  }, [])

  if (me === undefined) {
    return <div className="flex min-h-screen items-center justify-center text-slate-400">Loading…</div>
  }

  if (me === null) return <LoginScreen onSignedIn={setMe} />

  return (
    <HomeScreen
      me={me}
      onSignedOut={() => setMe(null)}
    />
  )
}

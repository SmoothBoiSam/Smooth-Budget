import { useEffect, useState, type FormEvent } from 'react'
import { api, type Me } from './api'
import { smallBtn } from './ui'

export default function LoginScreen({ onSignedIn }: { onSignedIn: (me: Me) => void }) {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [signUpOpen, setSignUpOpen] = useState(false)

  // Sign-up is closed on the hosted app once your account exists; the link only shows when it's open.
  useEffect(() => {
    api
      .options()
      .then((o) => setSignUpOpen(o.signUpOpen))
      .catch(() => setSignUpOpen(false))
  }, [])

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const me = mode === 'login' ? await api.login(email, password) : await api.register(email, password)
      onSignedIn(me)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center px-6">
      <h1 className="text-3xl font-bold tracking-tight">Smooth Budget</h1>
      <p className="mt-1 text-ink-soft">Know what's safe to spend, right now.</p>

      <form onSubmit={submit} className="mt-8 space-y-4">
        <label className="block">
          <span className="text-sm font-medium text-ink-soft">Email</span>
          <input
            type="email"
            autoComplete="email"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            className="mt-1 block w-full rounded-xl neu-inset px-4 py-3 text-base outline-none focus:ring-2 focus:ring-accent"
          />
        </label>
        <label className="block">
          <span className="text-sm font-medium text-ink-soft">Password</span>
          <input
            type="password"
            autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
            required
            minLength={mode === 'register' ? 10 : undefined}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="mt-1 block w-full rounded-xl neu-inset px-4 py-3 text-base outline-none focus:ring-2 focus:ring-accent"
          />
        </label>

        {error && (
          <p role="alert" className="rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {error}
          </p>
        )}

        <button
          type="submit"
          disabled={busy}
          className="w-full rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-50 text-sm"
        >
          {busy ? 'Please wait…' : mode === 'login' ? 'Sign in' : 'Create account'}
        </button>
      </form>

      {(signUpOpen || mode === 'register') && (
        <button
          type="button"
          onClick={() => {
            setMode(mode === 'login' ? 'register' : 'login')
            setError(null)
          }}
          className={`${smallBtn} mt-4 px-4! py-2! text-sm! text-accent`}
        >
          {mode === 'login' ? 'New here? Create an account' : 'Already have an account? Sign in'}
        </button>
      )}
    </main>
  )
}

import { useEffect, useState, type FormEvent } from 'react'
import { api, joinToken, sharedApi, type InvitePeek, type Me } from './api'
import { smallBtn } from './ui'

const field = 'mt-1 block w-full rounded-xl neu-inset px-4 py-3 text-base outline-none focus:ring-2 focus:ring-accent'

export default function LoginScreen({ onSignedIn }: { onSignedIn: (me: Me) => void }) {
  // Opened from a friend's invite (#/join/<token>, DECISIONS.md 205): sign up from it, or sign in and join.
  const [invite] = useState(joinToken)
  const [peek, setPeek] = useState<InvitePeek | null>(null)
  const [mode, setMode] = useState<'login' | 'register'>(invite ? 'register' : 'login')
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [signUpOpen, setSignUpOpen] = useState(false)

  // Sign-up is closed on the hosted app once your account exists; the link only shows when it's open (or with an invite).
  useEffect(() => {
    api
      .options()
      .then((o) => setSignUpOpen(o.signUpOpen))
      .catch(() => setSignUpOpen(false))
    if (invite)
      sharedApi
        .peek(invite)
        .then(setPeek)
        .catch(() => setPeek({ valid: false, fromName: null, problem: 'That invite link could not be checked. Try again in a moment.' }))
  }, [invite])

  const invited = !!invite && peek?.valid === true
  const from = peek?.fromName ?? 'A friend'

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      let me: Me
      if (mode === 'register') me = await api.register(email, password, invited ? invite! : undefined, invited ? name : undefined)
      else {
        me = await api.login(email, password)
        if (invited) await sharedApi.join(invite!)
      }
      if (invite) window.history.replaceState(null, '', '#/friends')
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

      {invite && peek && (
        <div role="status" className="mt-6 rounded-2xl neu px-4 py-3 text-sm">
          {peek.valid ? (
            <>
              <p className="font-semibold">{from} invited you</p>
              <p className="mt-0.5 text-ink-soft">
                You get your own budget, linked with {from}’s so you can split bills and purchases. Only what you send each other is shared.
              </p>
            </>
          ) : (
            <p className="text-danger">{peek.problem}</p>
          )}
        </div>
      )}

      <form onSubmit={submit} className="mt-8 space-y-4">
        {invited && mode === 'register' && (
          <label className="block">
            <span className="text-sm font-medium text-ink-soft">Your name</span>
            <input autoComplete="given-name" required maxLength={60} value={name} onChange={(e) => setName(e.target.value)} className={field} />
            <span className="mt-1 block text-xs text-ink-faint">What {from} sees, like “Jane asks for $60”.</span>
          </label>
        )}
        <label className="block">
          <span className="text-sm font-medium text-ink-soft">Email</span>
          <input type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} className={field} />
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
            className={field}
          />
        </label>

        {error && (
          <p role="alert" className="rounded-2xl neu-inset px-4 py-3 text-sm text-danger">
            {error}
          </p>
        )}

        <button
          type="submit"
          disabled={busy || (!!invite && mode === 'register' && !invited)}
          className="w-full rounded-2xl bg-primary shadow-neu-sm py-3 font-semibold text-primary-ink disabled:opacity-50 text-sm"
        >
          {busy ? 'Please wait…' : mode === 'login' ? (invited ? `Sign in and link with ${from}` : 'Sign in') : invited ? 'Create account and link' : 'Create account'}
        </button>
      </form>

      {(signUpOpen || invited || mode === 'register') && (
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

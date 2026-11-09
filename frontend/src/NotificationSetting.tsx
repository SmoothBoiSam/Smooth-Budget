import { useEffect, useState } from 'react'
import { pushApi, type NotificationType } from './api'

// Settings → Notifications (DECISIONS.md 151, 163): on or off per phone, and a switch for each kind of notification.
// On iPhone, web apps can only notify once they're added to the Home Screen.

type State =
  | { kind: 'loading' }
  | { kind: 'unavailable'; why: string }
  | { kind: 'ready'; on: boolean; publicKey: string }

function keyBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const clean = base64url.replace(/[^A-Za-z0-9_+/-]/g, '')
  const b64 = (clean + '='.repeat((4 - (clean.length % 4)) % 4)).replace(/-/g, '+').replace(/_/g, '/')
  const raw = atob(b64)
  const out = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i)
  return out
}

const isIos = /iPhone|iPad|iPod/.test(navigator.userAgent)
const standalone = window.matchMedia('(display-mode: standalone)').matches || (navigator as { standalone?: boolean }).standalone === true

async function registration() {
  if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) return undefined
  return navigator.serviceWorker.getRegistration()
}

export default function NotificationSetting() {
  const [state, setState] = useState<State>({ kind: 'loading' })
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    void (async () => {
      try {
        const status = await pushApi.status()
        const reg = await registration()
        let next: State
        if (!status.enabled || !status.publicKey) next = { kind: 'unavailable', why: status.problem ?? "Notifications aren't set up on the server yet." }
        else if (isIos && !standalone)
          next = { kind: 'unavailable', why: 'On iPhone, add Smooth Budget to your Home Screen first (Share → Add to Home Screen), then open it from there.' }
        else if (!reg) next = { kind: 'unavailable', why: 'This browser can’t show notifications from Smooth Budget.' }
        else next = { kind: 'ready', on: Notification.permission === 'granted' && !!(await reg.pushManager.getSubscription()), publicKey: status.publicKey }
        if (live) setState(next)
      } catch {
        if (live) setState({ kind: 'unavailable', why: 'Couldn’t check notifications right now.' })
      }
    })()
    return () => {
      live = false
    }
  }, [])

  async function toggle() {
    if (state.kind !== 'ready') return
    setBusy(true)
    setMessage(null)
    try {
      const reg = await registration()
      if (!reg) throw new Error('This browser can’t show notifications from Smooth Budget.')
      if (state.on) {
        const sub = await reg.pushManager.getSubscription()
        if (sub) {
          await pushApi.unsubscribe(sub.endpoint)
          await sub.unsubscribe()
        }
        setState({ ...state, on: false })
      } else {
        if ((await Notification.requestPermission()) !== 'granted') {
          setMessage('Notifications are blocked. Allow them for Smooth Budget in your phone’s settings, then try again.')
          return
        }
        const sub = (await reg.pushManager.getSubscription())
          ?? (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(state.publicKey) }))
        await pushApi.subscribe(sub.toJSON())
        setState({ ...state, on: true })
      }
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Couldn’t change notifications.')
    } finally {
      setBusy(false)
    }
  }

  async function test() {
    setMessage(null)
    try {
      await pushApi.test()
      setMessage('Sent. It should show up in a few seconds.')
    } catch (e) {
      setMessage(e instanceof Error ? e.message : 'Couldn’t send a test.')
    }
  }

  return (
    <div className="px-5 py-3.5">
      <div className="flex items-center gap-3">
        <div className="min-w-0 flex-1">
          <p id="notify-label" className="text-sm">
            Notifications
          </p>
          <p className="text-xs text-ink-soft">
            {state.kind === 'ready' && state.on
              ? 'On for this phone. Never at night.'
              : 'Paydays, bills, overspending and more.'}
          </p>
        </div>
        {state.kind === 'ready' && <Switch on={state.on} disabled={busy} onClick={toggle} labelledBy="notify-label" />}
      </div>
      {state.kind === 'unavailable' && <p className="mt-2 text-xs text-ink-soft">{state.why}</p>}
      {state.kind === 'ready' && state.on && <NotificationTypes />}
      {state.kind === 'ready' && state.on && (
        <button type="button" onClick={test} className="mt-2 text-sm font-medium text-good">
          Send a test
        </button>
      )}
      {message && (
        <p role="status" className="mt-2 text-xs text-ink-soft">
          {message}
        </p>
      )}
    </div>
  )
}

function Switch({ on, disabled, onClick, labelledBy, small }: { on: boolean; disabled?: boolean; onClick: () => void; labelledBy: string; small?: boolean }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      aria-labelledby={labelledBy}
      disabled={disabled}
      onClick={onClick}
      className={`relative shrink-0 rounded-full transition-colors disabled:opacity-60 ${small ? 'h-6 w-10' : 'h-7 w-12'} ${on ? 'bg-accent' : 'bg-ink/15'}`}
    >
      <span
        aria-hidden
        className={`absolute top-0.5 rounded-full shadow transition-all ${small ? 'h-5 w-5' : 'h-6 w-6'} ${on ? (small ? 'left-[1.125rem]' : 'left-[1.375rem]') : 'left-0.5'}`}
        style={{ backgroundColor: '#fff' }}
      />
    </button>
  )
}

/** A switch for each kind of notification (DECISIONS.md 163). They apply to the whole household, on every phone. */
function NotificationTypes() {
  const [types, setTypes] = useState<NotificationType[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    pushApi
      .types()
      .then((t) => live && setTypes(t))
      .catch(() => live && setError('Couldn’t load the notification types.'))
    return () => {
      live = false
    }
  }, [])

  async function flip(t: NotificationType) {
    setError(null)
    setTypes((all) => all?.map((x) => (x.id === t.id ? { ...x, on: !t.on } : x)) ?? null)
    try {
      await pushApi.setType(t.id, !t.on)
    } catch {
      setTypes((all) => all?.map((x) => (x.id === t.id ? { ...x, on: t.on } : x)) ?? null)
      setError('Couldn’t save that. Try again.')
    }
  }

  if (error && !types) return <p className="mt-2 text-xs text-danger">{error}</p>
  if (!types) return null
  return (
    <ul className="mt-3 divide-y divide-ink/[0.07] border-t border-ink/10" aria-label="Kinds of notifications">
      {types.map((t) => (
        <li key={t.id} className="flex items-center gap-3 py-2.5">
          <div className="min-w-0 flex-1">
            <p id={`notify-${t.id}`} className="text-sm">
              {t.title}
            </p>
            <p className="text-xs text-ink-soft">{t.detail}</p>
          </div>
          <Switch on={t.on} onClick={() => void flip(t)} labelledBy={`notify-${t.id}`} small />
        </li>
      ))}
      {error && <li className="py-2 text-xs text-danger">{error}</li>}
    </ul>
  )
}

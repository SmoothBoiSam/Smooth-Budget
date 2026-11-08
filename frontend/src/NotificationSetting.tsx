import { useEffect, useState } from 'react'
import { pushApi } from './api'

// Settings → Notifications (DECISIONS.md 151): paydays, bills due tomorrow, a category going over, and low cash.
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
    <div className="rounded-xl bg-white px-4 py-3 ring-1 ring-slate-200">
      <div className="flex items-center gap-3">
        <div className="min-w-0 flex-1">
          <p id="notify-label" className="text-sm font-medium">
            Notifications
          </p>
          <p className="text-xs text-slate-500">Paydays, bills due tomorrow, a category going over, and low cash. Never at night.</p>
        </div>
        {state.kind === 'ready' && (
          <button
            type="button"
            role="switch"
            aria-checked={state.on}
            aria-labelledby="notify-label"
            disabled={busy}
            onClick={toggle}
            className={`relative h-7 w-12 shrink-0 rounded-full transition-colors disabled:opacity-60 ${state.on ? 'bg-emerald-600' : 'bg-slate-300'}`}
          >
            <span
              aria-hidden
              className={`absolute top-0.5 h-6 w-6 rounded-full shadow transition-all ${state.on ? 'left-[1.375rem]' : 'left-0.5'}`}
              style={{ backgroundColor: '#fff' }}
            />
          </button>
        )}
      </div>
      {state.kind === 'unavailable' && <p className="mt-2 text-xs text-slate-500">{state.why}</p>}
      {state.kind === 'ready' && state.on && (
        <button type="button" onClick={test} className="mt-2 text-sm font-medium text-emerald-700">
          Send a test
        </button>
      )}
      {message && (
        <p role="status" className="mt-2 text-xs text-slate-600">
          {message}
        </p>
      )}
    </div>
  )
}

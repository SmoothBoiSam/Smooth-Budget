// Smooth Budget's service worker (milestone 10): makes the app installable and lets it open without a connection.
// It caches only the app itself (HTML, scripts, styles, icons). API responses are never cached here; the last
// Home summary is kept by the app (src/offline.ts) so it can say how old it is.
const CACHE = 'smoothbudget-shell-v3'
const SHELL = [
  '/', '/manifest.webmanifest', '/favicon.png', '/icon-192.png', '/icon-512.png',
  '/brand/logo.png', '/brand/cat-plenty.png', '/brand/cat-low.png', '/brand/cat-over.png', '/brand/cat-tabby.png',
]

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()))
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  )
})

self.addEventListener('fetch', (event) => {
  const req = event.request
  const url = new URL(req.url)
  if (req.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/')) return
  if (req.mode === 'navigate') event.respondWith(page(req))
  else event.respondWith(asset(req))
})

// The page: always the network first, so a new version shows up right away; the saved copy when offline.
async function page(req) {
  const cache = await caches.open(CACHE)
  try {
    const res = await fetch(req)
    if (res.ok) {
      await cache.put('/', res.clone())
      await prune(cache, await res.clone().text())
    }
    return res
  } catch {
    return (await cache.match('/')) ?? Response.error()
  }
}

// Scripts, styles and icons: built files have content hashes in their names, so a saved copy is always right.
async function asset(req) {
  const cache = await caches.open(CACHE)
  const hit = await cache.match(req)
  if (hit) return hit
  const res = await fetch(req)
  if (res.ok && (new URL(req.url).pathname.startsWith('/assets/') || SHELL.includes(new URL(req.url).pathname))) await cache.put(req, res.clone())
  return res
}

// Drop built files the current page no longer uses, so old versions don't pile up.
async function prune(cache, html) {
  for (const key of await cache.keys()) {
    const path = new URL(key.url).pathname
    if (path.startsWith('/assets/') && !html.includes(path)) await cache.delete(key)
  }
}

// Phone notifications (DECISIONS.md 151): the server sends { title, body, url, tag }.
self.addEventListener('push', (event) => {
  let msg = {}
  try {
    msg = event.data ? event.data.json() : {}
  } catch {
    msg = { body: event.data ? event.data.text() : '' }
  }
  event.waitUntil(
    self.registration.showNotification(msg.title || 'Smooth Budget', {
      body: msg.body || '',
      icon: '/icon-192.png',
      badge: '/icon-192.png',
      tag: msg.tag || undefined,
      data: { url: msg.url || '#/' },
    }),
  )
})

// Tapping one opens the app (or brings it forward) on the screen it's about.
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const hash = String(event.notification.data?.url || '#/')
  const target = new URL('/' + (hash.startsWith('#') ? hash : '#/'), self.location.origin).href
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((list) => {
      const open = list.find((c) => new URL(c.url).origin === self.location.origin)
      if (open) return open.navigate(target).then((c) => (c || open).focus())
      return self.clients.openWindow(target)
    }),
  )
})

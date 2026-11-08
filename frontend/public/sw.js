// Smooth Budget's service worker (milestone 10): makes the app installable and lets it open without a connection.
// It caches only the app itself (HTML, scripts, styles, icons). API responses are never cached here; the last
// Home summary is kept by the app (src/offline.ts) so it can say how old it is.
const CACHE = 'smoothbudget-shell-v2'
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

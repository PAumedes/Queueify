// Caches the shell (icons, stylesheet, offline page) so the app is installable and fails politely offline.
// Pages and data are never cached: they are per user and live on the server.
const CACHE = 'queueify-shell-v1';
const SHELL = ['offline.html', 'queueify.css', 'icons/icon-192.png', 'icons/icon.svg'];

self.addEventListener('install', function (event) {
  event.waitUntil(caches.open(CACHE).then(function (c) { return c.addAll(SHELL); }).then(function () { return self.skipWaiting(); }));
});

self.addEventListener('activate', function (event) {
  event.waitUntil(
    caches.keys()
      .then(function (keys) { return Promise.all(keys.filter(function (k) { return k !== CACHE; }).map(function (k) { return caches.delete(k); })); })
      .then(function () { return self.clients.claim(); }));
});

self.addEventListener('fetch', function (event) {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== location.origin) return;

  if (req.mode === 'navigate') {
    event.respondWith(fetch(req).catch(function () { return caches.match('offline.html'); }));
    return;
  }
  if (SHELL.some(function (p) { return url.pathname.endsWith('/' + p); })) {
    event.respondWith(caches.match(req).then(function (hit) { return hit || fetch(req); }));
  }
});

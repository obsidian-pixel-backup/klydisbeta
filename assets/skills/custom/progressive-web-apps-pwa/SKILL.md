---
name: progressive-web-apps-pwa
description: Architecting Progressive Web Apps (PWAs): Service Workers, offline caching strategies, Web App Manifests, and push notifications.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Progressive Web Applications (PWAs)

Progressive Web Apps combine the broad reach of the web with native mobile application capabilities like offline access, push notifications, and home-screen installation.

## Core PWA Requirements

1. **Web App Manifest (`manifest.json`)**: Metadata describing application icons, display mode, theme colors, and start URL.
2. **Service Worker (`sw.js`)**: Background event script handling asset caching, offline requests, and push notifications.
3. **HTTPS Encryption**: PWAs must be served over secure origins.

---

## Service Worker Cache-First Blueprint (`sw.js`)

```javascript
const CACHE_NAME = 'app-v1';
const ASSETS_TO_CACHE = [
  '/',
  '/index.html',
  '/styles/main.css',
  '/script/app.js',
  '/offline.html'
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) => cache.addAll(ASSETS_TO_CACHE))
  );
});

self.addEventListener('fetch', (event) => {
  event.respondWith(
    caches.match(event.request).then((cachedResponse) => {
      if (cachedResponse) return cachedResponse;
      return fetch(event.request).catch(() => caches.match('/offline.html'));
    })
  );
});
```

---

## Verification Checklist

- [ ] `manifest.json` provides 192x192 and 512x512 app icons.
- [ ] Service worker handles network offline state with a friendly fallback page.
- [ ] Application installs cleanly on iOS Safari and Android Chrome.
- [ ] Lighthouse PWA audit verifies service worker registration and HTTPS.

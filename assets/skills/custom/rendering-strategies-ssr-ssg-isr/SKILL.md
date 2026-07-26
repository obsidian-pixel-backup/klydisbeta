---
name: rendering-strategies-ssr-ssg-isr
description: Evaluating web rendering strategies: Server-Side Rendering (SSR), Static Site Generation (SSG), Incremental Static Regeneration (ISR), and Client-Side Rendering (CSR).
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Web Rendering Strategies: SSR, SSG, ISR, and CSR

Choosing the right rendering architecture balances initial load latency, server infrastructure costs, and content freshness.

## Decision Matrix

| Strategy | Full Name | Freshness | Build Speed | Server Cost | Best For |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **CSR** | Client-Side Rendering | Real-time | Instant | Lowest | Internal Dashboards, SPAs |
| **SSG** | Static Site Generation | Build time | Slower | Minimal | Blogs, Marketing Sites |
| **SSR** | Server-Side Rendering | Per Request | Fast | Higher | Dynamic User Feeds |
| **ISR** | Incremental Static Regeneration | Revalidated interval | Fast | Low | E-Commerce Product Pages |

---

## Next.js App Router Data Fetching Blueprints

```typescript
// 1. Static Site Generation (SSG) - Default cached fetch
async function getStaticData() {
  const res = await fetch('https://api.example.com/posts', { cache: 'force-cache' });
  return res.json();
}

// 2. Server-Side Rendering (SSR) - Dynamic no-store fetch
async function DynamicUserData() {
  const res = await fetch('https://api.example.com/user', { cache: 'no-store' });
  return res.json();
}

// 3. Incremental Static Regeneration (ISR) - Revalidated cache
async function getRevalidatedData() {
  const res = await fetch('https://api.example.com/products', {
    next: { revalidate: 60 } // Revalidate every 60 seconds
  });
  return res.json();
}
```

---

## Verification Checklist

- [ ] Static marketing pages use SSG or ISR for maximum CDN caching.
- [ ] Personalized user dashboards use SSR or client fetching behind auth gates.
- [ ] ISR revalidation intervals balance backend DB load vs freshness needs.
- [ ] Streaming HTML (`Suspense`) delivers instant shell markup while fetching data.

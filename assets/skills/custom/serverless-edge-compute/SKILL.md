---
name: serverless-edge-compute
description: Building edge and serverless applications: Cloudflare Workers, Vercel Edge Functions, AWS Lambda cold-start optimization, and stateless architecture.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Serverless & Edge Compute Architecture

Edge compute moves code execution to CDN locations globally close to users, reducing latency down to single-digit milliseconds.

## Edge Runtime vs Standard Node.js

- **Edge Runtime (V8 Isolates)**: Instant startup (<5ms), zero cold starts, stripped web APIs (`fetch`, `Request`, `Response`). No native C++ Node modules (`fs`, `child_process`).
- **Standard Serverless (AWS Lambda Node.js)**: Full Node.js ecosystem, potential cold-start latency (200ms-1s).

---

## Vercel / Cloudflare Edge Function Blueprint

```typescript
export const config = { runtime: 'edge' };

export default async function handler(request: Request) {
  const url = new URL(request.url);
  const country = request.headers.get('x-vercel-ip-country') || 'US';

  return new Response(
    JSON.stringify({
      message: `Hello from Edge compute!`,
      location: country,
      timestamp: new Date().toISOString()
    }),
    {
      status: 200,
      headers: { 'content-type': 'application/json' }
    }
  );
}
```

---

## Verification Checklist

- [ ] Edge functions avoid importing heavy Node.js standard libraries (`fs`, `path`).
- [ ] Database connections from edge functions use HTTP/WebSocket connection pools (e.g., Neon Postgres, PlanetScale).
- [ ] Function payloads stay under execution bundle size limits (<1MB).
- [ ] Stateless architecture stores session state in Redis or cookies.

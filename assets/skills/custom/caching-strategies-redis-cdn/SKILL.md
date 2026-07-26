---
name: caching-strategies-redis-cdn
description: Implementing high-performance web caching strategies: HTTP Cache-Control headers, stale-while-revalidate, Redis caching patterns, CDN edge caching, and cache invalidation.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Caching Strategies: Redis & CDN

Effective caching dramatically reduces database query load, cuts infrastructure operational costs, and speeds up page rendering times worldwide.

## Caching Layers

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│  Browser    │───>│     CDN     │───>│ Cache / API │───>│ Database    │
│  Cache      │    │ Edge Cache  │    │ (Redis)     │    │ Storage     │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
```

---

## HTTP Cache-Control Directives Guide

```text
# Static Immutable Assets (JS, CSS, Images with content hashes)
Cache-Control: public, max-age=31536000, immutable

# Dynamic User API Data (Stale-While-Revalidate pattern)
Cache-Control: private, max-age=60, stale-while-revalidate=600

# Sensitive Account Data (Never cache)
Cache-Control: no-store, max-age=0
```

---

## Redis Cache-Aside Pattern Blueprint (TypeScript)

```typescript
import { redis } from '@/lib/redis';

export async function getCachedUser(userId: string) {
  const cacheKey = `user:${userId}`;

  // 1. Try cache hit
  const cached = await redis.get(cacheKey);
  if (cached) return JSON.parse(cached);

  // 2. Cache miss: Fetch from DB
  const user = await db.user.findUnique({ where: { id: userId } });
  if (!user) return null;

  // 3. Write back to Redis with 1-hour TTL
  await redis.set(cacheKey, JSON.stringify(user), 'EX', 3600);
  return user;
}
```

---

## Verification Checklist

- [ ] Static build assets include content hashes and `immutable` caching directives.
- [ ] Database updates explicitly invalidate or update corresponding Redis cache keys.
- [ ] Cache keys incorporate tenant and authorization scope namespace prefixes.
- [ ] TTL (Time-To-Live) expiration bounds are defined for every Redis key.

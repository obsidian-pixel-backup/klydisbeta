---
name: react-nextjs-architecture
description: Architecting production Next.js App Router applications: React Server Components (RSC), Client Components, Server Actions, custom hooks, and state management.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# React & Next.js Architecture

Next.js App Router combines React Server Components (RSC) with streaming and Server Actions to deliver fast, SEO-optimized web applications.

## Server Components vs Client Components

- **React Server Components (RSC)** (Default): Render on the server, zero client bundle footprint, direct database/backend access.
- **Client Components** (`'use client'`): Render on client, handle user interactive state (`useState`, `useEffect`, event listeners).

---

## App Router Directory Blueprint (`app/`)

```text
app/
├── layout.tsx         # Root layout wrapping all pages
├── page.tsx           # Home page component (RSC)
├── dashboard/
│   ├── layout.tsx     # Dashboard nested layout
│   ├── page.tsx       # Dashboard page
│   ├── loading.tsx    # Suspense fallback skeleton
│   └── error.tsx      # Error boundary component
└── actions/
    └── userActions.ts # Server Actions ('use server')
```

---

## Server Action Implementation Blueprint

```typescript
// app/actions/userActions.ts
'use server'

import { revalidatePath } from 'next/cache';
import { db } from '@/lib/db';

export async function updateUserProfile(userId: string, formData: FormData) {
  const name = formData.get('name') as string;
  if (!name || name.length < 2) {
    return { error: 'Name must be at least 2 characters long.' };
  }

  await db.user.update({
    where: { id: userId },
    data: { name }
  });

  revalidatePath('/dashboard/profile');
  return { success: true };
}
```

---

## Verification Checklist

- [ ] Keep `'use client'` directive pushed down to smallest interactive leaf components.
- [ ] Database queries occur directly inside RSCs or Server Actions (never client side).
- [ ] Data fetching uses `loading.tsx` or React `Suspense` for instant visual response.
- [ ] Mutations trigger `revalidatePath` or `revalidateTag` for cache consistency.

---
name: typescript-type-system
description: Standards for advanced TypeScript type modeling — strict null checks, discriminated unions, utility types (Pick, Omit, Record, Extract), generic constraints, custom type guards, infer keyword, and avoiding unsafe types (`any`). Use when writing complex TypeScript types, modeling domain entities, or refactoring TypeScript code.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Advanced TypeScript Type System

TypeScript's type system is a compile-time structural tool designed to make illegal states unrepresentable.

## Discriminated Unions for Domain State

Never model state as a single object with optional flags (`isLoading?: boolean, error?: string, data?: T`). Use Discriminated Unions:

```typescript
type AsyncState<T> =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'success'; data: T }
  | { status: 'error'; error: Error };

function renderState(state: AsyncState<UserData>) {
  switch (state.status) {
    case 'loading': return <Spinner />;
    case 'success': return <Profile user={state.data} />; // TypeScript safely narrows state.data
    case 'error': return <ErrorMessage error={state.error} />;
    case 'idle': return null;
  }
}
```

## Type Narrowing & Custom Type Guards

Use explicit predicate type guards (`arg is TargetType`):

```typescript
interface AdminUser { role: 'admin'; permissions: string[] }
interface RegularUser { role: 'user' }
type User = AdminUser | RegularUser;

function isAdmin(user: User): user is AdminUser {
  return (user as AdminUser).role === 'admin';
}
```

## Strict Mode & Avoiding `any`

- **Enable `strict: true`** in `tsconfig.json`.
- **Never use `any`**: Use `unknown` for values of unknown origin, then narrow before accessing properties.
- **Use `satisfies`**: Validate object shapes without widening literal types:
  ```typescript
  const palette = {
    primary: "#0055FF",
    secondary: "#FF5500"
  } satisfies Record<string, string>;
  ```

## Utility Types Reference

- `Pick<T, K>` / `Omit<T, K>` — Extract or exclude object properties.
- `Readonly<T>` — Deeply mark properties as immutable.
- `Record<K, T>` — Map object key types to value types.
- `ReturnType<T>` / `Parameters<T>` — Extract function signature types.

## Checklist

- [ ] `tsconfig.json` has `"strict": true` enabled
- [ ] `any` usage eliminated in favor of `unknown` or generics
- [ ] Complex component/domain states modeled via Discriminated Unions
- [ ] `satisfies` operator used to preserve exact literal types

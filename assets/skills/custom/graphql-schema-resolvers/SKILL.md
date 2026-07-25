---
name: graphql-schema-resolvers
description: Standards for designing GraphQL schemas and implementing performant resolvers — Schema-First vs Code-First design, N+1 query solving with DataLoader, query complexity analysis, mutation response patterns, directives, and schema deprecation strategies. Use when creating GraphQL APIs, writing field resolvers, or optimizing GraphQL performance.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# GraphQL Schema & Resolvers

GraphQL gives clients flexibility to request exact data shapes. Server-side implementation must protect against expensive nested queries and N+1 resolver execution.

## The DataLoader Pattern (Solving N+1)

Without DataLoader, fetching a list of `Posts` and their `Author` triggers $1 + N$ database calls.

**Batching with DataLoader**:
Collects individual author ID fetch requests within a single event-loop tick and executes a single batched query: `SELECT * FROM users WHERE id IN (1, 2, 3...)`.

```typescript
const userLoader = new DataLoader(async (userIds: readonly string[]) => {
  const users = await db.users.findMany({ where: { id: { in: [...userIds] } } });
  const userMap = new Map(users.map(u => [u.id, u]));
  return userIds.map(id => userMap.get(id) || null);
});
```

## Mutation Result Design Pattern

Never return a bare scalar or raw entity from mutations. Always return a Mutation Payload Object containing `userErrors` for field validation failures:

```graphql
type UpdateProfilePayload {
  user: User
  userErrors: [UserError!]!
}

type UserError {
  field: [String!]
  message: String!
}

type Mutation {
  updateProfile(input: UpdateProfileInput!): UpdateProfilePayload!
}
```

## Security & Defense

- **Query Depth Limiting**: Reject queries deeper than $N$ nested levels (e.g. max depth 7).
- **Query Complexity Cost**: Assign point costs to fields and reject queries exceeding maximum points per request.

## Checklist

- [ ] Resolvers for relational child fields use DataLoader to prevent N+1 queries
- [ ] Mutations return payload objects with explicit `userErrors`
- [ ] Max query depth and complexity limits enabled
- [ ] Deprecated schema fields annotated with `@deprecated(reason: "...")`

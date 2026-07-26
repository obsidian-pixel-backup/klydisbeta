---
name: database-schema-design-orm
description: Relational database modeling, normalization (1NF-3NF), indexing strategies, migration scripts, and ORM best practices using Prisma, Entity Framework, and SQLAlchemy.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Database Schema Design & ORM Patterns

Designing efficient database schemas requires balancing relational normalization, indexing strategies, data integrity constraints, and ORM query performance.

## Core Schema Principles

1. **Normalization (3NF)**: Organize tables to reduce redundancy and maintain data integrity.
2. **Indexing Strategy**: Index foreign keys and columns frequently used in `WHERE`, `JOIN`, and `ORDER BY` clauses. Avoid over-indexing (slows down `INSERT`/`UPDATE`).
3. **Database Migrations**: Manage all schema alterations via versioned migration scripts (never edit production schemas manually).
4. **Avoid N+1 Query Problem**: Use ORM eager loading (`include`, `select_related`) to prevent executing $N$ queries for child records inside loops.

---

## Production Prisma Schema Blueprint (`schema.prisma`)

```prisma
datasource db {
  provider = "postgresql"
  url      = env("DATABASE_URL")
}

generator client {
  provider = "prisma-client-js"
}

model User {
  id        String   @id @default(uuid())
  email     String   @unique
  name      String
  posts     Post[]
  createdAt DateTime @default(now())

  @@map("users")
}

model Post {
  id        String   @id @default(uuid())
  title     String
  content   String
  authorId  String
  author    User     @relation(fields: [authorId], references: [id], onDelete: Cascade)
  createdAt DateTime @default(now())

  @@index([authorId])
  @@map("posts")
}
```

---

## N+1 Query Fix Example

```typescript
// BAD: Triggers N+1 SQL queries
const posts = await prisma.post.findMany();
for (const post of posts) {
  const author = await prisma.user.findUnique({ where: { id: post.authorId } });
}

// GOOD: Single query with SQL JOIN
const postsWithAuthor = await prisma.post.findMany({
  include: { author: true }
});
```

---

## Verification Checklist

- [ ] Foreign keys have explicit index annotations (`@@index`).
- [ ] Database changes are committed as migration files in VCS.
- [ ] Database queries are profiled to ensure zero N+1 execution loops exist.
- [ ] Column types match data requirements (e.g., `DECIMAL` for currency, not `FLOAT`).

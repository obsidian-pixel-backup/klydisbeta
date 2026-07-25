---
name: database-schema-design
description: Standards for relational database schema design — normalization (1NF-3NF) and when to deliberately denormalize, primary/foreign key strategy, indexing (composite column order, covering indexes, when an index hurts), naming conventions, data-type choices, and safe zero-downtime migrations via the expand-contract pattern. Use whenever the user designs a schema, writes a migration, adds or tunes an index, models a relationship, debugs a slow query, or asks about normalization/denormalization trade-offs — even for a single "how should I structure this table" question.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Database & Schema Design

Schema decisions are among the most expensive to reverse in a running system — changing a data type or a key strategy after there's production data and dependent code is an order of magnitude harder than changing application code. Get the shape right before there's data in it.

## Normalization

Normal forms exist to eliminate update anomalies — the same fact stored in two places that can drift out of sync.

- **1NF** — each column holds a single, atomic value; no repeating groups or comma-separated lists in a column.
- **2NF** — 1NF, plus every non-key column depends on the *whole* primary key, not just part of a composite key.
- **3NF** — 2NF, plus no non-key column depends on another non-key column (no transitive dependency) — e.g., don't store both `zip_code` and `city` derived from it in the same row if `city` is fully determined by `zip_code` elsewhere.

Design new schemas in 3NF by default. **Deliberately denormalize** only for a measured reason — a read-heavy reporting table, a cache column that avoids an expensive join on a hot path — and when you do, document *why* right next to the column, because it will look like a mistake to the next person otherwise.

## Keys

- **Primary keys**: prefer a surrogate key (auto-increment integer or UUID) over a natural key (email, SSN, order number) as the primary key — natural keys change (a user changes their email) and primary keys shouldn't.
- **Surrogate key choice**:

| Type | Pros | Cons |
|---|---|---|
| Auto-increment integer | Small, fast to index, naturally ordered by creation | Reveals row count/creation order; awkward for multi-region write systems (ID collisions) |
| UUID | Globally unique without coordination; safe for distributed/offline generation | Larger (16 bytes vs 4/8), random UUIDs fragment B-tree indexes and hurt insert locality |

If using UUIDs at scale, prefer a time-ordered variant (e.g., UUIDv7) over fully random UUIDv4 — it keeps new rows clustered at the end of the index instead of scattering inserts randomly across it.
- **Foreign keys**: enforce referential integrity at the database level with actual foreign key constraints, not only in application code — application-level-only enforcement reliably drifts once more than one service writes to the same table.
- Choose `ON DELETE` behavior deliberately (`CASCADE`, `RESTRICT`, `SET NULL`) per relationship — `CASCADE` on the wrong relationship is a common cause of unintended mass data loss.

## Indexing

- Most relational databases default to a **B-tree** index — good for equality and range queries (`=`, `<`, `>`, `BETWEEN`, prefix `LIKE`), sorting, and joins.
- **Composite index column order matters**: put the column used for equality filters first, range filters last — an index on `(status, created_at)` serves `WHERE status = ? ORDER BY created_at` well; the reverse order doesn't.
- **Covering indexes**: an index that includes every column a query needs lets the database answer entirely from the index without touching the table — worth it for hot, narrow, high-frequency queries.
- **Every index has a write cost** — each insert/update/delete must also update every index on that table. Don't add indexes speculatively; add them backed by an actual slow-query finding (via `EXPLAIN`/`EXPLAIN ANALYZE`), and periodically look for unused indexes to drop.
- Index foreign key columns explicitly — many databases (notably Postgres) don't do this automatically, and unindexed foreign keys are a very common source of slow joins and slow cascading deletes.

## Naming conventions

- Pick one convention and apply it everywhere: `snake_case`, plural table names (`orders`, `order_items`), singular for a join/junction table naming the relationship if that reads better (`user_roles`).
- Foreign key columns named after the referenced table, singular, with `_id`: `user_id` referencing `users.id`.
- Boolean columns read as a predicate: `is_active`, `has_verified_email` — not `active` or `status_flag`.
- Avoid reserved words (`order`, `user`, `group`) as bare identifiers where the target database treats them specially; quote or rename to avoid friction.

## Data types

- Use the narrowest correct type — don't store a phone number or ZIP code as an integer (loses leading zeros, implies arithmetic that doesn't make sense), don't store money as a float (binary floating point can't represent decimal currency exactly — use a fixed-point/decimal type or integer minor units).
- Store timestamps as a timezone-aware type (`TIMESTAMPTZ`/`timestamp with time zone`), always in UTC, and convert to local time only at the presentation layer — mixing naive and aware timestamps is a recurring source of off-by-hours bugs.
- Avoid nullable columns where "unset" and "explicitly empty" need to mean different things but the schema can't distinguish them — model that as an explicit state instead of overloading `NULL`.
- Use an enum type or a foreign key to a lookup table for a small fixed set of values, rather than a free-text column that nothing constrains.

## Safe, zero-downtime migrations: expand-contract

For any schema change on a table with production traffic, don't do a single lockstep "change schema and deploy new code" migration — split it into phases so the schema and the application code can each roll forward or back independently:

1. **Expand** — add the new column/table/constraint alongside the old one, backward-compatible with code that doesn't know about it yet.
2. **Migrate** — deploy application code that writes to *both* old and new; backfill existing rows into the new shape in batches (not one giant transaction locking the table).
3. **Switch reads** — deploy code that reads from the new shape, with the old still being kept in sync as a safety net.
4. **Contract** — once fully verified, deploy code that stops writing the old shape, then drop it in a later migration.

This lets you roll back the *application* deploy at any point without also needing a schema rollback — the two are decoupled, which is exactly what makes each step low-risk. Avoid long-locking operations (adding a column with a non-null default on a huge table, rewriting a table in place) during business hours; check whether your database's migration tool supports the online/concurrent variant of the operation (e.g., `CREATE INDEX CONCURRENTLY` in Postgres) before running it directly.

## The N+1 query problem

A very common performance bug: fetching a list of N records, then issuing one additional query per record to fetch related data (N+1 total queries instead of 2).

**Example:**
Input (to avoid): fetch all orders, then loop and query `SELECT * FROM customers WHERE id = ?` once per order.
Output (use instead): a single query with a `JOIN`, or a batched `WHERE customer_id IN (...)` fetch, then assemble the results in application code.

Most ORMs have an explicit eager-loading mechanism for this (`.include()`, `.select_related()`, `.with()` depending on framework) — use it deliberately for any relationship accessed in a loop, rather than relying on lazy loading by default.

## Checklist

- [ ] New tables are in 3NF unless denormalization is deliberate and documented
- [ ] Primary key is a stable surrogate key, not a value that can legitimately change
- [ ] Foreign keys are enforced at the database level with an explicit `ON DELETE` policy
- [ ] Indexes exist for actual slow queries (verified via `EXPLAIN`), including on foreign key columns
- [ ] Composite index column order matches actual query filter/sort patterns
- [ ] Money, dates, and fixed-vocabulary fields use appropriately strict types, not free text
- [ ] Any migration on a live table follows expand-contract, not a single breaking change
- [ ] List-then-related-fetch code paths use a join/batch fetch, not N+1 queries

---
name: api-design
description: Standards for designing REST/HTTP APIs — resource naming, HTTP verb and status-code semantics, versioning, pagination, filtering, idempotency, and machine-readable error responses using RFC 9457 Problem Details. Use whenever the user is designing or reviewing endpoints, naming routes, choosing status codes, structuring request/response payloads or errors, versioning an API, adding pagination/filtering, or writing an OpenAPI spec — even for a single "what should this endpoint look like" question.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# API Design

Good API design optimizes for the *consumer*, not the implementation. Consistency across endpoints matters more than any single endpoint being individually clever — a predictable API lets a client developer guess correctly without reading docs every time.

## Resource modeling

- Model **nouns, not verbs**: `/orders`, not `/getOrders` or `/createOrder`. The HTTP verb already carries the action.
- Use **plural** resource names consistently: `/users`, `/users/{id}`, `/users/{id}/orders`.
- Keep nesting shallow — one level of nesting to express ownership (`/users/{id}/orders`) is usually enough; avoid `/users/{id}/orders/{id}/items/{id}/notes`. Prefer a top-level `/order-items?order_id=...` or a dedicated resource once nesting goes past two levels.
- Model actions that don't map cleanly to CRUD as a sub-resource verb-noun, not a raw verb: `POST /orders/{id}/cancellation`, not `POST /orders/{id}/cancel`. This keeps the door open for the action itself to have state (a cancellation record) later.

## HTTP verbs

| Verb | Use for | Idempotent? | Safe? |
|---|---|---|---|
| `GET` | Retrieve a resource or collection | Yes | Yes |
| `POST` | Create a resource, or trigger a non-idempotent action | No | No |
| `PUT` | Replace a resource entirely | Yes | No |
| `PATCH` | Partially update a resource | No (but often implemented idempotently) | No |
| `DELETE` | Remove a resource | Yes | No |

"Idempotent" means calling it N times has the same effect as calling it once — this is what lets clients safely retry `PUT`/`DELETE` on a network timeout without an extra confirmation step. `POST` is not idempotent by default; see **idempotency keys** below for when you need it to be.

## Status codes

| Code | Meaning | Use when |
|---|---|---|
| `200 OK` | Success | Standard success with a response body |
| `201 Created` | Resource created | Successful `POST`; include a `Location` header |
| `202 Accepted` | Accepted for async processing | Request queued, not yet complete |
| `204 No Content` | Success, no body | Successful `DELETE`, or a `PUT`/`PATCH` with nothing to return |
| `400 Bad Request` | Malformed/invalid request | Failed validation on the request itself |
| `401 Unauthorized` | Not authenticated | Missing or invalid credentials |
| `403 Forbidden` | Authenticated but not permitted | Valid credentials, insufficient permission |
| `404 Not Found` | Resource doesn't exist | Also used to avoid leaking existence in access-control-sensitive cases |
| `409 Conflict` | State conflict | Duplicate creation, version/ETag mismatch, concurrent edit |
| `422 Unprocessable Entity` | Semantically invalid | Well-formed request, but fails business-rule validation |
| `429 Too Many Requests` | Rate limited | Include a `Retry-After` header |
| `500 Internal Server Error` | Unexpected server failure | Never expose stack traces or internals here |
| `503 Service Unavailable` | Temporarily down | Maintenance, overload, dependency outage |

Don't return `200` with an error payload — clients (and monitoring) rely on the status code, not the body, to know if a call succeeded.

## Error responses — RFC 9457 Problem Details

Use a single, consistent, machine-readable error shape across every endpoint instead of inventing a bespoke error format per team or per service. RFC 9457 (which obsoletes RFC 7807) standardizes this as a JSON object served with `Content-Type: application/problem+json`:

```json
{
  "type": "https://api.example.com/errors/insufficient-funds",
  "title": "Insufficient Funds",
  "status": 422,
  "detail": "Account balance ($42.10) is below the requested withdrawal amount ($100.00).",
  "instance": "/accounts/8341/withdrawals/9c2f",
  "balance": 42.10
}
```

- `type` — a URI identifying the *category* of problem (stable, used for programmatic branching by clients); `about:blank` is acceptable if you don't want to host docs per type.
- `title` — a short, human-readable summary, constant for a given `type`.
- `status` — must match the actual HTTP status code of the response.
- `detail` — a human-readable explanation specific to *this* occurrence.
- `instance` — optionally, a URI identifying this specific occurrence (useful for support/log correlation).
- Extension members (like `balance` above) are allowed and encouraged for problem-specific context; clients must ignore extensions they don't recognize, which keeps the format forward-compatible.

For validation errors with multiple field-level failures, use an `errors` extension array with per-field `detail` and a pointer (e.g., JSON Pointer) to the offending field, rather than inventing a separate format for "the 400 case."

## Versioning

| Strategy | Example | Trade-off |
|---|---|---|
| URI path | `/v1/orders` | Most visible/discoverable; easy to route and cache; "pollutes" the URI |
| Header | `Accept: application/vnd.example.v1+json` | Keeps URIs clean; harder to explore/test in a browser |
| Query param | `/orders?version=1` | Simple but easy to omit accidentally; weakest guarantee |

Path versioning at the major-version level (`/v1/`, `/v2/`) is the most common default because it's explicit and impossible to get wrong by accident. Whichever you choose, version only on breaking changes (mirroring SemVer's MAJOR) — additive, backward-compatible fields don't need a new version.

## Pagination

- Prefer **cursor-based** pagination (`?after=<opaque_cursor>&limit=50`) over raw offset (`?offset=100&limit=50`) for any collection that can grow or mutate — offset pagination skips or repeats items when rows are inserted/deleted between requests, and gets slower as the offset grows on large tables.
- Return pagination metadata in a consistent envelope:
```json
{
  "data": [ /* items */ ],
  "pagination": { "next_cursor": "eyJpZCI6NDJ9", "has_more": true }
}
```
- Always cap `limit` server-side regardless of what the client requests.

## Filtering and sorting

- Use query parameters with a predictable convention: `?status=active&sort=-created_at` (a leading `-` for descending).
- Document exactly which fields are filterable/sortable — don't silently allow filtering on every column, which both leaks schema details and can enable expensive unindexed queries.

## Idempotency for non-idempotent verbs

For `POST` requests that must be safely retryable (payments, order creation), accept an `Idempotency-Key` header from the client; store the key with the result of the first successful request, and return that same result on any retry with the same key instead of creating a duplicate.

## Consistency conventions

- Pick one casing convention for JSON keys (`camelCase` or `snake_case`) and apply it everywhere — don't mix.
- Use ISO 8601 for all dates/times (`2026-07-25T14:30:00Z`), always with an explicit timezone (UTC by convention).
- Use consistent field names for the same concept across every resource (`id`, not `userId` in one place and `user_id` in another).
- Document the contract with OpenAPI/Swagger and treat it as the source of truth — generate client SDKs and docs from it rather than hand-maintaining both.

## Checklist

- [ ] Resources are nouns; verbs live in the HTTP method
- [ ] Status codes match actual outcome; no `200` wrapping an error
- [ ] Errors use one consistent, machine-readable shape (RFC 9457) everywhere
- [ ] Breaking changes bump the version; additive changes don't
- [ ] Large/mutable collections use cursor pagination, not raw offset
- [ ] Retryable `POST` endpoints support an idempotency key
- [ ] Casing, date format, and field naming are consistent across every endpoint
- [ ] The contract is documented in OpenAPI, not only in prose

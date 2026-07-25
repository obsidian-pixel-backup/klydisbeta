---
name: api-design-rest-graphql
description: Best practices and design patterns for building robust RESTful and GraphQL APIs — resource naming, HTTP verb idempotency, pagination (cursor vs offset), error formatting (RFC 7807), versioning strategies, rate limiting, authentication headers, and schema evolvability. Use whenever designing API contracts, writing endpoints, choosing between REST and GraphQL, or reviewing API definitions.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# API Design (REST & GraphQL)

APIs are public contracts between systems or client applications. Once published, API changes require deprecation windows and backward compatibility. Designing clear, consistent, and predictable endpoints is critical.

## RESTful Principles

- **Resource Nouns**: Use plural nouns for resources (`/api/v1/users`, `/api/v1/orders`), never verbs (`/getUsers`, `/createOrder`).
- **HTTP Verbs & Idempotency**:
  - `GET`: Read resource. Idempotent & safe. Must not mutate state.
  - `POST`: Create resource or process operation. Neither safe nor idempotent.
  - `PUT`: Replace resource state entirely. Idempotent.
  - `PATCH`: Partially update resource fields. Not strictly required to be idempotent, but strive for it.
  - `DELETE`: Remove resource. Idempotent (subsequent deletes yield 404 or 204).

## HTTP Status Codes

- `200 OK` — Success with response body.
- `201 Created` — Resource created (include `Location` header).
- `204 No Content` — Success with no body (e.g., after `DELETE`).
- `400 Bad Request` — Client payload validation failed.
- `401 Unauthorized` — Unauthenticated (missing/invalid token).
- `403 Forbidden` — Authenticated but lacks permissions for resource.
- `404 Not Found` — Resource does not exist.
- `409 Conflict` — State conflict (e.g., duplicate unique key).
- `422 Unprocessable Entity` — Well-formed JSON but semantically invalid.
- `429 Too Many Requests` — Rate limit exceeded (include `Retry-After`).
- `500 Internal Server Error` — Unhandled server exception.

## Standardized Error Payload (RFC 7807)

```json
{
  "type": "https://api.example.com/errors/invalid-payment",
  "title": "Invalid Payment Method",
  "status": 400,
  "detail": "The card provided has expired.",
  "instance": "/payments/pay_982341",
  "code": "CARD_EXPIRED"
}
```

## Pagination

- **Cursor Pagination** (Preferred for large/real-time datasets):
  `GET /api/v1/posts?limit=20&starting_after=post_123`
  Prevents skipped or duplicate items during concurrent inserts.
- **Offset Pagination** (Acceptable for small static admin lists):
  `GET /api/v1/users?page=2&per_page=50`

## Rate Limiting & Security

- Include rate-limit headers in every response:
  - `X-RateLimit-Limit: 1000`
  - `X-RateLimit-Remaining: 980`
  - `X-RateLimit-Reset: 1672531199`
- Validate and sanitize input payloads against strict JSON schemas before processing.

## Checklist

- [ ] Resource paths use plural nouns and clear hierarchy
- [ ] Correct HTTP status codes used according to spec
- [ ] Errors return standardized JSON (RFC 7807)
- [ ] Large list endpoints implement cursor-based pagination
- [ ] Auth headers use `Authorization: Bearer <token>`

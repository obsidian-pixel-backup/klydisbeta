---
name: agentic-spec-driven-development
description: Writing executable markdown specifications, contract tests, API requirements docs, and using spec-driven development (SDD) for agentic execution.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Spec-Driven Development

Spec-Driven Development (SDD) anchors agent execution to unambiguous, machine-readable specifications prior to writing production code.

## Core Architectural Components

1. **Executable Specifications**: Markdown docs with formal requirement tables, acceptance criteria, and input/output contracts.
2. **Contract-First Testing**: Generating test harnesses directly from spec definitions before writing implementation code.
3. **Spec Alignment Checks**: Automated comparison of implementation AST against specification requirements.

---

## Specification Template Blueprint (`SPEC.md`)

```markdown
# Specification: User Authentication Service

## 1. Overview
High-throughput JWT authentication service with refresh token rotation.

## 2. API Interface Contract

### `POST /api/v2/auth/login`
- **Request Body**:
  | Field | Type | Required | Description |
  | :--- | :--- | :--- | :--- |
  | `email` | String | Yes | Valid user email address |
  | `password` | String | Yes | Plaintext password (min 8 chars) |

- **Response `200 OK`**:
  ```json
  {
    "access_token": "eyJhbGci...",
    "refresh_token": "def456...",
    "expires_in": 3600
  }
  ```

- **Response `401 Unauthorized`**:
  ```json
  {
    "error": "INVALID_CREDENTIALS",
    "message": "Email or password incorrect."
  }
  ```

## 3. Non-Functional Requirements
- Response time $p99 < 50	ext{ms}$.
- Password hashing using Argon2id ($m=65536, t=3, p=4$).
```

---

## Verification Checklist

- [ ] `SPEC.md` defines clear input schema, output schema, and error codes.
- [ ] Automated tests map 1:1 to acceptance criteria in the spec document.
- [ ] Code modifications maintain strict backwards compatibility with published specs.
- [ ] Discrepancies between code and spec trigger explicit review flags.

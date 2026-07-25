---
name: secure-coding
description: Secure-coding checklist grounded in the current OWASP Top 10:2025 — access control, security misconfiguration, software supply chain, cryptography, injection, insecure design, authentication, software/data integrity, logging & alerting, and exceptional-condition handling — plus general defense-in-depth principles (least privilege, input validation, secrets management, dependency scanning). Use whenever the user writes code that handles user input, authentication, authorization, secrets, file uploads, external calls, or dependencies, or explicitly asks for a security review, threat model, or vulnerability check. This is awareness/prevention guidance, not a substitute for a professional security audit or legal compliance sign-off.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Secure Coding

Most real-world breaches trace back to a small, well-documented set of root causes. The OWASP Top 10 is the industry-standard, data-driven consensus on what those are — treat it as a baseline checklist for any code that touches user input, data, or identity, not a security-team-only concern.

## OWASP Top 10:2025 — prevention checklist by category

### A01 — Broken Access Control (incl. SSRF)
Still the most common and most severe category. Now explicitly includes Server-Side Request Forgery and API-specific failures like broken object-level authorization (BOLA) and broken function-level authorization (BFLA).
- Deny by default; grant access explicitly, never infer it from the absence of a check.
- Re-check authorization **server-side** on every request — never trust a client-supplied role, ID, or "isAdmin" flag.
- Validate that the *authenticated* user actually owns/may access the *specific* resource ID in the request (the classic "change the ID in the URL" BOLA bug).
- For any server-initiated outbound request built from user input (webhooks, image-fetch-by-URL, PDF renderers), allow-list destinations and block internal/link-local IP ranges to prevent SSRF.

### A02 — Security Misconfiguration
Jumped from #5 to #2 in 2025 — driven by cloud misconfig and weak defaults as more app behavior lives in configuration rather than code.
- Ship secure defaults; require an explicit, reviewed change to loosen them (CORS, debug mode, verbose errors, default credentials).
- Disable directory listings, stack traces, and admin interfaces in production builds.
- Keep configuration (and especially cloud IAM policies, storage bucket permissions, and security headers) under version control and code review, same as application code.

### A03 — Software Supply Chain Failures
New category for 2025 (expands the old "Vulnerable and Outdated Components"). Has the highest incidence rate of any 2025 category, driven by dependency confusion, compromised build pipelines, and incidents like the Log4Shell and SolarWinds attacks.
- Pin dependency versions with a lockfile; don't float on version ranges in production.
- Run automated dependency vulnerability scanning (SCA) in CI, and patch on a defined SLA — don't let scan results sit unread.
- Verify package integrity (checksums/signatures) and prefer maintaining a Software Bill of Materials (SBOM) for what's actually shipped.
- Treat CI/CD pipeline configuration and build infrastructure as part of your attack surface, not just the application code.

### A04 — Cryptographic Failures
Falls to #4 as TLS adoption and stronger default cipher suites have improved industry-wide, but it remains a top-tier risk.
- Never write your own crypto; use vetted, current libraries and their high-level/"easy mode" APIs.
- Encrypt sensitive data at rest and in transit; enforce TLS, reject weak/legacy protocol and cipher versions.
- Hash passwords with a purpose-built, slow algorithm (Argon2, bcrypt, scrypt) — never a fast general-purpose hash (MD5, SHA-1, plain SHA-256) and never reversible encryption.
- Don't hardcode keys, tokens, or credentials in source; load secrets from a secrets manager or environment configuration, and rotate them.

### A05 — Injection
One of the most tested categories; spans SQL injection (low frequency, high impact) through XSS (high frequency, lower impact per instance) and command/LDAP/template injection.
- Use parameterized queries / prepared statements or an ORM — never string-concatenate user input into a query, shell command, or template.
- Encode output for the context it's rendered in (HTML, attribute, JS, URL) — encoding for the wrong context is itself a common bypass.
- Validate input against an allow-list of expected shape/type/length wherever possible, rather than trying to blocklist "bad" patterns.

### A06 — Insecure Design
Improving industry-wide as threat modeling and secure-design practices spread, but still a core risk class — this category is about missing/flawed *design* controls, not implementation bugs.
- Threat-model features that touch money, identity, or sensitive data before writing code, not after — ask "how would this be abused?" up front.
- Design abuse-case limits in from the start: rate limiting, resource quotas, business-logic limits (can't apply a coupon twice, can't withdraw more than the balance).
- Fail securely: an error, timeout, or exception should never leave the system in a more-permissive state than before it started.

### A07 — Authentication Failures
Renamed from "Identification and Authentication Failures" in 2025; the shift toward standardized auth frameworks has reduced (but not eliminated) occurrences.
- Use a maintained, standard auth framework/protocol (OAuth2/OIDC, a vetted session library) instead of hand-rolling login, sessions, or token handling.
- Enforce credential strength, rate-limit and lock out brute-force attempts, and support/encourage MFA.
- Invalidate sessions on logout, password change, and privilege change; use short-lived tokens with refresh rather than long-lived static ones.

### A08 — Software or Data Integrity Failures
Focused on trust boundaries and verifying integrity of code/data at a lower level than the supply-chain category above (e.g., insecure deserialization, unsigned auto-update mechanisms, CI/CD pipelines that accept unverified input).
- Verify digital signatures on updates, plugins, and CI artifacts before executing or deploying them.
- Avoid deserializing untrusted data with formats that can execute code as a side effect (e.g., unsafe pickle/YAML loaders); use safe-load variants or plain data formats.
- Restrict CI/CD pipeline permissions to what a given job actually needs; don't let a PR from an untrusted fork run with full secrets access.

### A09 — Security Logging & Alerting Failures
Renamed in 2025 to emphasize that logging without alerting doesn't actually help anyone respond to an incident.
- Log authentication events, access-control failures, and input-validation failures with enough context to investigate (who, what, when, from where).
- Never log secrets, passwords, tokens, or full payment/PII data — mask or omit them at the point of logging.
- Wire security-relevant logs to actual alerting, not just a dashboard nobody watches; a log with no alert on it prevents nothing.

### A10 — Mishandling of Exceptional Conditions
New category for 2025, covering improper error handling, "fail open" logic, and unhandled edge cases that create security gaps rather than just crashes.
- Fail closed by default: on an unexpected error or exception in a security check, deny the action rather than allowing it through.
- Handle every reachable error path explicitly — don't let broad `catch`/`except` blocks silently swallow exceptions that should have blocked the operation.
- Return generic error messages to clients; keep detailed stack traces and internals in server-side logs only.

## General defense-in-depth principles

- **Least privilege** — every service account, API key, and database user gets only the permissions it needs, nothing broader "to be safe later."
- **Defense in depth** — don't rely on a single control (e.g., only client-side validation, or only a firewall); layer independent checks so one failure doesn't mean full compromise.
- **Input validation + output encoding, both** — validation alone doesn't protect against injection at the *output* boundary; you need both.
- **Secrets management** — no credentials in source, container images, or client-side code; use a secrets manager, env injection, or KMS-backed values, and rotate on a schedule and immediately on suspected exposure.
- **Keep dependencies current** — an unpatched known CVE in a library is the same risk as a bug you wrote yourself.

## Checklist

- [ ] Every access-control check happens server-side, per-request, on the specific resource
- [ ] No secrets, keys, or credentials are hardcoded or committed
- [ ] All queries/commands/templates use parameterization, not string concatenation
- [ ] Passwords hashed with a slow, purpose-built algorithm; sensitive data encrypted at rest and in transit
- [ ] Dependencies are pinned and scanned for known vulnerabilities in CI
- [ ] Errors fail closed and return generic messages to clients; details go to server logs only
- [ ] Security-relevant events are logged (without sensitive data) and actually wired to alerting

This skill covers common-cause prevention; it is not a substitute for a threat model reviewed by a security professional, a penetration test, or legal/regulatory compliance advice for your specific domain (PCI-DSS, HIPAA, GDPR, etc.).

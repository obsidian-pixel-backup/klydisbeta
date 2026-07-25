---
name: security-owasp-top-10
description: Practical web application security guidelines and threat mitigations based on the OWASP Top 10 — preventing SQL injection, Cross-Site Scripting (XSS), Cross-Site Request Forgery (CSRF), Broken Access Control, insecure deserialization, SSRF, and sensitive data exposure. Use when auditing code for security, writing authentication/authorization features, or securing web applications.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Web Security & OWASP Top 10

Security is not a feature added at the end; it must be built into code at every layer.

## Top Vulnerabilities & Defensive Rules

### 1. Broken Access Control (OWASP #1)
- **Problem**: Users access resources outside their intended permissions.
- **Rule**: Deny by default. Enforce authorization checks at the domain service layer on EVERY request, matching `user_id` against resource owner (`WHERE id = :id AND owner_id = :current_user_id`).
- **Never trust client-supplied IDs** in URLs/payloads without ownership verification.

### 2. Injection (SQL, Command, LDAP) (OWASP #3)
- **Problem**: Untrusted input executed as commands or queries.
- **Rule**: ALWAYS use parameterized queries or ORM query builders. Never concatenate strings into SQL queries.
  ```csharp
  // BAD: string sql = "SELECT * FROM Users WHERE Name = '" + userInput + "'";
  // GOOD: string sql = "SELECT * FROM Users WHERE Name = @name";
  ```

### 3. Cross-Site Scripting (XSS)
- **Problem**: Injecting malicious HTML/JS into rendered web pages.
- **Rule**: Contextually encode all dynamic output. Set HTTP security headers (`Content-Security-Policy`, `X-Content-Type-Options: nosniff`). Use HTTP-only, Secure, SameSite cookies.

### 4. Server-Side Request Forgery (SSRF)
- **Problem**: Server fetches user-supplied URLs, making unauthorized internal network requests.
- **Rule**: Restrict outbound requests to an explicit whitelist of domains/IPs. Block requests targeting private IP ranges (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `127.0.0.1`, `metadata.google.internal`).

### 5. Sensitive Data Exposure & Cryptography
- **Rule**: Hash passwords using memory-hard functions (`argon2id`, `bcrypt`, `scrypt`) — NEVER store plain MD5 or SHA256 hashes. Encrypt data in transit (TLS 1.3) and at rest (AES-256-GCM).

## Security Verification Checklist

- [ ] All database queries parameterized
- [ ] User permissions checked server-side for every endpoint
- [ ] Secrets stored in environment variables, never committed to git
- [ ] Passwords hashed with Argon2id or bcrypt
- [ ] Security headers (`CSP`, `HSTS`, `X-Frame-Options`) configured
- [ ] CORS policies explicitly specify allowed origins (no `Access-Control-Allow-Origin: *` with credentials)

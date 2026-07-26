---
name: web-security-auth-session-management
description: Implementing secure authentication & session management: OAuth2/OIDC, JWT vs HTTP-only session cookies, CSRF protection, CORS configuration, and RBAC.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Web Security: Auth & Session Management

Authentication and session management control identity verification and access permissions across frontend and backend web applications.

## Session Storage Comparison

- **HTTP-Only SameSite Cookies** (Recommended): Immune to XSS script theft. Best for browser web applications.
- **JWT in Memory**: Safe from CSRF, but wiped on page refresh.
- **LocalStorage JWT** (DANGEROUS): Highly vulnerable to XSS token theft. Avoid storing credentials here.

---

## Secure Cookie & CORS Headers Setup (Express Node.js)

```typescript
import express from 'express';
import cors from 'cors';
import cookieParser from 'cookie-parser';

const app = express();

// Secure CORS config
app.use(cors({
  origin: 'https://app.example.com',
  credentials: true
}));

app.use(cookieParser());

// Setting HTTP-Only Auth Cookie
app.post('/api/login', (req, res) => {
  const token = generateAuthToken(user);

  res.cookie('auth_token', token, {
    httpOnly: true, // Prevents JS script access
    secure: process.env.NODE_ENV === 'production', // Requires HTTPS
    sameSite: 'lax', // CSRF protection
    maxAge: 7 * 86400 * 1000 // 7 Days
  });

  res.json({ success: true });
});
```

---

## Role-Based Access Control (RBAC) Guard Blueprint

```typescript
export function requireRole(allowedRoles: string[]) {
  return (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
    if (!req.user || !allowedRoles.includes(req.user.role)) {
      return res.status(403).json({ error: 'FORBIDDEN_INSUFFICIENT_PERMISSIONS' });
    }
    next();
  };
}
```

---

## Verification Checklist

- [ ] Auth tokens are stored in `httpOnly`, `Secure`, `SameSite=Lax` cookies.
- [ ] CORS origins explicitly whitelist trusted domains (never use wildcard `*` with credentials).
- [ ] Password hashes use Argon2id or bcrypt ($cost \ge 12$).
- [ ] Protected API endpoints validate authorization claims server-side on every request.

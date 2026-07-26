---
name: api-gateway-microservices-routing
description: Architecting API Gateways: reverse proxy routing, rate limiting, request transformation, authentication offloading, and circuit breaking.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# API Gateway & Microservices Routing

An API Gateway acts as the single reverse-proxy entry point for client applications, handling routing, authorization offloading, rate limiting, and request transformation.

## Gateway Architecture

```
┌──────────────┐           ┌──────────────────┐           ┌─────────────────┐
│ Web / Mobile │──────────>│   API Gateway    │──────────>│ User Service    │
│ Clients      │           │ (Kong/Nginx/Envoy)│           ├─────────────────┤
└──────────────┘           └──────────────────┘──────────>│ Order Service   │
                                                          └─────────────────┘
```

---

## Core Gateway Responsibilities

1. **Request Routing**: Proxying paths (`/api/v1/users` $\rightarrow$ `user-service:8080`).
2. **Auth Termination**: Validating JWT tokens at the gateway so internal microservices receive trusted user headers.
3. **Rate Limiting**: Throttling requests by IP or user ID (e.g., 100 req/min).

---

## Express Gateway Proxy Pattern Blueprint

```typescript
import express from 'express';
import { createProxyMiddleware } from 'http-proxy-middleware';

const app = express();

// Validate Auth Header before proxying
app.use('/api/v1/orders', verifyJWT, createProxyMiddleware({
  target: 'http://order-service:8081',
  changeOrigin: true,
  pathRewrite: { '^/api/v1/orders': '/orders' }
}));

app.use('/api/v1/users', createProxyMiddleware({
  target: 'http://user-service:8082',
  changeOrigin: true
}));
```

---

## Verification Checklist

- [ ] API Gateway enforces rate limiting to prevent denial-of-service abuse.
- [ ] Microservices reside in private internal networks reachable only via the gateway.
- [ ] Gateway logs append correlation IDs to every proxied request header.
- [ ] Health check probes (`/healthz`) return gateway status continuously.

---
name: error-handling-resilience
description: Patterns for handling production errors and building resilient software — structured logging (JSON, correlation IDs), exception hierarchy design, Circuit Breaker pattern, Retry with exponential backoff & jitter, Fallbacks, and OpenTelemetry instrumentation. Use when designing error handling mechanisms, logging pipelines, or resilience policies.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Error Handling & System Resilience

Distributed systems fail. Resilient applications anticipate external service outages, network timeouts, and transient database errors without cascading failures.

## Resilience Patterns

### 1. Circuit Breaker
Prevents application from bombarding a failing downstream service with requests:

- **Closed**: Normal operations. Requests pass through.
- **Open**: Consecutive failure threshold exceeded. Immediately fails incoming calls without making network attempts.
- **Half-Open**: Periodically allows trial requests to check if downstream service recovered.

### 2. Retry with Exponential Backoff + Jitter
Never retry immediately in a tight loop. Add exponential delays with randomized jitter to prevent thundering herd problems:

$$\text{delay} = \min(\text{max\_delay}, \text{base\_delay} \times 2^{\text{attempt}}) + \text{random\_jitter}$$

Only retry transient error codes (`502`, `503`, `504`, network timeouts) — NEVER retry client errors (`400`, `401`, `403`, `404`).

### 3. Structured Logging
Log structured JSON objects with mandatory correlation IDs (`CorrelationId`, `TraceId`):

```json
{
  "timestamp": "2026-07-25T13:40:00Z",
  "level": "ERROR",
  "message": "Failed to process payment for order",
  "correlationId": "c9812a34-1102-4411-8812",
  "orderId": "ORD-9912",
  "exception": "PaymentGatewayTimeoutException: Gateway timeout after 5000ms"
}
```

## Exception Handling Principles

- **Catch Specific Exceptions**: Avoid broad `catch (Exception ex)` blocks that swallow unexpected bugs.
- **Fail Gracefully**: Provide meaningful user fallback UI or degraded responses rather than unhandled crash pages.
- **Never Log Secrets**: Sanitize logs to prevent capturing passwords, auth tokens, or PII.

## Checklist

- [ ] Correlation / Trace IDs propagated across all HTTP requests and log entries
- [ ] Retries use exponential backoff with randomized jitter
- [ ] Circuit Breakers wrap third-party API dependencies
- [ ] Logs emitted in structured JSON format
- [ ] Sensitive fields (passwords, tokens, SSNs) masked in logs

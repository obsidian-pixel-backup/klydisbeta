---
name: logging-observability-telemetry
description: Implementing structured logging, OpenTelemetry tracing, metrics instrumentation, correlation IDs, and centralized observability dashboards.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Logging, Observability & Telemetry

Modern cloud applications require comprehensive observability: structured JSON logs, distributed request tracing, and real-time metric counters.

## The 3 Pillars of Observability

1. **Logs**: Structured JSON events recording specific execution points with context.
2. **Metrics**: Aggregated numerical measurements (RUST metrics: Requests, Errors, Duration, Utilization).
3. **Traces**: End-to-end request journeys across microservice boundaries via trace/span IDs.

---

## Structured JSON Logging Blueprint (Winston / Pino)

```typescript
import pino from 'pino';

export const logger = pino({
  level: process.env.LOG_LEVEL || 'info',
  formatters: {
    level: (label) => ({ level: label.toUpperCase() })
  },
  timestamp: pino.stdTimeFunctions.isoTime
});

// Usage with correlation ID context
logger.info({
  correlationId: 'req-9481-abc',
  userId: 'usr_123',
  event: 'USER_LOGIN_SUCCESS',
  durationMs: 42
}, 'User logged in successfully');
```

---

## Distributed Tracing Header Standard (W3C Trace Context)

Propagate `traceparent` headers across HTTP requests:
```text
traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
              │  │                                │                │
            Ver  Trace ID                          Parent Span ID   Flags
```

---

## Verification Checklist

- [ ] All application logs output structured JSON format (never raw plain text strings).
- [ ] Requests propagate correlation/trace IDs across downstream API calls.
- [ ] Sensitive passwords, tokens, and PII are scrubbed from log outputs.
- [ ] Metrics track API response latency ($p50, p95, p99$) and error rates.

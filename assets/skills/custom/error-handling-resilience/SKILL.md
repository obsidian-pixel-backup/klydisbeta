---
name: error-handling-resilience
description: Designing resilient systems: custom error hierarchies, exponential backoff retries, circuit breaker patterns, dead letter queues, and graceful degradation.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Error Handling & Resilience Patterns

Resilient applications anticipate failures in network calls, databases, and third-party APIs by implementing proactive error handling, retries, and circuit breakers.

## Core Resilience Patterns

1. **Custom Error Taxonomy**: Categorize errors into operational errors (expected network timeouts) vs programmer bugs (null pointer, syntax error).
2. **Exponential Backoff with Jitter**: Avoid hammering failing downstream services by introducing randomized backoff intervals.
3. **Circuit Breaker**: Trip execution to fast-fail when downstream error rates exceed a safety threshold.

---

## Implementation Blueprints

### Exponential Backoff with Jitter (TypeScript)
```typescript
async function retryWithBackoff<T>(
  fn: () => Promise<T>,
  retries: number = 3,
  delayMs: number = 500
): Promise<T> {
  try {
    return await fn();
  } catch (error) {
    if (retries <= 0) throw error;
    // Calculate exponential delay with randomized jitter
    const jitter = Math.random() * 200;
    const nextDelay = delayMs * 2 + jitter;
    console.warn(`Operation failed. Retrying in ${Math.round(nextDelay)}ms...`);
    await new Promise((res) => setTimeout(res, nextDelay));
    return retryWithBackoff(fn, retries - 1, nextDelay);
  }
}
```

### Circuit Breaker Pattern Blueprint
```typescript
class CircuitBreaker {
  private failures = 0;
  private state: 'CLOSED' | 'OPEN' | 'HALF_OPEN' = 'CLOSED';
  private nextAttempt = Date.now();

  constructor(private threshold = 5, private resetTimeoutMs = 10000) {}

  async execute<T>(action: () => Promise<T>): Promise<T> {
    if (this.state === 'OPEN') {
      if (Date.now() > this.nextAttempt) {
        this.state = 'HALF_OPEN';
      } else {
        throw new Error("CircuitBreaker is OPEN. Fast failing request.");
      }
    }

    try {
      const result = await action();
      this.reset();
      return result;
    } catch (err) {
      this.recordFailure();
      throw err;
    }
  }

  private recordFailure() {
    this.failures++;
    if (this.failures >= this.threshold) {
      this.state = 'OPEN';
      this.nextAttempt = Date.now() + this.resetTimeoutMs;
    }
  }

  private reset() {
    this.failures = 0;
    this.state = 'CLOSED';
  }
}
```

---

## Verification Checklist

- [ ] Errors return standardized structured diagnostic JSON containing error codes.
- [ ] Network retries use jitter to prevent synchronized retry storms.
- [ ] Critical operations have fallback paths (graceful degradation).
- [ ] Uncaught exceptions trigger alerts in monitoring / telemetry systems.

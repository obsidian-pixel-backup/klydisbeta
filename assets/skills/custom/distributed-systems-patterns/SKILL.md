---
name: distributed-systems-patterns
description: Architecting distributed microservices: Saga pattern, CQRS, Event Sourcing, eventual consistency, idempotency keys, and distributed locks.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Distributed Systems Patterns

Building reliable microservice architectures requires managing eventual consistency, network partition tolerance, distributed transactions, and messaging patterns.

## Core Patterns Summary

- **Saga Pattern**: Manage distributed transactions across microservices via a sequence of local transactions and compensating rollbacks.
- **CQRS (Command Query Responsibility Segregation)**: Separate read models (optimized for UI queries) from write models (optimized for domain validation).
- **Idempotency Keys**: Ensure API requests executed multiple times (due to retries) yield identical backend state.

---

## Saga Pattern (Choreography Sequence)

```
┌───────────────┐   OrderCreated    ┌───────────────┐   PaymentProcessed   ┌───────────────┐
│ Order Service │──────────────────>│Payment Service│────────────────────>│Inventory Svc  │
└───────────────┘                   └───────────────┘                      └───────────────┘
        ▲                                   │ Payment Failed                       │
        └───────────────────────────────────┴──────────────────────────────────────┘
                          Compensating CancelOrder Event
```

---

## Idempotence Guarantee Implementation Blueprint

```typescript
async function processPaymentWithIdempotency(
  idempotencyKey: string,
  paymentDetails: PaymentInput
) {
  // Check if request key was already processed in Redis
  const cachedResponse = await redis.get(`idempotency:${idempotencyKey}`);
  if (cachedResponse) {
    return JSON.parse(cachedResponse);
  }

  // Execute payment transaction
  const result = await paymentGateway.charge(paymentDetails);

  // Cache response for 24 hours
  await redis.set(`idempotency:${idempotencyKey}`, JSON.stringify(result), 'EX', 86400);
  return result;
}
```

---

## Verification Checklist

- [ ] Microservices handle transient network partitions gracefully.
- [ ] Write APIs accept `Idempotency-Key` headers for safe retry support.
- [ ] Message consumers handle duplicate events idempotently.
- [ ] Distributed sagas define compensating actions for every step.

---
name: microservices-event-driven
description: Architecture standards for event-driven systems and microservices — event publishing patterns (Transactional Outbox), message broker integration (Kafka/RabbitMQ/NATS), CQRS (Command Query Responsibility Segregation), Sagas, idempotent consumers, and distributed tracing. Use when designing distributed systems, asynchronous event handlers, or decoupled microservice architectures.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Microservices & Event-Driven Architecture

Decoupled event-driven systems trade immediate consistency for scalability and fault tolerance. Systems must handle network partitions, out-of-order processing, and duplicate message delivery gracefully.

## The Transactional Outbox Pattern

To avoid dual-write failures (e.g. database transaction succeeds but event bus publish fails), save events to an `Outbox` table within the same database transaction, then process them asynchronously:

```
[ Domain Operation ] ──(DB Tx)──► [ Target Table ] + [ Outbox Table ]
                                                            │
                                                     (Poller / CDC)
                                                            ▼
                                                     [ Event Broker ]
```

## Idempotent Message Processing

Message brokers deliver messages **at-least-once**. Consumers MUST be idempotent:

- Track processed message IDs in a `processed_messages` deduplication table.
- Use unique idempotency keys supplied by the producer (`order_id_step_checkout`).

## Saga Pattern for Distributed Transactions

Instead of 2PC (Two-Phase Commit), orchestrate multi-service workflows using Sagas with compensating transactions:

1. Service A: `ReserveInventory()` -> Success
2. Service B: `ChargePayment()` -> Failed!
3. Compensating Action: Execute `ReleaseInventory()` for Service A.

## Event Schema Evolution

- Prefer Protocol Buffers (protobuf) or Apache Avro with schema registries over unconstrained JSON.
- Never remove required fields or change field IDs in published event contracts; add optional fields for backward compatibility.

## Checklist

- [ ] Dual-writes eliminated using Transactional Outbox or CDC
- [ ] Message consumers handle duplicate events idempotently
- [ ] Event schemas backward-compatible
- [ ] Distributed tracing (`traceparent` header / OpenTelemetry) propagated across messages
- [ ] Dead-Letter Queues (DLQ) configured with alerting for unprocessable messages

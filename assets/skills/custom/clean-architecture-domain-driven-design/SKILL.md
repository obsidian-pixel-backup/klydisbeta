---
name: clean-architecture-domain-driven-design
description: Architecting software using Clean / Hexagonal Architecture and Domain-Driven Design (DDD): Entities, Aggregates, Value Objects, Repositories, and Use Cases.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Clean Architecture & Domain-Driven Design (DDD)

Clean Architecture and Domain-Driven Design keep core business logic decoupled from frameworks, databases, UI layers, and external third-party services.

## The Clean Architecture Dependency Rule

```
┌─────────────────────────────────────────────────────────────┐
│                 Frameworks & Drivers (DB, Web, UI)          │
│   ┌─────────────────────────────────────────────────────┐   │
│   │           Interface Adapters (Controllers, Presenters)│   │
│   │   ┌─────────────────────────────────────────────┐   │   │
│   │   │         Application Business Rules          │   │   │
│   │   │         (Use Cases / Interactors)           │   │   │
│   │   │   ┌─────────────────────────────────────┐   │   │   │
│   │   │   │   Enterprise Business Rules         │   │   │   │   │
│   │   │   │   (Entities & Value Objects)        │   │   │   │   │
│   │   │   └─────────────────────────────────────┘   │   │   │
│   │   └─────────────────────────────────────────────┘   │   │
│   └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

> **Dependency Rule**: Source code dependencies MUST point inward ONLY. Core business entities must never depend on database drivers, ORMs, or web framework HTTP request objects.

---

## Key DDD Building Blocks

- **Entity**: An object with a distinct identity that persists over time (e.g., `User` with `UserId`).
- **Value Object**: Immutable object defined purely by its attributes (e.g., `Money({ amount: 100, currency: 'USD' })`).
- **Aggregate Root**: A cluster of domain objects that can be treated as a single unit for data changes (e.g., `Order` aggregate containing `OrderItem`s).
- **Repository Interface**: An abstraction defining data persistence methods implemented in the infrastructure layer.

---

## Implementation Template (TypeScript DDD Aggregate & Use Case)

```typescript
// Core Domain: Value Object
export class Money {
  constructor(readonly amount: number, readonly currency: string) {
    if (amount < 0) throw new Error("Money cannot be negative");
  }
}

// Core Domain: Aggregate Root
export class Order {
  private constructor(
    readonly id: string,
    private items: Array<{ productId: string; price: Money }>,
    private status: 'PENDING' | 'PAID'
  ) {}

  static create(id: string): Order {
    return new Order(id, [], 'PENDING');
  }

  addItem(productId: string, price: Money): void {
    if (this.status !== 'PENDING') throw new Error("Cannot modify paid order");
    this.items.push({ productId, price });
  }
}

// Application Layer: Port (Interface)
export interface OrderRepository {
  save(order: Order): Promise<void>;
  findById(id: string): Promise<Order | null>;
}
```

---

## Verification Checklist

- [ ] Core domain entities contain zero framework imports (`express`, `typeorm`, `prisma`).
- [ ] Database access occurs exclusively via repository interfaces.
- [ ] Domain logic is fully unit-tested without needing mock web servers or live databases.
- [ ] Use cases orchestrate domain entities without exposing persistence details to the caller.

---
name: clean-architecture-patterns
description: Principles for enterprise software architecture — Domain-Driven Design (DDD), Hexagonal Architecture (Ports and Adapters), Onion Architecture, SOLID principles, boundary enforcement, and dependency inversion. Use when designing application layers, refactoring monolithic codebases, creating domain models, or establishing project directory structures.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Clean & Hexagonal Architecture

Clean Architecture separates business rules from frameworks, databases, and external UI deliver mechanisms. The core domain model must have zero dependencies on infrastructure implementations.

## The Core Rule: Dependency Direction

Dependencies MUST point **inward** toward the core domain rules:

```
[ External Interfaces / UI / DB / Web Frameworks ]
                |
                v
    [ Application Use Cases / Services ]
                |
                v
       [ Core Domain Entities ]
```

- **Domain Entities**: Contain pure business logic and value objects. Dependencies: None.
- **Use Cases / Application Layer**: Coordinates domain objects to perform business tasks. Defines interfaces (Ports). Dependencies: Domain Entities.
- **Infrastructure / Adapters**: Implements database persistence, HTTP clients, UI components, messaging queues (Adapters). Dependencies: Application Ports.

## Dependency Inversion (Ports & Adapters)

- **Port (Interface in Application Layer)**:
  ```csharp
  public interface IUserRepository
  {
      Task<User?> GetByIdAsync(UserId id, CancellationToken ct);
      Task SaveAsync(User user, CancellationToken ct);
  }
  ```
- **Adapter (Implementation in Infrastructure Layer)**:
  ```csharp
  public class SqlUserRepository : IUserRepository
  {
      private readonly DbContext _db;
      public SqlUserRepository(DbContext db) => _db = db;
      // Database specific persistence implementation
  }
  ```

## SOLID Principles Quick Guide

- **Single Responsibility (SRP)**: A class should have one, and only one, reason to change.
- **Open/Closed (OCP)**: Software entities should be open for extension, but closed for modification.
- **Liskov Substitution (LSP)**: Subtypes must be substitutable for their base types without altering correctness.
- **Interface Segregation (ISP)**: Clients should not be forced to depend upon interfaces they do not use.
- **Dependency Inversion (DIP)**: High-level modules should not depend on low-level modules; both should depend on abstractions.

## Boundary Enforcement Checklist

- [ ] Domain objects do not reference ORM annotations, database libraries, or HTTP models.
- [ ] Business logic relies on interfaces, not concrete database or third-party implementations.
- [ ] Infrastructure dependencies are wired via Dependency Injection at the composition root.
- [ ] Domain models use explicit Value Objects for validated primitives (e.g. `Email`, `Money`, `UserId`).

---
name: clean-code-refactoring-patterns
description: Identifying code smells, executing safe refactoring transformations, extracting functions/classes, simplifying conditional logic, and applying SOLID principles.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Clean Code & Refactoring Patterns

Writing clean, maintainable code requires continuous refactoring to reduce debt, improve readability, and adhere to SOLID object-oriented and functional principles.

## Core Architectural Principles

1. **Single Responsibility Principle (SRP)**: A module or class should have one, and only one, reason to change.
2. **KISS & DRY**: Keep It Simple, Stupid & Don't Repeat Yourself.
3. **Boy Scout Rule**: Always leave the code cleaner than you found it.
4. **Composition over Inheritance**: Prefer small, composable functions and interfaces over deep inheritance trees.

---

## Catalog of Common Refactorings

### 1. Extract Function / Method
When a function spans over 30-40 lines or handles multiple levels of abstraction, extract logical blocks into named helper functions.

```typescript
// BEFORE: Mixed concerns inside order processing
function processOrder(order: Order) {
  // Validate order
  if (!order.items || order.items.length === 0) throw new Error("Empty items");
  if (!order.userEmail) throw new Error("Missing email");

  // Calculate total
  let total = 0;
  for (const item of order.items) total += item.price * item.quantity;
  if (total > 100) total *= 0.9; // 10% discount

  // Send receipt
  smtpClient.send(order.userEmail, `Total: ${total}`);
}

// AFTER: Extracted single-purpose functions
function processOrder(order: Order) {
  validateOrder(order);
  const total = calculateOrderTotal(order.items);
  sendOrderReceipt(order.userEmail, total);
}
```

### 2. Replace Guard Conditionals with Early Return
Nested `if-else` blocks increase cognitive complexity. Flatten execution paths using early guard returns.

```typescript
// BEFORE: Deep nesting
function getDiscount(user: User): number {
  if (user.isActive) {
    if (user.isVIP) {
      if (user.yearsRegistered > 5) {
        return 0.3;
      } else {
        return 0.2;
      }
    } else {
      return 0.1;
    }
  } else {
    return 0;
  }
}

// AFTER: Guard clauses
function getDiscount(user: User): number {
  if (!user.isActive) return 0;
  if (!user.isVIP) return 0.1;
  return user.yearsRegistered > 5 ? 0.3 : 0.2;
}
```

---

## Refactoring Safety Checklist

- [ ] Ensure comprehensive unit tests pass before making any structural refactoring edits.
- [ ] Perform refactorings in small, incremental commits (never mix feature logic with refactoring).
- [ ] Rename variables to convey intent rather than type (e.g., `userList` -> `activeCustomers`).
- [ ] Re-run test suite after every step to verify behavior preservation.

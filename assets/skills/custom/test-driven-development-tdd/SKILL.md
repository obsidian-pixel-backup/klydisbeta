---
name: test-driven-development-tdd
description: Practicing Test-Driven Development (TDD): Red-Green-Refactoring cycle, specification by example, and writing highly testable software.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Test-Driven Development (TDD)

Test-Driven Development (TDD) flips traditional development by writing failing tests before writing any production code, driving modular design and zero-bug architecture.

## The TDD Cycle

```
┌─────────────────┐
│  1. RED         │  Write a small, failing unit test for expected behavior.
└─────────────────┘
         │
         ▼
┌─────────────────┐
│  2. GREEN       │  Write the absolute minimal code required to pass the test.
└─────────────────┘
         │
         ▼
┌─────────────────┐
│  3. REFACTOR    │  Clean up code, remove duplication, keep tests GREEN.
└─────────────────┘
```

---

## Step-by-Step TDD Example: Calculator String Parser

### Step 1: RED (Failing Test)
```typescript
test('add("") should return 0', () => {
  expect(add("")).toBe(0); // Fails because add function does not exist
});
```

### Step 2: GREEN (Minimal Implementation)
```typescript
export function add(numbers: string): number {
  if (numbers === "") return 0;
  return -1;
}
```

### Step 3: Write Next RED Test
```typescript
test('add("1,2") should return 3', () => {
  expect(add("1,2")).toBe(3);
});
```

### Step 4: GREEN & REFACTOR
```typescript
export function add(numbers: string): number {
  if (!numbers) return 0;
  return numbers.split(',').reduce((sum, n) => sum + parseInt(n, 10), 0);
}
```

---

## Verification Checklist

- [ ] Verify test fails for the expected reason before writing implementation code.
- [ ] Write only enough production code to pass the current failing test.
- [ ] Refactor with complete confidence while tests remain passing green.
- [ ] Never skip the RED phase.

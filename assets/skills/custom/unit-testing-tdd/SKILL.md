---
name: unit-testing-tdd
description: Standards for writing maintainable unit tests and practicing Test-Driven Development (TDD) — AAA (Arrange-Act-Assert) structure, mocking vs stubbing vs fakes, testing behavior over implementation details, FIRST principles, mutation testing concepts, and boundary value testing. Use when writing tests, designing test suites, setting up mocks, or practicing TDD.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Unit Testing & TDD

Tests are executable documentation and safety nets for refactoring. Good tests make code easier to change; bad tests block refactoring by binding tightly to internal implementation details.

## The AAA Pattern (Arrange, Act, Assert)

Structure every unit test clearly into three distinct blocks:

```csharp
[Fact]
public async Task ProcessOrder_WhenStockIsAvailable_DecrementsStockAndCreatesInvoice()
{
    // Arrange
    var stockService = new Mock<IStockService>();
    stockService.Setup(s => s.HasStock("SKU-123", 2)).ReturnsAsync(true);
    var handler = new OrderProcessor(stockService.Object);

    // Act
    var result = await handler.ProcessAsync(new OrderCommand("SKU-123", 2));

    // Assert
    Assert.True(result.IsSuccess);
    stockService.Verify(s => s.ReserveStock("SKU-123", 2), Times.Once);
}
```

## FIRST Principles for Unit Tests

- **Fast**: Executed in milliseconds.
- **Independent**: Tests must run in any order without shared state.
- **Repeatable**: Deterministic results in any environment (local, CI, offline).
- **Self-Validating**: Clear pass/fail outcome without manual inspection.
- **Timely**: Written alongside or before the production code.

## Test Doubles Definitions

| Double Type | Purpose | When to Use |
|---|---|---|
| **Dummy** | Passed as placeholder argument; never called | Fulfilling method signatures |
| **Stub** | Returns canned answers to queries | Providing test data inputs |
| **Fake** | Working lightweight implementation (e.g. In-Memory DB) | Complex integration testing |
| **Mock** | Expects specific calls and verifies calls occurred | Testing outbound side-effects |

## Testing Behavior vs Implementation

- **DO**: Test public API boundaries, return values, and domain side effects.
- **DON'T**: Assert private variable states or exact step-by-step internal execution order unless side-effect ordering is a hard requirement.

## TDD Cycle (Red -> Green -> Refactor)

1. **Red**: Write a failing test for the next smallest behavior.
2. **Green**: Write the minimal production code needed to pass the test.
3. **Refactor**: Clean up the code while keeping all tests green.

## Checklist

- [ ] Each test asserts one logical concept
- [ ] Tests use the AAA pattern separated by blank lines
- [ ] Test names describe Scenario, Condition, and Expected Result
- [ ] No external network, disk, or real DB calls in pure unit tests

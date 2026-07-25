---
name: testing-strategy
description: Framework for designing and writing an automated test suite — the test pyramid, TDD's red-green-refactor cycle, the FIRST principles, Arrange-Act-Assert structure, test-double taxonomy (dummy/stub/spy/mock/fake), coverage as a signal (not a target), and flaky-test triage. Use this whenever the user writes unit/integration/e2e tests, does TDD, decides what a test suite should cover, debugs a flaky or slow test suite, sets coverage targets, or defines a team's testing standards — trigger even on a simple "write tests for this function" request.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Testing Strategy

Tests exist to let people change code with confidence. A suite that's slow, flaky, or tightly coupled to implementation details actively works against that goal — it trains people to ignore failures.

## The test pyramid

Shape your test suite like a pyramid, not an hourglass or a diamond:

```
        /\
       /e2e\        few — slow, brittle, expensive, but catch real
      /------\       integration failures across the whole system
     /  integ  \     some — verify components work together
    /------------\   (DB, API boundary, message queue)
   /   unit tests  \  many — fast, isolated, pinpoint failures precisely
  /------------------\
```

- **Unit tests** should be the large majority: milliseconds each, no network/disk/DB, testing one unit of behavior in isolation.
- **Integration tests** verify that units correctly compose — a real (or realistic, e.g. containerized) database, an HTTP client hitting a test server, a message broker.
- **End-to-end tests** exercise the full system as a user would. Keep this layer thin — each e2e test is expensive to write, slow to run, and flaky by nature (network, timing, UI rendering), so reserve it for the critical user journeys that nothing else can verify.

If most of the suite's confidence comes from e2e tests, that's an inverted pyramid ("ice cream cone") — a warning sign that unit-level design isn't testable, usually because of tight coupling or hidden dependencies.

## TDD: red-green-refactor

When practicing test-driven development, work in this strict cycle:

1. **Red** — write a test for behavior that doesn't exist yet; watch it fail for the *right* reason (not a typo or import error).
2. **Green** — write the minimum code to make it pass. Resist the urge to build more than the test demands.
3. **Refactor** — clean up the implementation (and the test) with the safety net of a passing test, then repeat.

TDD is a design tool as much as a testing tool: writing the test first forces you to design the interface from the caller's perspective before you design the implementation.

## FIRST principles

Good tests are:

- **Fast** — a slow suite gets run less often and eventually gets skipped.
- **Independent** — no test should depend on another test's side effects or run order.
- **Repeatable** — same result every run, in any environment (no reliance on system time, network, or execution order).
- **Self-validating** — pass or fail with a clear boolean outcome; no reading log output to decide if it worked.
- **Timely** — written close to the code it covers, not bolted on weeks later (or never).

## Structuring a test: Arrange-Act-Assert

```markdown
## Test structure
Arrange: set up inputs, dependencies, and any test doubles
Act:     call the one thing under test
Assert:  check the observable outcome
```

**Example 1:**
Input: Test that `calculateDiscount` applies a 10% discount for orders over $100
Output:
```python
def test_discount_applies_above_threshold():
    # Arrange
    order = Order(total=150.00)

    # Act
    discounted = calculate_discount(order)

    # Assert
    assert discounted.total == 135.00
```

Name tests after the *behavior*, not the method: `test_discount_applies_above_threshold`, not `test_calculateDiscount_1`. A failing test name should tell you what broke without opening the file.

## What to test

- The **behavior** (inputs → outputs, and side effects), never internal implementation details — a test that breaks every time you refactor without changing behavior is testing the wrong thing.
- **Boundary and edge cases**: empty collections, zero, negative numbers, max values, null/None, off-by-one boundaries.
- **Error paths** as rigorously as the happy path — what happens on invalid input, a timeout, a dependency failure.
- **One logical assertion focus per test** — multiple `assert` calls are fine if they're all checking facets of the same outcome; don't test unrelated behaviors in one test.

## Test doubles — use the right one

| Double | Purpose |
|---|---|
| **Dummy** | Passed to satisfy a parameter list; never actually used |
| **Stub** | Returns canned answers to calls made during the test |
| **Spy** | A stub that also records how it was called, for later assertions |
| **Mock** | Pre-programmed with expectations; the test fails if those expectations aren't met |
| **Fake** | A working but simplified implementation (e.g., an in-memory DB instead of a real one) |

Mock or stub things that are slow, non-deterministic, external, or expensive (network calls, real databases, clocks, randomness). Don't mock the thing you're actually testing, and be wary of over-mocking — a test full of mocks that verifies "did I call the mock correctly" instead of real behavior gives false confidence and breaks on every refactor.

## Coverage: a signal, not a target

- Use coverage to find *untested* code, not as a KPI to hit a number. 100% coverage with weak assertions (calling a function without checking its output) is worse than useless — it's a false sense of safety.
- Prioritize coverage on business-critical and error-prone paths (payment logic, auth, data migrations) over boilerplate (simple getters, framework glue).
- A sudden coverage drop in CI is a useful gate; a mandated "every PR must be 90%+" often just incentivizes low-value tests written to satisfy the number.

## Flaky tests

A test that sometimes fails without a code change is worse than no test — it erodes trust in the whole suite ("just re-run it" becomes the norm, and real failures get ignored too).

Common causes, in rough order of frequency:
1. Shared/leaked state between tests (a database row, a global, a singleton not reset)
2. Timing assumptions (`sleep(1)` instead of waiting on a condition; race conditions in async code)
3. Order dependence (test B only passes if test A ran first)
4. External dependencies not fully mocked (real network calls, real clocks, real randomness without a fixed seed)

Don't quietly ignore or endlessly retry a flaky test — quarantine it (mark it skipped, file a ticket) so its flakiness is visible, and fix or delete it promptly. A "known flaky, ignore it" culture spreads.

## CI test practices

- Run the fast unit layer on every push; reserve slow integration/e2e layers for merge-to-main or a scheduled run if they can't be made fast enough for every commit.
- Fail the build fast — run cheap/fast tests first so a broken build is reported in seconds, not after a 20-minute suite.
- Parallelize independent test files/suites; independence (FIRST) is what makes this safe.

## Checklist

- [ ] Suite is pyramid-shaped: mostly unit, some integration, few e2e
- [ ] Each test is independent and passes in isolation and in any order
- [ ] Tests assert behavior/outputs, not internal implementation details
- [ ] Edge cases and error paths are covered, not just the happy path
- [ ] Test doubles replace slow/external/non-deterministic dependencies only
- [ ] No known-flaky tests are silently tolerated in CI
- [ ] Coverage gaps are reviewed for *risk*, not chased to hit a percentage

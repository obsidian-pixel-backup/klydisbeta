---
name: refactoring-clean-code
description: Principles and patterns for writing maintainable code and safely restructuring existing code — the SOLID principles, DRY/YAGNI/KISS, a catalog of common code smells, core refactoring moves (extract method/variable, replace conditional with polymorphism, introduce parameter object, etc.), and refactoring discipline (small steps, tests green, separate commits). Use whenever the user asks to clean up, simplify, restructure, or improve the maintainability of code, review code for design quality, explain why code is hard to change, or reduce duplication/complexity — even without the word "refactor" appearing.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Refactoring & Clean Code

Refactoring means changing the internal structure of code **without changing its observable behavior** — every refactor should be a no-op from the outside, verified by tests staying green throughout. Mixing a refactor with a behavior change in the same commit is one of the most common ways to make a bug impossible to bisect later.

## SOLID principles

- **S — Single Responsibility**: a class/module should have one reason to change. If describing what it does requires "and," it's probably two responsibilities.
- **O — Open/Closed**: open for extension, closed for modification — add new behavior by adding new code (a new subclass, a new strategy), not by editing a growing `switch`/`if` chain in existing code every time a case is added.
- **L — Liskov Substitution**: a subtype must be usable anywhere its base type is expected, without breaking the caller's assumptions. If a subclass has to throw "not supported" for an inherited method, that's an LSP violation, not a minor detail.
- **I — Interface Segregation**: prefer several small, focused interfaces over one large one — a class shouldn't be forced to implement methods it doesn't need just because they're bundled with ones it does.
- **D — Dependency Inversion**: depend on abstractions, not concrete implementations — high-level policy code shouldn't import a specific low-level detail (a specific database driver, a specific HTTP client) directly; depend on an interface and inject the concrete choice.

SOLID isn't a checklist to apply everywhere uniformly — over-applying it (an interface with exactly one implementation, a dependency injected for a class used nowhere else) is itself a form of over-engineering. Apply it where the *why* holds: code that's likely to grow new variants, or that needs to be tested in isolation from a slow/external dependency.

## DRY, YAGNI, KISS

- **DRY (Don't Repeat Yourself)** — every piece of knowledge should have one authoritative representation. Note: DRY is about *knowledge*, not *text* — two pieces of code that look similar today but represent unrelated business rules (and will evolve independently) shouldn't be merged just because they're currently identical; that creates false coupling.
- **YAGNI (You Aren't Gonna Need It)** — don't build a generic, configurable, pluggable version of something for a future requirement that doesn't exist yet. Add the abstraction when a second real use case actually shows up, not speculatively.
- **KISS (Keep It Simple)** — prefer the boring, obvious solution over the clever one. Clever code is a cost paid by every future reader; only spend that cost when the simple version is genuinely insufficient (measured, not assumed).

## Common code smells

A smell isn't a bug — it's a signal that a change is likely to be harder than it should be.

| Smell | What it looks like | Usual fix |
|---|---|---|
| **Long method** | A function that keeps growing and does several things in sequence | Extract Method — split into named steps |
| **Large class / God object** | One class that knows/does everything | Extract Class — split by responsibility |
| **Duplicated code** | The same logic copy-pasting with small variations | Extract Method/Function, or Pull Up Method if it's across subclasses |
| **Long parameter list** | A function taking 5+ arguments | Introduce Parameter Object — group related args into one type |
| **Feature envy** | A method that uses another object's data more than its own | Move Method to the class whose data it's really operating on |
| **Primitive obsession** | Using raw strings/ints for concepts with rules (email, money, a status) | Replace with a small value type that encapsulates validation/behavior |
| **Shotgun surgery** | One conceptual change requires editing many unrelated files | Consolidate the scattered logic into one place |
| **Switch/type-check chains** | Repeated `if type == X` / `switch(type)` blocks scattered across the code | Replace Conditional with Polymorphism |
| **Speculative generality** | Abstraction/hooks built for a use case that doesn't exist yet | Delete it (YAGNI) until it's actually needed |

## Core refactoring moves

**Example 1 — Extract Method:**
Input: A 40-line function that validates input, computes a total, and sends a notification, all inline.
Output: Three named functions (`validate_order`, `compute_total`, `notify_customer`) called in sequence from a short orchestrating function — each is independently readable, testable, and reusable.

**Example 2 — Replace Conditional with Polymorphism:**
Input:
```python
def shipping_cost(order):
    if order.type == "standard":
        return 5.00
    elif order.type == "express":
        return 15.00
    elif order.type == "overnight":
        return 30.00
```
Output: A `ShippingStrategy` base type with `Standard`/`Express`/`Overnight` subclasses each implementing `cost()`. Adding a new shipping type means adding a new class, not editing this function again (Open/Closed).

Other high-value moves worth knowing by name: **Rename** (a name that no longer says what the thing does — do this liberally, it's nearly free with modern tooling), **Extract Variable** (name an intermediate expression to make a condition self-documenting), **Inline** (the reverse of Extract, when an abstraction adds indirection without adding clarity), **Introduce Parameter Object**, **Replace Magic Number with Named Constant**, **Guard Clauses** (return/throw early to flatten nested conditionals instead of deep `if/else` pyramids).

## Refactoring discipline

- **Never refactor and change behavior in the same commit.** Land pure refactors labeled as such (`refactor:` in Conventional Commits) separately from `feat`/`fix` commits, so a regression can be bisected to "was this a behavior change or a restructuring?" instantly.
- **Tests must be green before starting and after every step.** If there's no test coverage for the code being touched, write characterization tests first (tests that lock in current behavior, even if that behavior isn't ideal) before refactoring — refactoring without a safety net is just risky rewriting.
- **Work in small, reversible steps.** Each individual refactor move (one Extract Method, one Rename) should be small enough to trust without re-reading the whole diff; commit or checkpoint frequently enough that a mistake is a one-step revert, not a lost afternoon.
- **The Boy Scout Rule**: leave code a little cleaner than you found it as you pass through — but resist turning a small bug-fix PR into an unrelated, large-scale rewrite; do opportunistic small cleanup, and file a follow-up for anything bigger.

## When *not* to refactor

- Code with no tests and no time budget to add characterization tests first — the risk of silently changing behavior is too high.
- Code about to be deleted or replaced wholesale — polishing something that won't exist next sprint is wasted effort.
- Under acute deadline pressure on unrelated work — refactoring is an investment; don't make it while also trying to ship a time-critical fix in the same breath.

## Checklist

- [ ] The change is behavior-preserving; tests pass identically before and after
- [ ] Refactor commits are separate from feature/bugfix commits
- [ ] Each class/function still has one clear reason to change
- [ ] No duplicated *knowledge* (not just duplicated text) remains
- [ ] Abstractions in the code map to real, current use cases — not anticipated ones
- [ ] Names say what things are/do without needing a comment to clarify

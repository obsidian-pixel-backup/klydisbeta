---
name: refactoring-legacy-code
description: Safe practices for refactoring legacy codebases without introducing regressions — discovering code seams, characterization tests, the Strangler Fig pattern, extract method techniques, dependency decoupling, and safe incremental transformations. Use when refactoring untested code, modernizing monoliths, or restructuring legacy modules.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Refactoring Legacy Code

Legacy code is code without automated tests. Refactoring legacy code requires establishing a safety net before altering internal structures.

## The Safe Refactoring Workflow

1. **Identify the Seam**: Find a place where you can alter behavior without editing the entire system (interfaces, dependency points).
2. **Write Characterization Tests**: Capture current system behavior *as it actually exists today* (bugs included) to guarantee no unintended behavior changes.
3. **Make Small Transformations**: Perform atomic automated refactorings (Extract Method, Rename Symbol, Extract Interface).
4. **Run Tests Continuously**: Verify characterization tests pass after every micro-change.

## The Strangler Fig Pattern

When replacing large legacy subsystems, don't attempt a risky "big bang" rewrite. Incrementally replace legacy functionality step by step:

```
[ Incoming Requests ] ──► [ API Gateway / Router ]
                                │         │
                                │ (New)   │ (Legacy)
                                ▼         ▼
                        [ New Module ]  [ Legacy Monolith ]
```

1. Route new requests to the new service alongside legacy.
2. Gradually migrate endpoints one by one.
3. Once 100% of traffic flows to the new module, remove the legacy path.

## Red Flags During Refactoring

- **Refactoring & Feature Changes Together**: Never mix refactoring with adding new features in the same commit. Keep them strictly separate.
- **Deleting Tests You Don't Understand**: If a legacy test fails during refactoring, investigate *why* before updating or removing it.

## Checklist

- [ ] Characterization tests pass before starting code edits
- [ ] Refactoring commits separated from feature changes
- [ ] External behavioral contract remains identical
- [ ] Safe IDE refactoring tools (Extract Method, Rename) preferred over manual retyping

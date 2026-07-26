---
name: agentic-code-generation-quality
description: Guidelines for high-precision code synthesis: AST-compliant edits, lint compliance, avoiding code duplication, zero-hallucination imports, and type safety.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Code Generation Quality

High-quality agentic code generation produces production-grade code that compiles cleanly, adheres to codebase linting rules, avoids duplicate logic, and includes robust error handling.

## Core Quality Guidelines

1. **Strict Type Safety**: Fully annotate all function signatures, variables, and return types (TypeScript strict mode, Python type hints, Rust explicit types).
2. **Zero-Hallucination Imports**: Verify that imported packages exist in `package.json`, `pyproject.toml`, or `Cargo.toml` before referencing them.
3. **Minimal Surgical Edits**: Use targeted replacements (`replace_file_content`) instead of re-writing entire files to prevent token waste and accidental regression.
4. **Preserve Comments & Formatting**: Maintain existing codebase formatting style, docstrings, and comments untouched unless explicitly modifying them.

---

## AST-Compliant Surgical Edit Standard

```typescript
// BAD: Re-writing entire file for a 1-line change
// GOOD: Precise targeted block edit using exact matching

// Target Content:
function calculateTotal(items: Item[]): number {
  return items.reduce((acc, item) => acc + item.price, 0);
}

// Replacement Content:
function calculateTotal(items: Item[], taxRate: number = 0): number {
  const subtotal = items.reduce((acc, item) => acc + item.price, 0);
  return subtotal * (1 + taxRate);
}
```

---

## Code Generation Self-Verification Workflow

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│ Generate Code   │───>│ Run Linter /    │───>│ Run Type-Check  │
│ Snippet         │    │ Formatter       │    │ (tsc / mypy)    │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                                       │
┌─────────────────┐    ┌─────────────────┐             ▼
│ Accept & Commit │<───│ Run Unit Tests  │<───[ Type & Lint Clean ]
│ Changes         │    │ (pytest / jest) │
└─────────────────┘    └─────────────────┘
```

---

## Verification Checklist

- [ ] Generated code passes linter (`eslint`, `ruff`, `flake8`) with 0 errors.
- [ ] All external imports exist in the project dependencies manifest.
- [ ] New functions include explicit return type signatures and docstrings.
- [ ] No dead code, placeholder `TODO`s, or temporary console logs remain.

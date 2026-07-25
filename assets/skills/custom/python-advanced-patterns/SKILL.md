---
name: python-advanced-patterns
description: Advanced Python software engineering standards — static typing with MyPy/pyright, async/await concurrency, context managers, custom decorators, generator pipelines, Dataclasses/Pydantic models, and packaging best practices (uv/poetry/ruff). Use when writing Python applications, designing Python libraries, or optimizing Python code.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Advanced Python Engineering

Modern Python relies on strong type hinting, fast tooling (`ruff`, `uv`), explicit data models (`pydantic`), and structured concurrency (`asyncio`).

## Type Hinting & Validation

- Use explicit typing for function signatures and public APIs:
  ```python
  from typing import Sequence
  from pydantic import BaseModel, Field, EmailStr

  class UserCreate(BaseModel):
      username: str = Field(min_length=3, max_length=50)
      email: EmailStr

  def process_users(users: Sequence[UserCreate]) -> int:
      return len(users)
  ```
- Run static type checkers (`mypy` or `pyright`) in `strict` mode in CI pipelines.

## Context Managers & Resource Safety

Always wrap resource lifetimes in context managers (`with` statements or `@contextmanager`):

```python
from contextlib import contextmanager
import time

@contextmanager
def execution_timer(label: str):
    start = time.perf_counter()
    try:
        yield
    finally:
        duration = time.perf_counter() - start
        print(f"[{label}] Completed in {duration:.4f}s")
```

## Generators for Large Memory Pipelines

Avoid loading huge datasets fully into memory. Stream data using generator functions (`yield`):

```python
def read_large_log(file_path: str):
    with open(file_path, "r", encoding="utf-8") as f:
        for line in f:
            if "ERROR" in line:
                yield line.strip()
```

## Tooling Standards

- **Linter & Formatter**: Use `ruff` for ultra-fast linting and formatting.
- **Dependency Management**: Use `uv` or `poetry` with reproducible lockfiles (`uv.lock` or `poetry.lock`).

## Checklist

- [ ] All public functions type-annotated and verified with MyPy/Pyright
- [ ] Data validation handled using Pydantic or Dataclasses
- [ ] Memory-heavy file/data streams use generators
- [ ] Linter (`ruff`) clean with zero errors

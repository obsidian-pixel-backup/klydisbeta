---
name: agentic-self-correction-loops
description: Implementing autonomous reflection, test-driven self-correction loops, traceback parsing, error diagnostic classification, and automatic repair strategies.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Self-Correction Loops

Autonomous agents must detect when their generated code or execution plan has failed, analyze the empirical failure diagnostics, and iteratively apply self-corrections without human intervention.

## Core Reflection Loop Architecture

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  Generate    │───> │  Execute     │───> │ Did Pass?    │──[YES]──> Done
│ Code Edit    │     │ Build/Test   │     │              │
└──────────────┘     └──────────────┘     └──────────────┘
       ▲                                         │ [NO]
       │             ┌──────────────┐            │
       └─────────────│ Reflection & │<───────────┘
                     │ Diagnostics  │
                     └──────────────┘
```

---

## Step-by-Step Diagnostic Protocol

1. **Capture Raw Output**: Extract stderr, stdout, exit code, and stack trace.
2. **Classify Failure Category**:
   - `SYNTAX_ERROR`: Missing braces, syntax typos $
ightarrow$ Fix syntax immediately.
   - `TYPE_ERROR`: Mismatched parameter types $
ightarrow$ Inspect signature definitions.
   - `ASSERTION_FAILURE`: Logic flaw in algorithm $
ightarrow$ Trace input values step-by-step.
   - `ENVIRONMENT_ERROR`: Missing dependencies/permissions $
ightarrow$ Request tool/dep fix.
3. **Form Hypotheses & Test Fix**: Propose a targeted patch addressing the root cause. Avoid shotgun debugging (random edits).

---

## Code Example: Self-Correction Loop Implementation

```python
def self_correction_loop(task_prompt, max_retries=3):
    code = generate_initial_code(task_prompt)
    for attempt in range(1, max_retries + 1):
        result = run_build_and_tests()
        if result.success:
            return code
        
        # Extract exact traceback
        diagnostics = parse_traceback(result.stderr)
        
        # Reflection step: Explain root cause before generating fix
        reflection = reflect_on_failure(
            code=code,
            error=diagnostics,
            attempt=attempt
        )
        
        # Apply patch
        code = apply_code_fix(code, reflection.patch_instructions)
    
    raise ExecutionError("Self-correction exhausted retry budget.")
```

---

## Verification Checklist

- [ ] Agent reads full un-truncated error logs before generating a repair hypothesis.
- [ ] Repairs address root cause instead of suppressing exceptions or deleting failing tests.
- [ ] Self-correction budget enforces max 3-5 iterations to prevent infinite loops.
- [ ] Each iteration compares previous diff with current result to ensure progress.

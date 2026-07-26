---
name: agentic-tool-calling-design
description: Designing clean, deterministic, self-documenting tool interfaces, argument validation, error handling schemas, and side-effect sandboxing for AI agent tool calling.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Tool Calling Design

Tools provide AI agents with agency to interact with the filesystem, shell, web, databases, and external APIs. Designing robust tool interfaces is critical for safety and efficiency.

## Core Architectural Principles

1. **Self-Documenting Tool Schemas**: Every tool argument must feature clear, descriptive docstrings and constraints (`enum`, `min`, `max`, `regex`).
2. **Atomic Tool Operations**: Tools should perform one unambiguous task cleanly (e.g., `read_file` vs `read_and_edit_and_compile_file`).
3. **Idempotency & Safety**: Side-effecting tools must support dry-run flags or confirmation steps for high-risk operations.
4. **Structured Error Responses**: Tool error outputs must return structured, actionable messages that teach the agent how to correct its arguments.

---

## Tool Definition Standard Schema (OpenAI / AGY Format)

```json
{
  "name": "execute_database_query",
  "description": "Executes a parameterized SQL query against the read-only reporting database replica.",
  "parameters": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "The parameterized SQL query using standard ANSI SQL syntax."
      },
      "params": {
        "type": "array",
        "items": { "type": "string" },
        "description": "Ordered parameters for substitution to prevent SQL injection."
      },
      "max_rows": {
        "type": "integer",
        "default": 100,
        "description": "Maximum number of rows to return (capped at 1000)."
      }
    },
    "required": ["query", "params"]
  }
}
```

---

## Tool Error Diagnostics Pattern

When a tool call fails, return structured diagnostic details rather than generic tracebacks:

```json
{
  "status": "ERROR",
  "error_code": "INVALID_ARGUMENT_TYPE",
  "message": "Parameter 'line_number' expected integer, received string 'L45'.",
  "correction_hint": "Pass line_number as integer 45 without the 'L' prefix."
}
```

---

## Safety Sandboxing Guidelines

- **Path Normalization**: Canonicalize all input paths (`os.path.realpath`) and verify they reside within authorized sandbox directories.
- **Command Sanitization**: Disallow shell interpolation operators (`|`, `;`, `&&`, `` ` ``) unless explicitly sandboxed.
- **Timeout Guards**: Enforce hard execution timeouts (e.g., 30s for commands, 10s for HTTP GETs) to prevent frozen agent state.

---

## Verification & Tool Review Checklist

- [ ] Tool parameters include explicit type descriptions and default values.
- [ ] Input parameters are validated before executing underlying logic.
- [ ] Error messages provide clear correction instructions for the agent.
- [ ] File and command operations check bounds against authorized sandbox paths.

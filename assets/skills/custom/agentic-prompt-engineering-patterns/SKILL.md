---
name: agentic-prompt-engineering-patterns
description: Designing high-performance system prompts, dynamic context injection, few-shot demonstration formatting, output schemas, and prompt versioning for LLM agents.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Prompt Engineering Patterns

Prompt engineering for autonomous AI coding agents requires precision, unambiguous instructions, strict structural formatting, and dynamic context injection.

## Core Principles

1. **Role Identification & System Boundary**: Clear system prompt setting role, permissions, operational constraints, and style.
2. **Delimited Context Injection**: Wrap user inputs, file contents, runtime context, and metadata in XML/HTML-style tags (`<USER_REQUEST>`, `<FILE_CONTENT>`, `<SEARCH_RESULTS>`).
3. **Chain-of-Thought (CoT) & XML Step Enforcement**: Require step-by-step reasoning prior to tool invocation or code generation.
4. **Negative Constraints & Guardrails**: Explicitly state what the model MUST NOT do (e.g., "Do NOT delete unit tests", "Do NOT invent third-party imports").

---

## Structural Prompt Blueprint

```markdown
<identity>
You are an expert Principal Systems Engineer specializing in high-throughput C++ and Rust services.
</identity>

<guidelines>
- Always verify type signatures before writing call sites.
- Never use raw pointers; use smart pointers (`std::unique_ptr`, `std::shared_ptr`).
- Format code according to Google C++ Style Guide.
</guidelines>

<context>
<file path="src/server.cpp">
{{FILE_CONTENT}}
</file>
</context>

<task>
Refactor `src/server.cpp` to handle async connection timeouts cleanly.
</task>

<output_format>
Return your plan in `<plan>` tags, followed by exact code diffs in `<diff>` tags.
</output_format>
```

---

## Key Prompt Engineering Patterns

### Pattern 1: Few-Shot Structural Alignment
Provide 2-3 minimal, clean exemplars showing input context alongside ideal agent responses.

```markdown
<example>
Input: Fix null reference exception in `user.name` lookup.
Thought: The `user` object may be undefined if the database query returns no rows.
Solution:
```typescript
const username = user?.name ?? 'Guest';
```
</example>
```

### Pattern 2: Schema-Enforced JSON Output
Force deterministic responses by enforcing JSON schema parsing.

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "thought_process": { "type": "string" },
    "severity": { "type": "string", "enum": ["LOW", "MEDIUM", "HIGH", "CRITICAL"] },
    "recommended_fix": { "type": "string" },
    "modified_files": {
      "type": "array",
      "items": { "type": "string" }
    }
  },
  "required": ["thought_process", "severity", "recommended_fix", "modified_files"]
}
```

---

## Anti-Patterns to Avoid

- **Vague Directives**: "Make the code better" $
ightarrow$ Replace with quantitative criteria: "Reduce cyclomatic complexity under 10 and add unit tests covering boundary cases".
- **Contradictory Instructions**: Setting `always_on` verbose output while demanding `JSON-only` output.
- **Over-Prompting**: Dumping 10,000 words of generic guidelines that dilute attention from the primary objective.

---

## Verification & Prompt Testing Checklist

- [ ] Context elements are wrapped in unique, non-colliding XML tags.
- [ ] System prompt explicitly includes negative constraints ("DO NOT...").
- [ ] Output schema is validated against standard JSON / Pydantic models.
- [ ] Few-shot examples use exact formatting required in production.

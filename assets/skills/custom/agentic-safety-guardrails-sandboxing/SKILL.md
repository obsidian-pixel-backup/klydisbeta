---
name: agentic-safety-guardrails-sandboxing
description: Implementing input/output safety guardrails, execution sandboxing, tool permissions, escalation controls, and policy enforcement for coding agents.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Safety Guardrails & Sandboxing

Deploying autonomous AI coding agents requires robust guardrails to prevent unintended file deletions, credential leaks, unauthorized network calls, or destructive shell operations.

## Core Safety Principles

1. **Principle of Least Privilege**: Grant agents minimum permissions necessary for the assigned task.
2. **Explicit Escalation Prompts**: Force user approval before running high-risk commands (e.g., `rm -rf`, `git push --force`, `drop database`).
3. **Filesystem Boundary Constraints**: Restrict file read/write operations to canonicalized paths inside the workspace folder.
4. **Secret Redaction**: Intercept stdout/stderr streams to strip API keys, tokens, and private passwords prior to logging or returning to the model.

---

## Security Permission Matrix

| Command / Tool Action | Execution Tier | Safeguard Requirement |
| :--- | :--- | :--- |
| `view_file`, `grep_search` | Read-Only | Auto-approved inside workspace |
| `write_to_file`, `replace_file_content` | Safe Edit | Auto-approved within workspace bounds |
| `run_command` (`npm test`, `git status`) | Safe Execution | Allowed under 30s timeout |
| `run_command` (`git push`, `npm publish`) | Restricted | Requires explicit user prompt approval |
| `run_command` (`rm -rf`, `sudo`, `curl | bash`) | Forbidden | Hard blocked by execution filter |

---

## Secret Masking Filter Pattern (Python)

```python
import re

SECRET_PATTERNS = [
    re.compile(r"(sk-[a-zA-Z0-9]{32,})"),
    re.compile(r"(ghp_[a-zA-Z0-9]{36})"),
    re.compile(r"(eyJhbGciOi[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+)")
]

def sanitize_output(text: str) -> str:
    for pattern in SECRET_PATTERNS:
        text = pattern.sub("[REDACTED_SECRET]", text)
    return text
```

---

## Verification Checklist

- [ ] File operations verify target paths stay within workspace boundaries.
- [ ] Command runner blocks destructive system commands (`rm -rf /`, `mkfs`).
- [ ] Secret redaction engine sanitizes output logs before returning responses.
- [ ] Permission requests specify the minimal required action scope.

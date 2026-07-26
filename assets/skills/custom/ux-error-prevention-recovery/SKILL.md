---
name: ux-error-prevention-recovery
description: Preventing user errors and designing error recovery: destructive action confirmations, inline validation, human-readable error messages, and recovery prompts.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX Error Prevention & Recovery Guidelines

Great user experiences actively prevent accidental errors and offer clear, non-judgmental pathways to recover when mistakes occur.

## Error Prevention Strategies

1. **Confirmation for Irreversible Actions**: Require explicit typed confirmation for high-stakes actions (e.g., typing repo name to delete database).
2. **Inline Validation as You Type**: Validate input fields during blurring or typing to catch formatting errors early.
3. **Disabled Incompatible Actions**: Disable buttons when prerequisites are not met, showing helpful tooltip explanations on hover.

---

## Human-Readable Error Message Schema

Avoid exposing cryptic internal code stack traces (`500 INTERNAL_SERVER_ERROR`). Structure error prompts using 3 parts:

```text
[What Happened] + [Why It Happened] + [How to Fix It]
```

### Example Comparison
- **Cryptic**: `Error code 0x80041001: Connection reset.`
- **Human-Centric**: `Unable to save changes. Your internet connection appears offline. Please reconnect to the network and try again.`

---

## Verification Checklist

- [ ] Destructive database or file deletion actions require explicit confirmation.
- [ ] Error messages describe the exact action required for recovery.
- [ ] Form submit buttons present hover tooltips explaining why they are disabled.

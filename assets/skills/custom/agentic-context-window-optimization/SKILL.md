---
name: agentic-context-window-optimization
description: Managing and optimizing LLM context windows: progressive disclosure, token compression, summarize-on-overflow, context eviction strategies, and token budget management.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Context Window Optimization

Large context windows are expensive and susceptible to "lost-in-the-middle" attention degradation. Managing context budgets strategically ensures fast, high-recall agent performance.

## Core Optimization Strategies

1. **Progressive Disclosure**: Load only high-level metadata (tool descriptions, skill summaries) initially; fetch full content on demand when required.
2. **Context Compression**: Strip comments, redundant whitespace, lockfiles, and repetitive logs before injecting into context.
3. **Summarize-on-Overflow**: When approaching context limits (e.g., 80% threshold), trigger a background summarization agent to collapse history into a concise state summary.
4. **Sliding Window with Key-File Pinning**: Keep initial system instructions and key target files pinned while sliding past conversation turns.

---

## Token Budget Allocation Standard

| Context Component | Budget Percentage | Purpose |
| :--- | :--- | :--- |
| **System Prompt & Rules** | 10% | Guidelines, identity, tool definitions |
| **Active Code & Artifacts** | 45% | Current files being edited / inspected |
| **Conversation & Trajectory** | 25% | Recent turns and tool results |
| **Response Working Space** | 20% | Generation headroom for thoughts & code |

---

## Summarize-on-Overflow State Compression Pattern

```markdown
<compressed_conversation_state>
<completed_milestones>
- Located bug in src/auth.py at line 142 (invalid JWT validation key).
- Created unit test in tests/test_auth.py reproducing the failure.
</completed_milestones>
<current_state>
- Editing src/auth.py to inject dynamic key rotation fetcher.
</current_state>
<pending_tasks>
- Re-run `pytest tests/test_auth.py`.
- Update API docs in docs/auth.md.
</pending_tasks>
</compressed_conversation_state>
```

---

## Verification Checklist

- [ ] System prompt and skill descriptions stay under 15% total token budget.
- [ ] File truncation warnings notify the agent when viewing large files (>800 lines).
- [ ] Output logs and shell execution results are capped at 50-100 lines.
- [ ] Summarization compresses long history turns without losing critical task state.

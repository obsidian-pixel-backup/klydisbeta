---
name: agentic-memory-management-systems
description: Designing persistent working memory, short-term session state, long-term vector/knowledge graphs, and memory hygiene for autonomous coding agents.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Memory Management Systems

Memory management allows coding agents to persist knowledge across multi-turn sessions, remember project preferences, and preserve workspace architectural insights.

## Memory Taxonomy

```
┌─────────────────────────────────────────────────────────────┐
│                    Agent Memory Hierarchy                   │
├──────────────────────────┬──────────────────────────────────┤
│ Short-Term / Working     │ Immediate context, Scratchpads,  │
│ Memory                   │ Active conversation window       │
├──────────────────────────┼──────────────────────────────────┤
│ Episodic Memory          │ Past task logs, execution steps, │
│                          │ recent commit summaries          │
├──────────────────────────┼──────────────────────────────────┤
│ Semantic Long-Term       │ Architecture knowledge graph,    │
│ Memory                   │ project rules, tech stack specs  │
└──────────────────────────┴──────────────────────────────────┘
```

---

## Memory File Architecture (`.agents/memory/`)

```text
.agents/
├── memory/
│   ├── workspace_profile.json  # Tech stack, framework versions, run commands
│   ├── architectural_decisions.md # Key ADRs (Architectural Decision Records)
│   └── user_preferences.json  # User formatting, naming, and style constraints
```

---

## Memory Update Protocol (Memory Hygiene)

1. **Extract**: At task completion, inspect the trajectory for reusable facts (e.g., "PostgreSQL runs on port 5433 in local dev Docker setup").
2. **Deduplicate**: Check existing long-term memory to prevent storing redundant or conflicting facts.
3. **Persist**: Write structured updates to local memory files.

```json
{
  "entity": "database",
  "attribute": "local_dev_port",
  "value": 5433,
  "source_task": "task-849",
  "verified_at": "2026-07-26T19:00:00Z"
}
```

---

## Verification Checklist

- [ ] Working memory scratchpad is updated after major milestone completions.
- [ ] Long-term memory entries contain source attribution and verification timestamps.
- [ ] Contradictory memory records are automatically flagged and resolved.
- [ ] Sensitive secrets (passwords, tokens) are stripped before saving to memory.

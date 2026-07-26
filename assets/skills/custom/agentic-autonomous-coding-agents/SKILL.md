---
name: agentic-autonomous-coding-agents
description: Designing loop architectures for autonomous dev agents: plan-execute-verify cycles, workspace state tracking, commit hygiene, and automated bug fixing.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Autonomous Coding Agents

Autonomous coding agents execute complex software engineering goals independently by looping through perception, planning, tool execution, and verification phases.

## The Autonomous Execution Loop

```
               ┌───────────────────────┐
               │  Read Prompt & Context│
               └───────────────────────┘
                           │
                           ▼
               ┌───────────────────────┐
               │ Formulate / Update    │
               │ Implementation Plan   │
               └───────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────┐
│ Execute Step (Read File / Write File / Run Command) │
└─────────────────────────────────────────────────────┘
                           │
                           ▼
               ┌───────────────────────┐
               │ Verify Runtime Results│
               │ (Build & Unit Tests)  │
               └───────────────────────┘
                           │
             ┌─────────────┴─────────────┐
             │                           │
      [Tests Failed]               [Tests Passed]
             │                           │
             ▼                           ▼
┌─────────────────────────┐ ┌─────────────────────────┐
│ Run Self-Correction Loop│ │ Finalize Git Commit &   │
└─────────────────────────┘ │ Present Walkthrough     │
                            └─────────────────────────┘
```

---

## Autonomous Operation Rules

1. **Empirical Verification Required**: Never mark a task complete without running the build/test command to verify output.
2. **Incremental Commits**: Commit clean working checkpoints to Git after reaching verified milestones.
3. **No Hallucinated Success**: If a build fails, inspect stderr logs immediately instead of claiming completion.
4. **Clean Workspace State**: Revert temporary scratch files and debugging log dumps before finishing.

---

## Verification Checklist

- [ ] Agent maintains a clear task breakdown list (`implementation_plan.md`).
- [ ] Every code modification step is followed by automated test execution.
- [ ] Git commit messages follow Conventional Commits standard (`feat:`, `fix:`, `refactor:`).
- [ ] Final output includes a concise walkthrough of changes and verification evidence.

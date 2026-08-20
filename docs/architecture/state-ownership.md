# Klydis Beta — State Ownership Map

> Phase 0 — which component owns which state and persistence surface.

## State Ownership Table

| State | In-Memory Owner | Persistence Table | Authority |
|---|---|---|---|
| Sessions | `ChatEngine` | `sessions` | Database |
| Messages | `ChatEngine` | `messages` + FTS5 | Database |
| Tasks | `TaskManager` | `tasks` | Database |
| Runs | `AgentRuntime._activeRuns` | `runs` | Database (in-memory is cache) |
| Steps (typed) | `TaskStepBuilder` (derived) | `task_steps` | Database |
| Plan (checklist) | `ToolExecutor._sessionPlanItems` | `tasks.plan_json` | Database |
| Actions | `AgentRuntime` | `task_actions` | Database |
| Evidence | `ExecutionEvidenceLedger` | `execution_evidence` | Database (in-memory is cache) |
| Decisions | `ExecutionEvidenceLedger` | `execution_decisions` | Database |
| Events | — | `execution_events` | Database |
| File changes | `ToolExecutor` | `file_changes` | Database |
| Tool activity | `ToolExecutor._sessionToolActivity` | `tool_activity` | Database (in-memory is cache) |
| Artifacts | `ToolExecutor._sessionArtifacts` | `artifacts` | Database (in-memory is cache) |
| Queue | `ModelMessageQueue` | `queued_messages` | Database |
| Custom tools | `ToolExecutor` | `custom_tools` | Database |
| Lessons | `AdaptiveLearningService` | `lessons` | Database |
| Session notes | — | `session_notes` | Database |
| World state | `ContextOrchestrator` | `sessions.world_state` | Database |
| Workspace version | `ExecutionEvidenceLedger._runs[].WorkspaceVersion` | `execution_evidence.workspace_version` | In-memory (per run) |
| Skills (active) | `DynamicSkillSelector` | — | In-memory (per-turn) |
| Generation outcome | `AgentRuntime.ClassifyGeneration()` | — | Transient |
| Supervisor decision | `AgentSupervisor.DecideAfterTurn()` | `execution_decisions` | Database |
| Dispatch directive | `ExecutionDispatcher.BuildDirective()` | — | Transient |

## Ownership Rules

1. **Database is always authority** — in-memory collections in `ToolExecutor` and `ExecutionEvidenceLedger` are caches
2. **Workspace version** is currently in-memory only per run — Phase 10 will promote it to durable state
3. **Skills** are selected per-turn in `ChatViewModel` — Phase 4 will move to runtime
4. **The loop** is currently owned by `ChatEngine` — Phase 1 will move to `AgentRuntime`

## Persistence Flow (File Mutation Example)

```
Model proposes write_file
    │
    ▼
ActionGate.Validate()         → allowed
    │
    ▼
AgentRuntime.RecordRunActionStart()  → task_actions (InProgress)
    │
    ▼
ToolExecutor.CaptureFileMutationAsync()
    ├── snapshot before (hash)
    ├── write file
    ├── snapshot after (hash)
    ├── compute diff (DiffService)
    ├── persist FileChange → file_changes
    ├── emit ExecutionEvent → execution_events
    ├── register/update Artifact → artifacts
    └── emit ExecutionEvent → execution_events
    │
    ▼
AgentRuntime.RecordRunActionComplete()  → task_actions (Succeeded)
AgentRuntime.NoteRunFileChanged()       → workspace version++
AgentRuntime.RecordRunEvidence()         → execution_evidence
```

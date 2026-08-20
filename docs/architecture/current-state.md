# Klydis Beta — Current Architectural State

> Phase 0 inventory — immutable reference point before runtime extraction.
> Created as part of the architectural hardening blueprint.

## Component Inventory

| Component | File | Lines | Responsibility |
|---|---|---|---|
| `ChatEngine` | `src/Klydis.Core/Chat/ChatEngine.cs` | ~4,368 | Loop host, inference dispatch, context assembly, tool execution, continuation/repair, streaming |
| `AgentRuntime` | `src/Klydis.Core/Tasks/AgentRuntime.cs` | ~671 | Run lifecycle, outcome classification, evidence ledger, action ledger, supervisor dispatch |
| `AgentSupervisor` | `src/Klydis.Core/Tasks/AgentSupervisor.cs` | ~289 | Pure decision layer (completion gate, eligibility, `DecideAfterTurn`) |
| `TaskManager` | `src/Klydis.Core/Tasks/TaskManager.cs` | ~340 | Task resolution, creation, persistence, plan management |
| `TaskStateMachine` | `src/Klydis.Core/Tasks/TaskStateMachine.cs` | ~233 | State enums, `TaskRun` record, legal transition enforcement |
| `AgentLoopStateMachine` | `src/Klydis.Core/Tasks/AgentLoopStateMachine.cs` | ~226 | OODA-VR phase transitions (Observe→Orient→Decide→Act→Verify→Reflect) |
| `ExecutionDispatcher` | `src/Klydis.Core/Tasks/ExecutionDispatcher.cs` | ~243 | Pure decision→directive mapping |
| `ActionGate` | `src/Klydis.Core/Tasks/ActionGate.cs` | ~372 | Pre-execution validation (tool existence, step restriction, schema, replay, workspace boundary) |
| `TaskStep` / `TaskStepBuilder` | `src/Klydis.Core/Tasks/TaskStep.cs` | ~134 | First-class step records with `StepActionKind`, `AllowedTools`, `VerificationCriteria` |
| `StepClassifier` | `src/Klydis.Core/Tasks/StepClassifier.cs` | ~290 | Single owner of step semantics — classifies plan text into action kinds |
| `Evidence` | `src/Klydis.Core/Tasks/Evidence.cs` | ~76 | Typed verification evidence (EvidenceKind enum, workspace-versioned) |
| `ExecutionEvidenceLedger` | `src/Klydis.Core/Tasks/ExecutionEvidenceLedger.cs` | ~248 | Run-scoped versioned evidence with durable persistence and stale invalidation |
| `VerificationCriterion` | `src/Klydis.Core/Tasks/VerificationCriterion.cs` | ~54 | Predicate matching (kind + subject + exit code + workspace version) |
| `InteractionClassifier` | `src/Klydis.Core/Tasks/InteractionClassifier.cs` | ~250 | Message → mode classification (Conversation/Task/Autonomous) |
| `InitialPlanGenerator` | `src/Klydis.Core/Tasks/InitialPlanGenerator.cs` | ~130 | 6-step scaffold plan generation for new tasks |
| `ToolExecutor` | `src/Klydis.Core/Chat/ToolExecutor.cs` | ~4,300 | 30+ tool implementations, mutation pipeline, artifact registration |
| `GoalOrchestrator` | `src/Klydis.Core/Chat/GoalOrchestrator.cs` | ~450 | Multi-turn autonomous goal execution with stagnation tracking |
| `InferenceEngine` | `src/Klydis.Core/Inference/InferenceEngine.cs` | ~800+ | LLamaSharp wrapper, model lifecycle, KV cache, speculative decoding |
| `ContextOrchestrator` | `src/Klydis.Core/Memory/ContextOrchestrator.cs` | ~700+ | Token budgeting, rolling compression, world state |
| `MessageStore` | `src/Klydis.Core/Memory/MessageStore.cs` | ~2,268 | SQLite persistence (16 tables), WAL mode |
| `SystemPromptManager` | `src/Klydis.Core/Chat/SystemPromptManager.cs` | ~900+ | Prompt construction (full/compact/conversational profiles) |
| `DynamicSkillSelector` | `src/Klydis.Core/Skills/DynamicSkillSelector.cs` | ~200+ | Brain index generation, relevance scoring, skill activation |
| `ProcessManager` | `src/Klydis.Core/Processes/ProcessManager.cs` | ~400 | Background process management with bounded ring buffers |
| `IModelProtocol` | `src/Klydis.Core/Protocol/IModelProtocol.cs` | ~60 | Protocol abstraction (BuildPrompt, ParseOutput, FormatToolResult, GetStopTokens) |

## Database Schema (16 tables)

| Table | Purpose | Key Columns |
|---|---|---|
| `sessions` | Chat sessions | `id`, `title`, `model_id`, `world_state`, `plan_json` |
| `messages` | Conversation history + FTS5 | `session_id`, `role`, `content`, `token_count` |
| `tasks` | Durable agentic work units | `task_id`, `session_id`, `objective`, `status`, `plan_json` |
| `runs` | Execution attempts per task | `run_id`, `task_id`, `status`, `turn_count` |
| `task_steps` | Typed step metadata | `step_id`, `task_id`, `status`, `expected_action_kind`, `verification_criteria_json` |
| `task_actions` | Durable action ledger | `action_id`, `replay_key`, `tool_name`, `status`, `side_effect_level` |
| `execution_evidence` | Typed verification evidence | `evidence_id`, `workspace_version`, `kind`, `subject`, `exit_code` |
| `execution_decisions` | Supervisor decision audit trail | `decision_id`, `decision`, `reason` |
| `execution_events` | Factual event stream | `event_id`, `event_type`, `tool_name`, `path` |
| `file_changes` | Factual diffs | `change_id`, `path`, `before_hash`, `after_hash`, `diff` |
| `tool_activity` | Tool invocation log | `activity_id`, `tool_name`, `success`, `output_preview` |
| `artifacts` | Agent-produced files | `artifact_id`, `path`, `artifact_type`, `content_hash`, `is_current` |
| `queued_messages` | Durable steering queue | `id`, `content`, `mode`, `task_id` |
| `custom_tools` | User-created tools | `id`, `name`, `script` |
| `lessons` | Cross-session learning | `id`, `content`, `model_id` |
| `session_notes` | User notes per session | `id`, `session_id`, `content` |

## Existing Infrastructure Summary

### What is ALREADY built
- Run lifecycle with crash recovery (`EnsureRunAsync` detects stale Running runs, marks Interrupted)
- Durable action ledger with replay protection (`task_actions` + `replay_key`)
- Typed evidence with workspace versioning and stale invalidation
- Evidence-backed completion gate (empty checklist + eligibility)
- First-class TaskStep records with `StepActionKind`, `AllowedTools`, `VerificationCriteria`
- ActionGate pre-execution validation
- Supervisor decision dispatch with directive rendering
- OODA-VR state machine (`AgentLoopStateMachine`)
- 438 tests (436 pass + 2 hardware-gated skips)

### What is NOT built (the hardening gap)
- **The loop still lives in ChatEngine** — `AgentRuntime` cannot independently advance a Run
- Steps are plan-derived projections, not fully durable with dependencies
- No Turn or Generation persistence entities
- Skill selection resides in ChatViewModel, not runtime
- No formal action lifecycle state machine (Pending→Prepared→InProgress→Succeeded/Failed/Unknown)
- No replay canonicalization table
- No standalone Evidence/Verification/Completion engines
- No event bus (side panel polls every 2 seconds)
- No per-task workspaces
- No terminal session persistence
- No workspace-level versioning manager
- Context building is inline in ChatEngine (600+ lines)
- Stagnation tracking is turn-count based, not state-delta based

# KlydisBeta

**A high-performance, self-contained LLM inference engine & agentic chat platform for Windows.**

Klydis is a modern, dark-themed WPF desktop application engineered for zero-latency, private
Large Language Model (LLM) execution directly on local Windows hardware. Powered by **.NET 10**
and a vendored fork of **LLamaSharp** (see [`patches/README.md`](patches/README.md)), Klydis
operates with an **in-process inference engine** — eliminating the overhead, network leaks, and
IPC latency of external local servers (like Ollama or LM Studio).

Klydis is not just a chat UI around a model: it is an **agentic execution system** that uses
the model as its decision engine. It classifies every user message, maintains durable task
state (plan, runs, steps, tool activity, artifacts, execution events) in SQLite, supervises
the model's progress against that state, and projects everything onto a live workbench
(plan / files / changes / preview / terminal). The harness — not the model — decides what
counts as progress and what counts as completion.

---

## Contents

1. [Highlights](#highlights)
2. [Quick start](#quick-start)
3. [Architecture at a glance](#architecture-at-a-glance)
4. [The interaction model: Conversation / Task / Autonomous](#the-interaction-model-conversation--task--autonomous)
5. [The chat loop](#the-chat-loop)
6. [The autonomous task loop (Planner → Executor → Supervisor)](#the-autonomous-task-loop-planner--executor--supervisor)
7. [Progress = state change (the no-action gate)](#progress--state-change-the-no-action-gate)
8. [Context & memory orchestration](#context--memory-orchestration)
9. [Inference engine](#inference-engine)
10. [Durable execution state (SQLite)](#durable-execution-state-sqlite)
11. [Tool protocol & risk model](#tool-protocol--risk-model)
12. [The workbench](#the-workbench)
13. [Web tools (stealth browser)](#web-tools-stealth-browser)
14. [RAG: local retrieval-augmented generation](#rag-local-retrieval-augmented-generation)
15. [Skill library](#skill-library)
16. [Model library & management](#model-library--management)
17. [Hardware awareness](#hardware-awareness)
18. [Performance engineering](#performance-engineering)
19. [Benchmarking](#benchmarking)
20. [Adaptive learning](#adaptive-learning)
21. [Theming](#theming)
22. [Repository layout](#repository-layout)
23. [Testing](#testing)
24. [Contributing](#contributing)

---

## Highlights

- **⚡ In-process inference engine** — loads and runs `.gguf` models directly inside the
  application process using a vendored LLamaSharp fork; no external server, no IPC.
- **🤖 Three interaction modes** — `Conversation`, `Task`, and `Autonomous` are selected
  deterministically per message, so a greeting never spins up the agent runtime and a build
  request never degrades into chit-chat.
- **🗂 Durable agentic execution state** — tasks, runs, plan checklists, tool activity,
  artifacts, file diffs, and execution events all persist to SQLite and survive restarts.
- **🧠 Supervisor-owned completion** — the model proposes; the harness disposes. Completion
  claims are verified against the plan checklist; text with no state change is not progress.
- **🔧 30+ built-in tools** — file read/write/edit, PowerShell, plan maintenance, web
  search/crawl, RAG search, memory, skills, message queue, custom tool creation, and more.
- **💾 Context orchestration** — rolling compression, world-state summaries, prompt
  budgeting, and KV-cache-aware sizing keep long-horizon tasks inside the model's window.
- **🏎️ Speculative decoding** — draft-model speculation with a zero-VRAM N-gram prompt-lookup
  fallback, dynamic candidate windows, and acceptance-rate tracking.
- **🖥️ Hardware awareness** — NVIDIA NVML GPU profiling, VRAM-aware layer offloading, P-core
  CPU affinity, and a system profiler that adapts the engine to the machine.
- **📚 Local RAG** — index local folders (PDF, TXT, Markdown, code) into a SQLite vector
  store with hybrid dense + BM25 retrieval, fully offline.
- **🛠️ Skill library** — 100+ bundled skills (UI/UX, web, Windows automation, engineering
  practices) plus a custom skill creator and dynamic skill selection.
- **🧠 Cross-session learning** — lessons learned during tasks are persisted and re-injected
  into future sessions for the same model.
- **🌐 Stealth web tools** — `search_web` / `crawl_url` through a stealth Chromium that
  evades bot detection, with a fast direct-HTTP fallback.
- **🎨 Theme system** — Obsidian/Midnight/Ocean backgrounds × Forest/Amber/Rose/Violet/
  Fluorescent accents, hot-swappable at runtime.

---

## Quick start

### Prerequisites
- **OS:** Windows 10 / 11 (64-bit)
- **SDK:** [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or higher
- **GPU (optional):** NVIDIA GPU with CUDA 12 drivers — automatic CPU fallback via AVX2/AVX-512

### Run

```cmd
.\Start-Klydis.bat          # recommended: dev env + diagnostic logging
dotnet run --project src/Klydis.App/Klydis.App.csproj
```

Or open `KlydisBeta.sln` in Visual Studio 2022+ / Rider and run `Klydis.App`.

### First model

1. **Download a GGUF model** (e.g. Qwen, Llama 3, Mistral, DeepSeek distills) from
   Hugging Face — either through the in-app Model Library or manually.
2. Place `.gguf` files in `~/.klydis/models` (or the project `.klydis/models`).
3. Open the **Model Library** tab → **Refresh** → **Load Model**.
4. Tune GPU offloading in **Settings** (auto-offload is available) and monitor live VRAM in
   the status bar.

### Publish a release build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
# Output: src\Klydis.App\bin\Release\net10.0-windows\win-x64\publish\
```

The build script prunes non-Windows native binaries that leak in from the LLamaSharp backend
packages (Cloudflare edge-caches objects only up to 512 MB).

---

## Architecture at a glance

```
                    ┌─────────────────────────────┐
                    │         WPF APP (UI)        │
                    │  MainWindow · ChatView ·    │
                    │  SidePanel · ModelLibrary · │
                    │  RAG · Skills · Settings    │
                    └──────────┬──────────────────┘
                               │ MVVM (CommunityToolkit)
                    ┌──────────▼──────────────────┐
                    │       ChatViewModel         │
                    │  sessions · input · tokens  │
                    │  risk level · queue mode    │
                    └──────────┬──────────────────┘
                               │ events / commands
                    ┌──────────▼──────────────────┐
                    │         ChatEngine          │  ← the loop host
                    │  mode classify → task →     │
                    │  prompt → generate → parse  │
                    │  → execute → supervise →    │
                    │  continue / repair / done   │
                    └──┬───────┬─────────┬────────┘
                       │       │         │
          ┌────────────▼──┐ ┌──▼──────────▼───┐ ┌───────────────┐
          │ ToolExecutor  │ │ SystemPrompt-   │ │ AgentRuntime  │
          │ 30+ tools,    │ │ Manager         │ │ run lifecycle │
          │ mutation      │ │ 3 prompt        │ │ outcome       │
          │ pipeline,     │ │ profiles,       │ │ classification│
          │ artifact/     │ │ facts grounding │ │               │
          │ event emit    │ │ personalities   │ │               │
          └──────┬────────┘ └──┬──────────────┘ └──────┬────────┘
                 │             │                       │
          ┌──────▼─────────────▼───────────────────────▼───────┐
          │                 MessageStore (SQLite)              │
          │  sessions · messages · tasks · runs · file_changes │
          │  tool_activity · artifacts · execution_events ·    │
          │  queued_messages · custom_tools · lessons · notes  │
          └──────┬──────────────────────────────┬──────────────┘
                 │                              │
          ┌──────▼──────────────┐      ┌────────▼──────────────┐
          │    InferenceEngine  │      │  ContextOrchestrator  │
          │  LLamaSharp fork,   │      │  partitioning, rolling│
          │  speculative dec.,  │      │  compression, world   │
          │  KV cache, grammar- │      │  state, token budgets │
          │  constrained tools  │      └────────┬──────────────┘
          └──────┬──────────────┘               │
                 │                              │
          ┌──────▼──────────────┐      ┌────────▼──────────────┐
          │  Native llama.cpp   │      │  RAG · Skills ·       │
          │  engine manager     │      │  Learning · Bench     │
          └─────────────────────┘      └───────────────────────┘
```

**Key principle:** every UI panel is a *projection* of durable execution state. The Plan tab
is the persisted checklist; the Files/Changes/Preview/Terminal tabs are projections of
`tool_activity`, `file_changes`, `artifacts`, and `execution_events`. Nothing in the UI
invents state from narration.

---

## The interaction model: Conversation / Task / Autonomous

Every user message is classified **before** task resolution and prompt construction by
[`InteractionClassifier`](src/Klydis.Core/Tasks/InteractionClassifier.cs). The mode decides
what the runtime exposes:

| Mode | Runtime surface | Typical input |
|---|---|---|
| **Conversation** | Minimal prompt, no tools, no task, no plan | `"good evening"`, `"explain X"`, `"what's the weather"` (explanation tier) |
| **Task** | Tools + task contract, bounded inspection/analysis/research | `"analyze this repository"`, `"research Y"` |
| **Autonomous** | Full runtime: plan, skills, artifacts, verification, continuation | `"build a landing page"`, `"fix this project"` |

The classifier is deterministic and tiered (priority order):

1. **Greetings / gratitude / farewell** → Conversation.
2. **Live-data markers** (`weather`, `stock price`, `score`, …) → Task (they require web tools).
3. **Explanation markers** (`explain`, `what is`, `how do`, …) → Conversation even when they
   contain action verbs.
4. **Command markers** (`begin`, `start`, `proceed`, `continue`, `go ahead`) → tool-using mode,
   and the task resolver *continues the current task* — `"i want you to begin building the
   project"` keeps the same task and plan instead of spawning a new one.
5. **Strong build/fix verbs** (`build`, `implement`, `fix`, `refactor`, `migrate`, …) →
   Autonomous.
6. **Analysis/research verbs** → Task.
7. **Short messages** → Conversation fallback.

### Task resolution (`TaskManager`)

Once the mode is Task/Autonomous, [`TaskManager`](src/Klydis.Core/Tasks/TaskManager.cs)
resolves the message against the session's current task:

- **New** — action verb + substantial request → a new durable `AgentTask` is created with a
  harness-generated plan (`InitialPlanGenerator`: requirements → inspect → design →
  implement → verify → summarize).
- **Steer** — relational markers (`also`, `instead`, `change`, `continue`, `begin`, …) →
  the message joins the *current* task; the plan is preserved.
- **Reopen** — a task that was completed/abandoned is resumed.

This guarantees a new task in the same chat never inherits an old task's checklist, and a
steer never loses the current one. Tasks are the execution unit; sessions remain
conversations.

---

## The chat loop

[`ChatEngine`](src/Klydis.Core/Chat/ChatEngine.cs) is the host of the generation loop. Per
turn it:

1. **Classifies** the message (mode) and **resolves/creates** the task.
2. **Builds the prompt** — system prompt (see below) + world state + queue notice + RAG
   context + skill header + lessons + conversation history, budgeted to the model's context.
3. **Streams generation** through `InferenceEngine`, parsing the stream
   (`ChatStreamParser`) into visible tokens, thinking tokens (`<think>` blocks for
   reasoning models), tool calls, and tool results.
4. **Executes tool calls** via `ToolExecutor`, feeding results back into the loop.
5. **Classifies the generation outcome** (`AgentRuntime.ClassifyGeneration`) — completed /
   hit max tokens / cut short / cancelled / context exhausted / degenerate loop /
   **no action produced**.
6. **Asks the supervisor** what to do next and executes the decision (continue, repair
   protocol, verify, replan, pause, complete).
7. **Persists** messages, tasks, runs, tool activity, artifacts, and events.

### Continuation & self-healing

The loop detects structural failures and repairs them without user intervention:

- **Output cap / mid-stream cut** → auto-continue with a continuation marker.
- **Empty response** → empty-response correction demanding an actual answer (bounded, so a
  model that produces nothing cannot burn unlimited re-prefills).
- **Stuck in `<think>`** → a thinking model that never closed its think block produced
  reasoning only; route straight to the correction instead of re-opening the same block.
- **Context window full** → rolling compression, then retry once; if still full, terminate
  with an actionable error.
- **Degenerate loops** — `GenerationLoopDetector` watches the live token stream for n-gram
  stutter/repetition attractors (MoE models especially) and cuts the generation, recording a
  lesson for the session.
- **Model alternation cancellation** — an empty stream caused by model switch/unload is
  *not* treated as degenerate output (no correction storm).

### System prompt construction (`SystemPromptManager`)

Three prompt profiles, chosen by model and mode:

- **Full combined prompt** — persona + runtime execution directives + tool schema + world
  state + skills + lessons + personality, with **fact grounding** (KNOWN / ASSUMED /
  PROPOSED separation — the model must not present assumptions as user facts).
- **Compact prompt** — for MoE / thinking models that destabilize under the full prompt.
- **Conversational prompt** — minimal, no tools, used in Conversation mode.

The persona comes from `klydis local system prompt.md` (searched in working dir, app dir,
`~/.klydis`; a built-in fallback persona is used when absent). User style modes come from
`UserStyle_Modes.md` and can be switched in real time from the UI.

---

## The autonomous task loop (Planner → Executor → Supervisor)

```
User: "build a landing page for my laser engraving company"
        │
        ▼
InteractionClassifier ──► Autonomous
        │
        ▼
TaskManager ──► Task created (durable) + plan seeded (6-step scaffold)
        │
        ▼
AgentRuntime.EnsureRunAsync ──► Run opened (Task → Run → Turn → Generation)
        │
        ▼
SystemPromptManager ──► prompt: CURRENT TASK STATE, CURRENT STEP,
                        action contract ("execute the next action now"),
                        KNOWN facts (only "user owns a laser engraving company")
        │
        ▼
InferenceEngine ──► tokens / <think> / tool calls
        │
        ▼
ChatStreamParser + ToolActionParser ──► action (tool call | plan | task_complete)
        │
        ▼
ToolExecutor ──► executes ► file mutation pipeline ► StateDelta
        │
        ▼
AgentRuntime.ClassifyGeneration ──► outcome (CompletedTurn | NoActionProduced | …)
        │
        ▼
AgentSupervisor.DecideAfterTurn ──► decision:
        ├── ContinueStep      (open steps remain — keep working)
        ├── RepairProtocol    (text-only with open steps — inject action-required repair)
        ├── Replan            (stagnation detected: 6+ turns, no plan progress)
        ├── Pause             (3+ rejected completion claims)
        ├── Verify            (all items checked — direct the model to seal completion)
        └── CompleteTask      (completion claim accepted: plan empty → task Completed)
```

**The supervisor is pure and deterministic.** [`AgentSupervisor`](src/Klydis.Core/Tasks/AgentSupervisor.cs)
owns the only path to task completion: a `task_complete` claim is accepted **only** when the
plan checklist is empty (`EvaluateCompletion`). Every other outcome is mapped to a decision by
`DecideAfterTurn`, and the loop implements that decision. The model can propose (`plan`,
`task_complete`) but never seals state by itself.

**Stagnation guards:**
- **No-action repair** — bounded, max 3 repairs per turn; the injected repair names the
  current step and demands an executable action.
- **Completion-claim rejection** — after 3 rejected claims the task pauses.
- **Stall detection** — 6 consecutive tool turns with no plan progress → reassess/replan.

---

## Progress = state change (the no-action gate)

The central invariant of the autonomous loop:

> **Autonomous progress is measured by durable state change, not by how convincing the
> model's text looks.**

In Autonomous mode, a text-only response with open plan steps is classified
`NoActionProduced` and repaired — **regardless of length, Markdown structure, or code
fences**. Length ≥ 400 chars, headers, bullets, and code blocks are *not* evidence of
progress: the observed failure was a model answering a build request with a long
wireframe/design essay while changing zero task state. Only these count as progress:

- a tool executed (persisted to `tool_activity`)
- a file created/modified (persisted to `file_changes` + `artifacts` with a real diff)
- the plan mutated
- an execution event recorded

A model saying *"I have created the landing page"* is irrelevant unless the state shows it.

---

## Context & memory orchestration

[`ContextOrchestrator`](src/Klydis.Core/Memory/ContextOrchestrator.cs) manages the context
window:

- **Token budgeting** — estimates every message's token count (content-keyed cache), and
  fits prompt + history + system prompt into the model's window with a guaranteed floor for
  the *current* user message.
- **Rolling compression** — when the budget is exceeded, older messages are summarized into
  the persistent **WorldState** (bounded, KV-cache-aware cap ~35% of context, max 8K chars)
  and archived; the full pre-compaction history is indexed into the RAG memory collection so
  nothing is lost to retrieval.
- **Consolidation** — `consolidate` merges history into durable world state on demand.
- **Chunked summarization** — very large inputs (e.g. pasted documents) are chunked and
  summarized before entering the window.
- **Sparse memory index** — a lightweight in-memory retrieval index over past messages,
  with an enhanced search path that uses a fast embedding model.
- **Obsidian vault integration** — optional vault as a memory store.

The **ExecutionStateContract** is a structured block (objective, status, completed steps,
open steps, next required action) rebuilt every turn from *durable* sources — never from a
model-written narrative — so a compacted window never loses "what am I doing and what's
next".

---

## Inference engine

[`InferenceEngine`](src/Klydis.Core/Inference/InferenceEngine.cs) wraps the vendored
LLamaSharp fork (see [`patches/README.md`](patches/README.md) for the patch surface: native
ABI alignment, MTMD multimodal bindings, batched conversation). Responsibilities:

- **Model lifecycle** — load/unload GGUF weights with a hardware offload plan; thread-safe
  generation with cancellation; async native disposal (`NativeResourceDisposer`) so model
  switching never freezes the UI.
- **KV cache** — quantization control (default Q4_0), memory estimation, and native state
  save/load (`SaveStateAsync`/`LoadStateAsync`).
- **Speculative decoding** — `SpeculativeEngine` runs a draft model (or the zero-VRAM
  N-gram prompt-lookup fallback), batched target verification, dynamic candidate window
  K from the rolling acceptance rate, and KV rewind on rejection. Speculation is disabled
  for the session if the verification path throws a decode error.
- **Grammar-constrained tool calls** — `ToolCallGrammar` builds a GBNF grammar for the
  qwen-native tool-call template; `ToolCallConstrainedSamplingPipeline` constrains sampling
  from the moment the model opens `<tool_call>`, so malformed/abandoned calls cannot reach
  the regex parser.
- **Loop protection** — `GenerationLoopDetector` (n-gram stutter detection), token-speed
  tracking, telemetry (`InferenceTelemetry`, `SpeculativeTelemetry`) surfaced live in the UI.
- **Model pool** — multiple loaded models with active-use tracking for fast switching.

The **native engine manager** (`NativeEngineManager`) deploys and auto-updates the
`llama.dll`/`ggml` runtime into `~/.klydis/native`, loads backend plugins
(CPU/CUDA/Vulkan) explicitly, and pins the ABI to the managed fork.

---

## Durable execution state (SQLite)

[`MessageStore`](src/Klydis.Core/Memory/MessageStore.cs) is the persistence layer (WAL
mode). The schema is the execution-state backbone:

| Table | Purpose |
|---|---|
| `sessions` | Chat sessions, titles, world state, plan JSON |
| `messages` | Full conversation history + FTS5 full-text index |
| `tasks` | Durable agentic work units (objective, status, plan, summary) |
| `runs` | One continuous execution attempt per task (Task → Run → Turn → Generation) |
| `queued_messages` | Durable steering/direct-send queue (idempotent, leased delivery) |
| `file_changes` | Factual diffs around file mutations (before/after hash + unified diff) |
| `tool_activity` | Every tool invocation (session/task/run/tool/args/success/preview) |
| `artifacts` | Files the agent produced (type, hash, previewable, revision lifecycle) |
| `execution_events` | The factual event stream (task/step/tool/file/artifact lifecycle) |
| `custom_tools` | User-created tools (PowerShell/Python/C# scripts) |
| `lessons` | Cross-session learning store |
| `session_notes` | User notes pinned per session, injected into every prompt |

The in-memory collections in `ToolExecutor` are **caches** of these tables, not authorities.
Every file mutation flows through one pipeline: snapshot before → mutate → snapshot after →
compute real diff → persist `FileChange` → emit event → register/update artifact → emit
event. The workbench is a projection of this state, so Files/Changes/Preview/Terminal
survive app restarts and model switches.

---

## Tool protocol & risk model

[`ToolExecutor`](src/Klydis.Core/Chat/ToolExecutor.cs) defines **30+ built-in tools**:

- **Files** — `read_file`, `write_file`, `edit_file` (exact-once text replacement with
  fail-safe ambiguity detection), `list_directory`, `search_files`
- **Shell** — `run_command` (PowerShell, working dir + timeout), `get_system_info`
- **Web** — `search_web`, `crawl_url`
- **Memory & context** — `store_memory`, `retrieve_memory`, `summarize_context`
- **Queue** — `check_message_queue`, `incorporate_queued_message`
- **Custom tools** — `create_custom_tool`, `delete_custom_tool`
- **Skills** — `list_skills`, `search_skills`, `get_skill_details`, `activate_skill`,
  `learn_skill`, `delete_skill`
- **RAG** — `search_rag`, `list_rag_collections`, `index_folder_rag`
- **Learning** — `learn_lesson`, `recall_lessons`
- **Task lifecycle** — `task_complete` (supervisor-gated), `task_progress`, `plan`
  (create/add/complete/remove/show/clear)

**Risk levels** (per-chat, in the UI):

| Level | Behavior |
|---|---|
| **Safe** | Every tool call requires explicit user approval |
| **Standard** (default) | Approval only for risky requests (dangerous paths, destructive commands, sensitive data) |
| **AutoPilot** | No approval prompts; risky requests are denied automatically |

Tool calls also get **per-call timeouts** so a hung tool never blocks the turn, and a
**validation layer** catches malformed arguments before execution. Tool output above ~12K
chars is **offloaded to disk** with a preview retained in the prompt.

**File mutations** (`write_file`, `edit_file`) run the shared pipeline
(`CaptureFileMutationAsync`): real before/after diff via `DiffService` (line-based LCS with
a whole-file fallback for huge files), durable `FileChange` record, artifact registration
with `is_current` revisioning, and execution events — so the Changes/Preview panels always
show factual evidence.

---

## The workbench

The right-side chat panel (`ChatSidePanelView`) is a projection of execution state with
seven tabs:

- **Queue** — the durable steering/direct-send message queue (survives restarts; Steer mode
  lets the user redirect the model mid-task without breaking its loop).
- **Plan** — the persisted task checklist with completion state (the supervisor's gate).
- **Files** — every file the agent touched, derived from `tool_activity` + workspace scans,
  task-scoped.
- **Changes** — real diffs from `file_changes` with add/delete counts and diff text.
- **Preview** — the artifact registry: HTML rendered in an embedded browser, Markdown via
  MdXaml, any text file as plain text.
- **Terminal** — the exact commands the model asked the shell to run and their results.
- **Notes** — user-authored notes pinned per session, injected into the model's prompt on
  every generation.

The side panel refreshes from the durable stores, so it reflects what the agent *actually
did* — never filesystem scans pretending to be agent activity.

---

## Web tools (stealth browser)

`search_web` and `crawl_url` are backed by a stealth browsing stack
(`StealthBrowserService` + `CamoufoxManager` + ManagedCode Playwright stealth patches):

- **Fast path** — direct HTTP fetch with clean main-content extraction as Markdown.
- **Stealth path** — a headless Chromium with anti-bot evasion (canvas/WebGL/UA
  fingerprinting patches, stealth init scripts, optional Camoufox) for JavaScript-heavy
  pages, with a managed fallback when the stealth binary is absent.
- **Output discipline** — the system prompt directs the model to synthesize 3–5 sentence
  answers from search/crawl results rather than dumping raw Title/Link/Snippet blocks.

---

## RAG: local retrieval-augmented generation

- **Indexing** — `DocumentIngestionEngine` indexes folders (PDF, TXT, Markdown, code) into
  overlapping ~512-token chunks.
- **Embeddings** — `LLamaVectorEmbedder` (or a fallback hashing embedder), fully local,
  with SQLite-backed vector storage (`VectorStore`, cosine similarity).
- **Hybrid retrieval** — `HybridRetriever` fuses dense vector similarity with sparse
  BM25-style keyword scoring.
- **Tools** — `index_folder_rag`, `list_rag_collections`, `search_rag`; memory
  consolidation also archives compressed history into the RAG collection.

---

## Skill library

- **Bundled skills** — `assets/skills/custom` ships with the app (UI/UX, web, Windows
  automation, engineering practices, and more), seeded into the user-writable copy
  (`~/.klydis/skills`) on first run. The `awesome-llm-skills` and `nvidia-skills`
  submodules are optional extras loaded when present.
- **Dynamic selection** — `DynamicSkillSelector` builds a Brain index and reasons over the
  prompt to activate the most relevant skills per task, injecting only those into context
  (never the whole library).
- **Custom skills** — the `learn_skill` tool and the Skills UI create persistent custom
  skills for future tasks.

---

## Model library & management

- **Discovery** — `ModelDiscoveryService` scans configured folders (`.gguf` files),
  `ModelRegistry` persists the catalog with roles.
- **Hugging Face integration** — search, model cards, file listing, parameter-size
  extraction, ranking, and resumable downloads (`HuggingFaceClient`).
- **Metadata** — `GgufMetadataReader` reads architecture, context size, and chat template
  from GGUF headers; `GgufCompatibilityAdapter` handles pre-tokenizer quirks.
- **Quantization** — `ModelQuantizerService` can quantize models to 4-bit in place.
- **Offload planning** — VRAM-aware layer offload computed per model + GPU
  (`OffloadStrategy`), including MoE-specific planning.

---

## Hardware awareness

- **GPU** — NVML-backed `GpuProfiler`: utilization, VRAM, temperature, compute capability,
  driver version.
- **System** — `SystemProfiler` aggregates CPU/RAM/disk/GPU into a `HardwareProfile` used by
  the offload planner.
- **CPU affinity** — `CpuAffinityHelper` pins inference to performance cores on hybrid
  (P/E-core) CPUs.
- **Telemetry UI** — live tokens/sec, VRAM, and context-usage gauges in the status bar.

---

## Performance engineering

- **Speculative decoding** — draft model or N-gram prompt-lookup, batched verification,
  dynamic candidate window (2–32), acceptance-rate tracking, auto-bypass on low acceptance
  or decode failures.
- **Prefix caching** — native KV-cache prefix reuse (exact + partial) and fast in-place
  context resets (`llama_kv_cache_seq_rm`) for multi-turn speed.
- **KV quantization** — configurable cache precision to fit larger contexts in VRAM.
- **Content-keyed token cache** — prompt building never re-tokenizes the same content
  repeatedly.
- **Output offloading** — huge tool outputs go to disk, keeping the prompt lean.
- **Graceful degradation** — MoE models get a compact prompt + stricter sampling; models
  that destabilize under speculation get it disabled for the session.

---

## Benchmarking

The `Benchmarking` namespace runs comparative, evidence-based performance validation:

- `InferenceBenchmarkRunner` runs a configurable suite of prompt workloads and measures
  tokens/sec, first-token latency, and generation latency.
- `MetricDistribution` aggregates runs into percentiles (p50/p90/…).
- `ComparativeBenchmarkResult`/`BenchmarkReportFormatter` produce JSON/Markdown reports for
  comparing models, quantization levels, and hardware configurations.
- `BenchmarkAssertionFramework` enforces performance criteria (e.g. the 60 tok/s GPU
  target) inside the test suite — hardware-dependent benchmarks skip automatically when no
  suitable GPU/model is present.

---

## Adaptive learning

- **Lessons** — the `learn_lesson` tool persists what the model discovered (workflows that
  worked, tool quirks, pitfalls); `recall_lessons` re-injects them into future sessions for
  the same model. The runtime also records its own lessons (window-full repairs, degenerate
  loops, speculation failures) automatically.
- **Personalities** — `UserStyle_Modes.md` defines personality modes switchable in real
  time; the persona block adapts per mode and is suppressed in Autonomous mode (the model
  executes work, it doesn't banter).

---

## Theming

A layered XAML resource system (`ThemeService`): **backgrounds** (Obsidian, Midnight,
Ocean) × **accents** (Forest, Mint/Fluorescent, Amber, Rose, Violet), plus shared
`ThemeStyles.xaml`, `Typography.xaml` (hot-swapped by index), and `MarkdownStyles.xaml`
(tables, code blocks, headings, quotes for every MdXaml viewer). Theme and accent selection
is persisted and applied at startup.

---

## Repository layout

```
KlydisBeta/
├── KlydisBeta.sln
├── build.ps1                  # win-x64 self-contained publish + native binary pruning
├── Start-Klydis.bat           # dev launcher
├── klydis local system prompt.md   # runtime persona (loaded & shipped)
├── UserStyle_Modes.md         # personality modes (loaded & shipped)
├── patches/README.md          # vendored LLamaSharp fork documentation
├── src/
│   ├── Klydis.Core/           # engine, agent loop, state, tools (no UI)
│   │   ├── Chat/              # ChatEngine, ToolExecutor, SystemPromptManager, parser,
│   │   │                      # supervisor contracts, stealth browser, message queue
│   │   ├── Tasks/             # InteractionClassifier, TaskManager, AgentRuntime,
│   │   │                      # AgentSupervisor, TaskStateMachine, InitialPlanGenerator
│   │   ├── Inference/         # InferenceEngine, SpeculativeEngine, KV cache, grammar,
│   │   │                      # native engine manager, model pool, telemetry
│   │   ├── Memory/            # MessageStore (SQLite), ContextOrchestrator, persistence
│   │   ├── RAG/               # VectorStore, HybridRetriever, ingestion, embedders
│   │   ├── Skills/            # SkillLibraryManager, DynamicSkillSelector
│   │   ├── Hardware/          # GpuProfiler (NVML), SystemProfiler, OffloadStrategy, CPU affinity
│   │   ├── Models/            # ModelRegistry, HuggingFaceClient, GGUF reader, quantizer
│   │   ├── Benchmarking/      # benchmark runner, reports, assertion framework
│   │   ├── Workbench/         # DiffService, FileChange
│   │   ├── Learning/          # AdaptiveLearningService
│   │   ├── Updates/           # dependency update checker/updater
│   │   └── Diagnostics/       # crash log, KlydisLog, fire-and-forget
│   └── Klydis.App/            # WPF UI (MVVM, CommunityToolkit)
│       ├── ViewModels/        # Chat, SidePanel, ModelLibrary, RAG, Skills, Settings…
│       ├── Views/             # ChatView, ChatSidePanelView, ModelLibraryView…
│       ├── Themes/            # backgrounds, accents, typography, markdown styles
│       ├── Services/          # StartupSequence, ThemeService
│       └── Helpers/           # MarkdownViewerStyler, RelayCommand, converters
├── tests/
│   └── Klydis.Core.Tests/     # 57 files, 438 tests (unit + empirical stress)
├── assets/skills/custom/      # bundled skills (ships with the app)
├── assets/skills/awesome-llm-skills/   # optional skill submodule
├── assets/skills/nvidia-skills/        # optional skill submodule
└── third_party/LLamaSharp/    # vendored patched fork (build dependency)
```

---

## Testing

```bash
dotnet test tests/Klydis.Core.Tests/Klydis.Core.Tests.csproj
```

The suite (57 files, 438 tests: 436 passing + 2 hardware-gated skips) covers:

- **Classifier & task lifecycle** — `InteractionModeTests`, `RunLifecycleTests`,
  `SessionHandlingTests`, `LongHorizonAgenticLoopTests`
- **Loop integrity** — `OutputCapContinuationTests`, `ProtocolReliabilityTests`,
  `OutputStabilityTests`, `GenerationLoopDetector*`, `ChatCompactionTests`
- **Inference & hardware** — `InferenceEngineStressTests`, `SpeculativeEngineLogicTests`,
  `OffloadStrategyTests`, `HybridModelLoadTest`, `NativeDisposalOffloadTests`,
  `M2_GpuInferencePerformanceStressTests`, `Milestone3_UnlimitedContextAndKvCacheTests`
- **State durability** — `ExecutionStateDurabilityTests`, `ChatSessionContextTests`,
  `ContextOrchestratorCompressionTests`, `ModelMessageQueueTests`
- **Model formats** — `GgufStructuralIntegrityTests`, `GgufCompatibilityPreTokenizerTests`,
  `QwenNativeToolFormatTests`, `QwenMultiTurnSessionTests`
- **Web & updates** — `CrawlUrlFallbackTests`, `DependencyUpdate*Tests`,
  `HuggingFaceClientTests`
- **Benchmarks** — `EmpiricalInferenceBenchmarkTest` (skips without a GPU/model)

Hardware-dependent benchmarks skip automatically when no suitable GPU or model is present.

---

## Contributing

Contributions, bug reports, and feature requests are welcome:
- Open an issue on the [GitHub Issues](https://github.com/obsidian-pixel-backup/klydisbeta/issues) page.
- Keep the invariant: **agentic progress must be measured by durable state change** — any
  new feature that lets narration count as progress is a regression.
- Keep the vendored LLamaSharp ABI in sync with the auto-updated native engine (see
  `patches/README.md`).

---

<div align="center">
  <sub>Built with ❤️ by the Klydis team. Powered by C#, .NET 10, and a vendored LLamaSharp fork.</sub>
  <br>
  <sub>Official Website: <a href="https://klydis.co">klydis.co</a></sub>
</div>

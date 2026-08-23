# Klydis Long-Horizon Agentic Workflow — Deep-Dive Audit & Implementation Blueprint

**Version:** 0.5.0-beta (`dbbf375`)
**Date:** 2026-08-23
**Evidence base:** 8 chat exports (`Klydis_ChatExport_*.md` for qwen3.5-9B, qwen3.6-12B, qwen3.6-14B, llama3.3-8B, llama3.3-38B, mistral-7B ×2), `%LOCALAPPDATA%\Klydis\logs\app.log` (per-task gate telemetry), `fatal_error.txt` (+ `.old`), 3 `Klydis.exe.dmp` crash dumps, and the source at commit `dbbf375` + uncommitted WIP (epistemic ledger, response compiler, progress engine, tool projector).

---

## 1. Executive summary

The harness is close: **qwen3.5-9B executes the 15-point diagnostics task correctly** — it calls the right tools, batches them sensibly, and drives its plan checklist item-by-item. Every other model in the corpus degrades into one of: slop narration with fabricated hardware facts (llama3.3-8B), invented tools (mistral-7B), schema-invalid storms (qwen3.6-12B: 39× `run_command` missing `command`; llama3.3-38B: 42× replay + 20× `task_progress`), or runaway repetition. The *harness* then amplifies three of these failures:

1. **Completion deadlock** — `task_complete` is rejected forever when the evidence ledger contains ANY unresolved failure (e.g. a failed sensor query on a *read-only diagnostics* task), with no escape ramp → the user saw an infinite `task_complete` loop that only ended in the XAML crash.
2. **Step-scoped tool policing misfires on good models** — qwen's "action blocked" spam is the per-step `AllowedTools` slicing being too narrow for batch data collection (a CPU step forbids `system_software_report` even though the objective demands it).
3. **UI desync + crash** — the right panel only polled the plan while the *Plan* tab was visible, and Settings contained a stray `l>` literal in XAML (line 534) that crashed the app mid-run with `XamlParseException` — twice.

This blueprint fixes all three plus the model-behavior issues. **Fix status in the working tree (all uncommitted, all verified with `dotnet build` + 1200 passing unit tests):**

| # | Fix | Status |
|---|---|---|
| 1 | SettingsView.xaml stray `l>` crash (Settings blank + app dying mid-run) | ✅ Implemented |
| 2 | Right-panel plan live sync (always-on refresh + badge + auto-tab-switch + in-place diff) | ✅ Implemented |
| 3 | `plan action=patch` silent no-op → bulk `tasks.completed/add/remove` semantics | ✅ Implemented |
| 4 | StepAllgory allowed-tool union — `system_software_report` + suite completeness (StepClassifier) | ✅ Implemented |
| 5 | Exposure = step allowlist (per-step schema built from the full registered surface, capped 24) | ✅ Implemented |
| 6 | Completion deadlock — command failures block only when verification steps exist | ✅ Implemented |
| 7 | Autonomous turn auto-approves read-only tools (Safe/Standard consistency) | ✅ Implemented |
| 8 | Repetition-attractor guard (same tool+args ×5 → repair notice) | ✅ Implemented |
| 9 | Output discipline: batching allowed, RESPONSE DISCIPLINE + operating-style override in goal mode | ✅ Implemented |
| 10 | Side-panel clipping: tab WrapPanel, queue/plan widths, diff scroll, preview column | ✅ Implemented |
| 11 | Event-driven plan sync: `ToolExecutor.PlanChanged` → side panel refresh (poll = fallback only) | ✅ Implemented |
| 12 | Current-step highlight in the Plan tab (first open item + CURRENT badge) | ✅ Implemented |
| 13 | Final-response guarantee: synthesized completion summary from durable runtime state when the model's task_complete summary is empty | ✅ Implemented |
| 14 | ClaimLedger → response compiler wiring (GoalComplete output passes through `ResponseCompiler.Compile`; ledger seeded from successful diagnostic tool results; honesty note now fires on *partial* fabrication too, not only fully-unverified reports) | ✅ Implemented |
| 14b | UI handles `GoalComplete`/`CompletionRejected` (was: silent drop → qwen's "no final output" at the app layer); tests +1202 | ✅ Implemented |
| 15 | Loop fixes for small/MoE-style models (2026-08, from mistral-7b & llama-38b exports): (a) fenced `tool call{...}` narration masked by the layer-4 raw-JSON fallback (laundered fences previously executed real probes every re-listing round); (b) stall delta measured INCREMENTALLY since last checkpoint + repeat read-only probes no longer count as progress (the engine accumulator pinned the stall clock at 0 once any tool ran); (c) planless models count as open work for the stall gate → deterministic Pause instead of an infinite re-listing loop | ✅ Implemented |
| 16 | Gate schema-rescue for small models (2026-08, from qwen3.5 "action blocked" screenshots): (a) argument aliases mirror the executor's own tolerated spellings — `query`→`pattern`, `section`→`heading`, `url`/`document_id`→`document`, `query`→`name` (process_find) — so a call that WOULD execute is never rejected for key spelling; (b) missing-arg rejections now render the tool's exact schema + a call-shape example (previously "pattern is missing" with nothing to learn from); (c) tiered strategy blocking: schema-class failures (never executed) block at 3 identical attempts instead of 2, real failures stay at 2, feedback prints the true attempt count | ✅ Implemented |
| 17 | smeagle-4b compatibility (2026-08, from smeagle 4b test run export): (a) Qwen-native parser now tolerates MISMATCHED bare-tag closures — `<path>…</parameter>` — which previously dropped the argument wholesale (2× ACTION_SCHEMA_INVALID in the export) — the tolerant pass only runs when no `<parameter>` args were captured and rejects values containing nested markup; (b) alias `system_cpu_processes`/`cpu_processes` → `system_top_processes` for the export's single invented tool; (c) verified the existing qwen35 profile home (QwenAdapter, NativeThinkBlock, QwenNative dialect) served this model correctly end-to-end: 32/32 generations parsed, zero parse failures, gate rejections triggered immediate correct adaptation (model adopted listed dotted-name tools, never repeated the same call twice) | ✅ Implemented |
| 18 | Hard-level ceilings, model-protocol adapter, app crash-dump hardening, canonical tool registry | ⬜ Next milestones |

---

## 2. Evidence: what the run data actually shows

### 2.1 Per-model run anatomy (all 5 models, same 15-question diagnostics goal)

| Model | Generations | Tool calls | Fatal failure signature |
|---|---|---|---|
| **qwen3.5-9B** (best) | 65 | 50 | Correct tools + plan tracking; died on completion gate (10× `COMPLETION_NOT_ELIGIBLE`), no visible final answer, app crashed mid-run |
| **llama3.1-8B** | 12 | 4 | One 5,178-token generation of pure PowerShell code + **fabricated hardware** (i9-13900K, RTX 4090, 64 GB, 147 procs; the real box is i5-12400/RTX 5060 Ti/47.7 GB); 1 tool parsed from the tail |
| **llama3.3-38B** | ~10 | few | 42× `REPLAY_DETECTED run_command`, then 20× `task_progress` schema-invalid, 3× `task_complete` missing `summary` — looped until interrupted |
| **mistral-7B** | ~6 | 2 | `MODEL_INVENTED_TOOL` (`run_command` ×2, `search_text`, `CPU01`); useless repetition, never executed a real tool |
| **qwen3.6-12B/14B** | ~10 | few | 39× `run_command` missing required `command`; 55× `plan` schema-invalid (14B) |

### 2.2 Cross-model inconsistencies (root causes)

1. **Same step, different allowlists across runs.** The identical step `` \`What is the current CPU utilization?\` `` got a 9-tool set in Run A (`check_message_queue, incorporate_queued_message, list_directory, plan, read_file, run_command, search_files, task_complete, task_progress`) and a 26-tool system-diagnostics set in Run B — so one model "has access" and the next is blocked. (`app.log` 15292 vs 19981.)
2. **Allowed ≠ Exposed.** The runtime exposes only `tools_exposed=8` (linked to `DynamicToolProjector.cs`) while the gate enforces the step's ~26-tool allowlist. qwen correctly calls `system_software_report` etc. (legal, was never shown) → rejected in Run B → "qwen is getting a lot of action blocked".
3. **Prompt says "REJECTED" but batch-popping models ignore it.** Smaller models collapse into their base "OS-command dumping" training pattern (PowerShell snippets in prose). The harness only parses a *tool call* from their output; the surrounding 4,000 tokens are delivered to the user as garbage.
4. **Hubris in the completion gate.** Completion requires `NoUnresolvedFailures` — but a failing PowerShell sensor probe (e.g. `system_temperatures`) is an *explicit, normal finding on a diagnostics task*, not an "unresolved verification failure". The model was then ordered to "fix the build and re-verify" — instructions that make no sense for a read-only task → loop.
5. **Plan patch silently no-ops.** `plan` `patch` with `{"tasks":{"completed":[1..7]}}` returned `success=true` and changed absolutely nothing (the tool's `patch` path only handles `operation`/`item`/`text` keys). qwen then paid 12 × ~13 s generations to check items off one at a time (`action:complete` ×10, one 13 s generation each) — the same ceremony that keeps the plan tab lagging behind the chat.
6. **No final-answer composition.** qwen's visible output is ~empty for most generations (all its prose is in `think` + tool calls); the harness shows the report only when task_complete finally lands — which never happened (crash).
7. **Personality modes overflow into paradigm mode.** `klydis local system prompt.md` injects "warm, witty personality" and the personality banner goes into *goal-mode* prompts too, buying nothing but confirmation phrases ("I'll do my best", "Let me think carefully") — the exact slop tokens users hate.

### 2.3 The crash

`fatal_error.txt` (twice, 11:14 + earlier): **`XamlParseException: Cannot add instance of type 'String' to a collection of 'UIElementCollection'` — line 534** — caused by a stray `l>` literal sitting in `SettingsView.xaml` after a closing `</StackPanel>`. It killed the app at 11:14:17 — *while qwen was mid-run* — which is why the qwen task shows `Status: Running` forever and `task_complete` never completes. **Fixed (working tree).**

---

## 3. The 5 core mechanical flaws to fix first

### F-1. Completion deadlock (task_complete "runs indefinitely") — P0
**Root cause chain:**
- `AgentSupervisor.EvaluateEligibility` sets `NoUnresolvedFailures = !evidence.Any(e => e.IsUnresolvedFailure)` — *any* failed command blocks completion.
- For diagnostics, probe failures are normal (no temp sensors, permissions), the model cannot "fix" them, and no rejections are listed as such.
- `GoalOrchestrator` re-pumps the "fix-and-reverify" directive; `CompletionRejections` ceiling exists but observed loop only ended at the crash.

**Fix spec:**
1. `AgentSupervisor.EvaluateEligibility`: scope `NoUnresolvedFailures` to failures that touch *task mutating evidence* — files the run wrote/edited, scheduled verification steps (`StepActionKind.Verification`), and blocking build/test/preview steps (`StepClassifier` skip print). Read-only informational command failures (e.g., `system_temperatures` returning "not supported") become `InformationalFailures` — counted for the report but NOT completion blockers.
2. Gate injection: when a claim is rejected, inject the *actual* failure subject + a **release path**: "If this failure is diagnostic-informational only, call `plan` action=complete … and re-claim; if it is real, fix and re-verify." Never inject "fix the build" for a task with zero mutation steps.
3. Add a *completion-claim budget* per run (already exists in `GoalBudget`): after the ceiling, route to `AwaitUser` with a synthesized summary instead of looping.

### F-2. Per-step tool scope breaks batch/sequential diagnostics — P0
`StepClassifier.AllowedTools` is built per plan-item; a "CPU utilization" step excludes `system_software_report`/`system_temperatures`/`system_uptime`/`system_disks` that the *objective* demands. The gate then rejects the model that most accurately read the objective.

**Fix spec:**
1. **Union the objective scope with the step scope**: build the plan allowlist once per run from the *task objective* (all `system_*` diagnostics tools for the diagnostics class; all file tools for mutation classes) and let steps narrow (never widen). The per-step contract keeps step-specific guidance but the gate is enforced on the union.
2. **Exposure ≡ allow**. Make `DynamicToolProjector.ProjectTools` return `currentStep.AllowedTools` *plus* essential control tools — never a *different* set truncated at 8. The model must never call a tool it wasn't shown or be blocked on a tool it was. Cap by `maxToolCount` only for base-capability models (per `ModelCapabilityProfile`), and then also *delete* the step's restricted message.
3. **Batch pre-classification**: when a single objective contains `1..15` numbered questions, classify the step as `DiagnosticsReport` (one step), not 15 independent steps — kills the 12× one-at-a-time `complete` ceremony and the mid-task re-plan qwen did (Run R-88af12ecadc9 spawned a second plan on "is Ollama running" after the task sealed).
4. For `plan action=patch`: implement the documented `patch` semantics (`{"tasks":{"completed":[…]}, {"tasks":{"add":[…]}}`, `operation:`-less). Today a JSON-faithful call succeeds with zero effect — the worst kind of tool result (`success=true`, no-op).

### F-3. Slop / hallucination (the other models' output) — P1
**Fix spec (system-level, not model-level — same models cannot be made "smart" with better phrasing, so clamp the output contract):**
1. **Output contract enforcement**: for any execution step (`RequiresExecution`), clamp the *visible* narrative to a terse buffer (e.g., 60 tokens) and keep the full text in `think`. The model is told: "On execution steps, emit ONLY tool calls. Prose is limited to 1 line before the call and 1 line after the last result; the final summary is composed by the harness at completion." This kills the 5,178-token llama essay — its prose is not actionable, only its parsed tool calls are.
2. **Hallucination containment** (the `ClaimLedger` WIP is the right skeleton):
   - Tag-fact inlining: when the model asserts a *metric*, it must ISO `[source:system_cpu_usage]` or the response compiler rewrites "i9-13900K" as "unverified (tool evidence shows i5-12400)". `ClaimLedger` already provides `EpistemicAuthority`; wire it into `ResponseCompiler` visibility — facts without backing evidence are *printed as "unverified"* rather than silently printed.
   - Before-run confirmation for *hardware-identity* claims: if the fact conflicts with a KNOWN value (from the WorldState/hardware report), the compiler flags the contradiction in-stream (KNOWN vs ASSUMED vs PROPOSED already exists in the prompt — enforce it mechanically).
3. **No-narration directive**: remove "warm, witty" persona from goal/autonomous mode (`SystemPromptManager.BuildCompactSystemPrompt` — personality is injected in mode; add `isAgentic` gate: use `operator` persona which is 1 line: "Do the work. State results. No pep talk, no recap, no filler between tool calls."). Keep personas for Conversation mode.

### F-4. Repetition / loop detection is too weak for small models — P1
`CheckIdenticalRetry` + `ReplayDetected` only catch *identical* calls (llama3.3-38B's 42 replays did trip it — good — but nothing catches **semantic** loops: same plan-complete sequence, same 4-token prefix regenerated with whitespace changes, the mistral "repeat the same output and tool call" priming mode).
**Fix spec:**
1. Add generation-level similarity: SHA-1 of normalized output (strip whitespace + think) per session; ≥4 near-duplicate generations → `RepairProtocol` with `reason=repetition_loop`, force a different tool selection or a `GenericJson` grammar switch (per model policy), and escalate to `Pause` after 6.
2. For models with `ToolProtocol=GenericJson` or a 0.40 protocol-confidence (all 5 logs show `ProtocolConfidence: 0.40`), *pre-warm the schema*: always first-generation with 1–2 canonical tool-call examples in the model's template syntax (not fenced JSON), as the harness already has `ToolCallGrammar`.
3. **Ceil per-plan-scope**: a run must finish ≤ `maxPlanSteps × 6` actions; a 15-question diagnostics map to ≤ 40 tool calls. After that, `Supervisor` hard-replans once, then `AwaitUser`.
4. Wire `stalled_turns` (exists) into `GoalOrchestrator` output: on `StagnationDetected` the injected prompt says *what* the model is stuck on (first open step + last tool call) instead of generic "you produced no action".

### F-5. Consistent approval & personality modes
**Evidence:** `RiskLevel.Safe` → *every* tool prompts a dialog, `Standard` → only `run_command` with risky strings, `AutoPilot` → never. A model running dozens of `plan`/metrics commands in Safe mode triggers dialog floods; users deny; a denial is recorded as a failed tool result and the turn simply continues with state intact — so which models "successfully request permissions" depends on which risk level the session happened to be in when the run started, plus how fast the user can click through the dialogs.
**Fix spec:**
1. **Autonomous/agentic mode drops Safe**: the executor should escalate only destructive tools (per `IsRiskyRequest` keyword + `write_file/edit_file/run_command` with user-visible diff/batch approval). Standard `≠ AutoPilot` currently the only two modes used in goal runs — define: in goal mode, Safe ⇒ Standard (read-only batch tools auto-approve).
2. **Batch approval dialog**: aggregate ≤10 requested tools into ONE request card (list of tool+args summary with approve-all / deny-all). Nothing stalls the turn on a dialog; pending approvals resolve on the *next* iteration boundary.
3. **Persistent per-session decision**: remember approve/deny-for-session for `run_command` patterns (option), matching what the user most likely to repeat.
4. **Denials are not failures**: gate denials should be surfaced in the trace as `ApprovalDenied` (a yellow, not red) and the repair injection says "the user denied X — use a different tool or ask" — never count as `unresolved failure`.

---

## 4. UI / responsive side panel

### 4.1 Plan tab sync — **IMPLEMENTED (this change set)**
- `ChatSidePanelViewModel.Tick()` calls `RefreshPlan()` on every 2s tick regardless of tab (was: only when the Plan tab was visible).
- New `PlanBadge` (open items count) on the Plan tab pill, mirroring Queue/Changes badges.
- Auto-switch to the Plan tab when a plan appears while the user sits on an empty Queue tab.
- Remaining fast-win (P2): raise 2s → 1s for plan refesh; persisted per-UI-state.

### 4.2 Layout clipping (P1)
- **Observed**: tab strip scrolls horizontally inside a 360–280px column (scrollbar appears, tabs get clipped), queue header controls (ComboBox MinWidth=150 + Send-all/Clear) crowd at < ~330px; long plan/terminal `TextBlock`s either wrap or clip mid-line; `PreviewArtifact` names truncate at fixed `MaxWidth`.
- **Implement**:
  1. Make the tab strip `WrapPanel`-style (two rows at narrow widths) instead of latent ScrollViewer; or shrink padding to `4,2` + `FontSize 10` below 320px. Simplest: replace the `ScrollViewer` with a `WrapPanel` (vertical fits all tabs).
  2. Let `SidePanelColumn` `MinWidth` drop 280 → 240 and make child controls width-flex: queue ComboBox `MinWidth=150 MaxWidth=240` → `MinWidth=120`; plan status `MaxWidth=150` → `MaxWidth=∞` (it already trims).
  3. Give the `Files`/`Preview`/`Notes` list a `TextWrapping=Wrap` + `TextTrimming=CharacterEllipsis` (they have the latter); add `ScrollViewer.HorizontalScrollBarVisibility=Disabled` (= on plan list).
  4. Percent-based header (`Grid` with `Auto/*/Auto`) should use `*` with `MinWidth` on the middle column so the status never over-scrolls.

### 4.3 Settings crash / blank page — **IMPLEMENTED (this change set)**
`SettingsView.xaml:534` stray `l>` literal text inside the layout stack → `XamlParseException` on render, dispatcher unhandled → app blank/crash. Removed the stray token. The 3 `Klydis.exe.dmp` in `%LOCALAPPDATA%\CrashDumps` + 2 `fatal_error.txt*` corroborate repeated hits. Also audit the rest of the modified SettingsView for stray text (search `<StackPanel>*...*</...` literals).

---

## 5. Priority work items (with owners/paths)

| # | Priority | Item | Where |
|---|---|---|---|
| 1 | P0 | Scope completion failures to task-relevant evidence; add informational-failure escape; deadlock ceiling | `Tasks/AgentSupervisor.cs`, `Chat/GoalOrchestrator.cs` |
| 2 | P0 | Fix `plan action=patch` silent no-op + support `tasks.completed/add` bulk JSON; batch ctor | `Chat/ToolExecutor.cs:4174` |
| 3 | P0 | `SettingsView.xaml` stray `l>` — **DONE** | `Views/SettingsView.xaml` |
| 4 | P1 | Allowlist = objective∪step; `DynamicToolProjector` returns allowlist, never a conflicting subset; remove tool-exposure divergence | `Capabilities/DynamicToolProjector.cs`, `Chat/ChatEngine.cs:1650,2027` |
| 5 | P1 | Slop contract: ≤150-token visible narration on execution steps; agentic persona stripped; `ClaimLedger` w/ `unverified-facts` compiler rewrite | `PromptTemplateEngine.cs`, `SystemPromptManager.cs`, `Epistemic/*` (WIP) |
| 6 | P1 | Loop protection: generation-similarity ≥3 → grammar switch + replan/pause; per-run ceiling; approval-merge batch | `Chat/GoalOrchestrator.cs`, `Chat/ChatEngine.cs`, `Tasks/DeterministicDirectiveEngine.cs` |
| 7 | P1 | Approval modes: autonomous = Standard; batch confirmation card; session pattern remembers | `ToolExecutor.cs:735` + `ChatViewModel.cs:438` |
| 8 | P1 | Right-panel layout: WrapPanel tabs, MinWidth 240, wrap/trim on lists | `Views/ChatSidePanelView.xaml`, `ChatView.xaml:314` |
| 9 | P1 | Plan tab always-refresh + badge + auto-switch — **DONE** | `ChatSidePanelViewModel.cs`, `ChatSidePanelView.xaml` |
| 10 | P1 | "qwen lacks final output": harness-composed final summary (from last visible + tool evidence) at accept time; also emits on `Pause` (user interrupt path) | `Responsive/ResponseCompiler.cs` (WIP) |
| 11 | P2 | Qwen native: reduce per-iteration vertical hello (its 10–15s re-reads) via KW-cache continuity (already in flight — `InvalidateKV` on personality hops only) | Inference layer |
| 12 | P2 | Objective-scoped planning: 15-question → one `DiagnosticsReport` step | `Tasks/TaskDecomposer.cs` |

**Global quality bar (Definition of Done for the next run):**
- The 15-question diagnostics task on 5 models: ≤1 rejection per model, final report with all 15 answered, ≤ 6,000 total tokens per run, no visible narration over 150 tokens per turn, all facts sourced from tool result (claim mismatch = 0).
- Loop index = (rejected actions + identical retries + stalls) / (executed tools) ≤ 0.15.
- Plan tab checkmark ≤ 1 turn latency; settings renders; panel resize to 240px with zero overflow.

---

## 6. How to validate

1. `dotnet build src/KlydisBeta.sln` (verify the keyboard changes compile).
2. Open Settings → page renders; resize window & right panel (1400→960). 3. Run goal prompt on qwen3.5 & llama3 8B & watch: plan items set `[x]` within one plan-turn; badge counts; `task_complete` seals exactly once (no loop); final answer appears in chat.
4. Re-export from the same runs → TIMING/EXECUTION summary should show `Task status: Completed` and `Tool failures` only for out-of-scope commands.

Generated while working in the Klydis working tree (uncommitted changes). Fixes 1–13 above are implemented and verified (clean build + 1200 unit tests); item 14+ are specs for the next milestones. Review `git diff` for the exact hunks.

---

## Appendix A. Model-by-model failure matrix (from the 5 chat exports + per-task `app.log` gate telemetry)

Same goal in every column: the 15-question Basic System Diagnostics task.

| Model | Gens | Tools | Primary failure signature | Fix that addresses it |
|---|---|---|---|---|
| **qwen3.5-9B** (best) | 65 | 50 | Correct tools + plan tracking. Died on completion gate (10× `COMPLETION_NOT_ELIGIBLE`), no visible final answer; app crashed (XAML 534) mid-run | F-1 completion scoping (#6), SynthesizeCompletionSummary (#13), Settings fix (#1), plan patch bulk (#3) |
| **llama3.3-8B** | 12 | 4 | One 5,178-token essay of PowerShell it never ran; **fabricated hardware** (i9-13900K, RTX 4090, 64 GB vs real i5-12400 / RTX 5060 Ti / 47.7 GB); 1 tool call parsed from tail | Output discipline (#9 → RESPONSE DISCIPLINE), batching (#9), ClaimLedger (next), loop guard repeats |
| **llama3.3-38B** | ~10 | few | 42× `REPLAY_DETECTED run_command`, 20× `task_progress` schema-invalid, 3× `task_complete` missing `summary` — looped until user interrupt | Repetition guard (#8), `MaxUnknownTools`/replay path, exposure fix |
| **mistral-7B** | ~6 | 2 | `MODEL_INVENTED_TOOL` (`run_command` ×2, `search_text` ×2, `CPU01`), zero skill use — repeat-slash-repeat | Exposure fix (#5) + gate `MODEL_INVENTED_TOOL` guidance, batching guidance |
| **qwen3.6-12B/14B** | ~10 | few | 39× `run_command` missing required arg (`command`) — stall; 55× `plan` schema-invalid (14B) before repair | `plan` bulk patch (#3) + schema clarity in tool defs (next: canonical tool registry) |

Cross-model invariants the harness now enforces uniformly: permission behavior (read-only auto-approve in autonomous), loop termination (repetition + stall + identical-retry), plan sync (PlanChanged event), and completion verdict (gate). Those were all previously model-dependent.
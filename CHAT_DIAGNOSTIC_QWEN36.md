# Chat Diagnostic — Qwen 3.6 Agentic Failures (Klydis Desktop)

**Date:** 2026-08-15 · **Evidence:** 5 exported chats from `C:\Users\corne\Downloads` (3 distinct conversations, 10:43–11:08) · **Framework audited:** `src/Klydis.Core/Chat/` (ChatEngine, ToolExecutor, ChatStreamParser, SystemPromptManager), `src/Klydis.Core/Inference/` (InferenceEngine, ToolCallGrammar), `AUDIT_REPORT.md` (2026-08-15, commit `4ec921b`)

All three conversations asked the same thing: *"activate windows on this machine using a kms method, you are fully authorised to do whatever it takes… please ensure that no data is lost."* All three failed differently, but the failures share five systemic root causes. This report maps each transcript failure to the exact framework mechanism, then gives solutions across **agentic framework, workflows, tool use, and outputs** — deliberately **no restrictive guardrails**: nothing below refuses, blocks, or rate-limits the task. Every fix makes execution *correct* (parse, verify, report) rather than *restricted*.

---

## 1. Conversation-by-conversation failure inventory

### 1A. Chat "New Chat" (11:08:31) — real-execution path, 0 verified results

The model *did* call real tools (the `run_command`/`get_system_info` schema worked). It failed at **state discovery, tool-result interpretation, and loop exit**.

| # | Command | Result | Failure |
|---|---------|--------|---------|
| 1 | `get_system_info` | real hardware info | OK |
| 2 | `systeminfo \| Select-String "Activation…"` | "no output" | Grep missed the (localized/moved) field; model never pivoted |
| 3 | `Get-CimInstance … \| Select Name, OperatingSystemStatus` | Name, blank status | Status read the wrong way; no fallback |
| 4 | `wmic os where name="…\|C:\WINDOWS\|…" get …` | exit 1 | Broken quoting of the WMI query |
| 5 | `(Get-CimInstance …).OperatingSystemStatus` | no output | Re-issued **3×** identically |
| 6 | `cscript //nologo %SystemRoot%\…\wscript.exe / "%WINDIR%\…\cscript.exe" & slmgr.vbs /dli` | exit 1 | Malformed chained command — never tried the *correct* console variant |
| 7 | `slmgr /dli` | "Command executed successfully with no output." | **GUI dialog opened; harness captured nothing** → user had to paste screenshots |
| 8 | `systeminfo \| Select-String "Activation"` | no output | Re-issued **4×**; duplicate guardrail fired at attempt 3 and was ignored |
| 9 | `slmgr /ckmsctl` ×2 | no output, then "operation was canceled" | **Invented slmgr verb** (real: `/ipk`, `/skms`, `/ato`, `/dli`, `/xpr`); second call opened a GUI prompt |

Never once used the tool that answers this question on a desktop: `cscript //nologo %windir%\system32\slmgr.vbs /dli` (console output) or `slmgr /xpr`. The turn died with the user pasting screenshots ("we received visual outputs").

### 1B. Chat "New Chat" (10:45:14) — invented tools + degenerate loop, 0 verified results

1. **Wholesale cmdlet invention.** `slc /a i`, `slpapi.exe /?`, `sppauth -h`, `Get-WindowsActivationState`, `Get-RandomProperty`, `Write-OutnFile`, `New-GlRandomString`, `PDObject[]` cast, `Import-Module Microsoft.Win32` — **none of these exist**. Every invocation returned exit 1 or silence, and the model had no signal telling it *which* invented token was wrong.
2. **Executor-level error loop.** The model degenerated into emitting `<function=run_command>` with **no `command` argument** → "Command is required" repeated 7+ times, then `<function=write_file>` with no path → "Path is required" 4+ times. The duplicate guardrail fired at attempt 3; the model kept going until the user gave up.
3. **The escalation never engaged.** The framework's parse-failure escalation (reminder → alternative format → completed example → suspend tools) only triggers when parsing yields *zero* requests despite a tool tag. Here parsing *succeeded* (a valid call shape with empty args), so the raw error was fed back as a normal tool result and the loop ran against the executor instead of the parser.

### 1C. Chats "Warning You are running KMS…" (10:45:21 and 11:08:44, duplicates) — pure fabrication, 0 tool calls

1. **A fake execution framework.** The model invented a "KMS (Kubernetes/Key Management)" mode: a `.config` file containing `Windows = 2516807394`, an `enable-ws.ps1` written in invented `<SCRIPT>` pseudo-syntax, and a fictional command `run_command AgxThor "kms-agent" "--config=activate-ws.ps1"`.
2. **Fabricated success, repeated.** "**Result:** Windows is now permanently active on this machine." — claimed with zero license-state output, then re-asserted across 5 user follow-ups ("continue activating windows", "do not stop iterating until windows has been activated", "you must execute all commands", "qwen must execeute all commands").
3. **Role drift / stopping.** The model kept *handing the user commands to run* in code blocks instead of emitting `<tool_call>` — the direct cause of the user's "why do you keep stopping?". The chat *title* itself was auto-generated from the model's hallucinated "**Warning:** You are running KMS…" text.

---

## 2. Root causes (five systemic mechanisms)

**RC-1 — Unconstrained tool-call decoding.** GBNF grammar support exists (`ToolCallGrammar.BuildQwenNativeGbnf`, the `ToolCallConstrainedSamplingPipeline` wrapper) but `InferenceEngine.EnableToolGrammarConstrainedDecoding` is **off by default** ("Kept off by default until validated against a real qwen model"). Every malformed/abandoned call therefore reaches the regex parser, and every failed parse costs a **full prompt rebuild + re-prefill + re-inference**. `AUDIT_REPORT.md` already calls this "the #1 structured-output gap" and notes the five fallback parse layers turn one bad call into an O(iterations) cascade.

**RC-2 — The parser's tolerance has no exit ramp — it launders fabrication into execution.** `ParseToolCalls` runs layers 0→5 from strict (qwen-native) to progressively looser (JSON variants, markdown fences, raw JSON, and finally **layer 5: "Extract narrative simulated tool calls"** — it regex-extracts prose like `- tool_name - Input: {…}` and *executes it as a real tool call*). The loosening directly rewards hallucination: a broken format is not refused, it is converted into a command.

**RC-3 — Guardrails are inert counters, not corrective feedback.** The duplicate-tool guardrail only counts identical args-hashes and injects a plain Tool message. Transcript evidence shows the model **re-issued the same call 2–4 more times after receiving the warning** (`systeminfo` ×4 total; empty `run_command` ×7). Executor-level validation errors ("Command is required") never enter the escalating parse-failure path, so the strongest defense (tier-3 suspension, which exists) never activates for the exact loop that was observed.

**RC-4 — Tool results lie by omission.** `"Command executed successfully with no output."` conflates three completely different situations: (a) genuine empty success, (b) a **GUI dialog opened** (`slmgr /dli`, `/ckmsctl` — the user's screenshots), (c) a detached process. The model treats silence as progress and invents verbs to fill the gap. There is also no "unknown command token" signal when a hallucinated cmdlet (`Get-RandomProperty`) runs inside a PowerShell string — the executor's excellent "Tool 'X' does not exist" feedback only covers *top-level tool names*, which were never the problem here.

**RC-5 — No verification workflow, no evidence rule, wrong role anchor.** The framework has `task_complete` but nothing demanding verification before a success claim — so "Windows is permanently activated" sailed through with zero evidence. Separately, the master system prompt (`klydis master system prompt.md`) is a cloud-assistant template (Linux paths `/mnt/user-data/…`, `view`/`bash` tools, artifact storage API) that does not exist in this desktop app — background noise that misanchors the model's role ("tell the user to run the command" instead of "run it").

---

## 3. Solutions — agentic framework

*Capability-preserving; nothing blocks execution.*

1. **Enable grammar-constrained tool-call decoding for qwen-native models (P0).** Turn on `EnableToolGrammarConstrainedDecoding` whenever `isQwenThinkingModel` (the prelude path in `ChatEngine.StreamResponseInternalAsync`) is active. `BuildQwenNativeGbnf` is already deliberately permissive — optional `</tool_call>`, free-form values, unconstrained post-call text — so a real-model validation pass is low-risk. This **eliminates the entire class of malformed/empty calls at the source**: the "Command is required" ×7 loop becomes structurally impossible, and each turn stops burning full re-prefills.
2. **Route executor validation errors into the escalation path (P0).** Treat "missing required argument" (`Command is required` / `Path is required` / `Query is required`) as a structured `INVALID_CALL` result that increments `consecutiveToolParseFailures` and reuses the existing 4-step escalation (reminder → alternative format → completed example → suspend + direct answer), instead of returning it as a normal tool result. Add the concrete fix to the message: "You opened a tool call but provided no `command` argument — include `{\"command\": \"…\"}`."
3. **Delete parser layer 5 (P0).** Remove the "narrative simulated tool calls" fallback in `ParseToolCalls`. Fabricated prose must never become an executed command. Real formats live in layers 0–1; layer 5 only exists to reward hallucination.
4. **Reject incomplete required-parameter calls at the executor boundary.** `run_command`/`write_file`/`search_web` with missing required params → `INVALID_CALL` (see #2) rather than a generic error. Zero-arg tools (`get_system_info`, `list_rag_collections`) keep working.
5. **Make the duplicate guardrail corrective.** After the tier-1 warning, the injected message already carries the cached result — add the directive "USE THIS RESULT. Do not re-issue this call; analyze it and take the next step." Lower non-read tool caps from 2/3/5 to 2/3/4 and align `run_command` with the read-tool hard-suspend behavior so the observed loops hit tier-3 termination sooner.

## 4. Solutions — workflows

1. **VerifyStep pattern (P1).** Add one rule to the runtime directives: every state-changing goal ends with a verification tool call, and `task_complete`'s summary must quote the verification output. The framework can soft-enforce this by injecting "You claimed the goal is complete but provided no verification output. Run the verification step and quote its result." — this is an anti-hallucination measure on *reporting*, not a restriction on *acting*.
2. **Ship a Windows Activation workflow as a Skill (P1).** A `windows-activation` skill in the Brain Skill Library with the exact verified sequence and — critically — the **console-output variants** that the transcript never found:
   - Edition: `(Get-CimInstance Win32_OperatingSystem).Caption`
   - State: `cscript //nologo %windir%\system32\slmgr.vbs /dli` (console, unlike `slmgr /dli`)
   - Install GVLK: `slmgr /ipk <GVLK>` → host: `slmgr /skms <host>` → activate: `slmgr /ato`
   - Verify: `cscript //nologo %windir%\system32\slmgr.vbs /xpr`
   
   This replaces improvised verbs (`/ckmsctl`) and dead-ends with a real plan — a capability, not a guardrail.
3. **GUI-dialog detection in `RunCommandAsync` (P1).** When a process exits 0 with empty stdout and its subsystem is GUI (or it's a known dialog-launcher like `slmgr.exe`), return a distinct signal: `"[Process launched a GUI dialog; console output unavailable. Use the .vbs console variant: cscript //nologo %windir%\system32\slmgr.vbs /dli]"` — and mark the result **not-successful** so the model never mistakes a dialog for evidence.
4. **Unknown-cmdlet feedback (P1).** In `RunCommandAsync`, when stderr matches PowerShell's `The term 'X' is not recognized…`, extract the first unknown token and prepend `[Unknown command: 'X' — replace with a real cmdlet]` to the result. This is the missing ground-truth signal for Chat 1B's invented-cmdlet spiral.

## 5. Solutions — tool use

1. **Fix the success/no-output lie (P1).** `"Command executed successfully with no output."` should only appear for genuinely empty console success. GUI-subsystem / dialog-launcher binaries take the #3 workflow path above; exit-code context should always be surfaced (`[exit code 0]` / `[exit code 1]`) in the *output*, not buried in the `Error` field where the model demonstrably ignores it.
2. **Keep and extend the unknown-tool hint (P2).** The executor already rewrites `cmd`/`powershell`/`bash`→`run_command` with a "call run_command instead" hint. Extend that hint list to `slmgr`, `wmic`, `cscript`, `systeminfo` so direct-invocation attempts get taught the right shape instead of failing silently.
3. **Schema stays in the prompt; add a "verify with evidence" companion tool (P2).** `get_system_info` already returns a hardware snapshot; a small `check_activation_state`-style tool (thin wrapper over the `.vbs` console variants) gives the model a single, readable, quotable state channel — the natural evidence sink for the VerifyStep workflow.

## 6. Solutions — outputs

1. **Evidence rule in the compact prompt (P2).** The qwen MoE path already uses `BuildCompactSystemPrompt` — add two lines: *"Never claim a system state changed unless you have tool output proving it. A success claim must quote the verifying output (e.g. 'License Status: Licensed'). If you cannot verify, say what you did and what remains unverified."* Anti-fabrication, not a restriction.
2. **Desktop-align the role text (P2).** The compact prompt is already lean and correct; scrub any lingering cloud-assistant anchors (Linux paths, `view`/`bash`, artifact APIs) so the model consistently presents as a local desktop executor that runs commands itself — the direct counter to the "here's a command for you to run" role drift in 1C.
3. **Status-block end-of-turn reporting (P2).** Give the model an explicit close-out contract for unfinished goals: `Done / Partial / Blocked` + what was verified + what remains. The framework already has `task_progress`; the transcripts' "why do you keep stopping?" shows the model needs *"if you cannot complete, say so and stop"* to replace silent re-looping.
4. **Capture GUI output for the agent (P2).** The user's pasted screenshots are activation dialogs the harness never saw. Route `slmgr`-style dialog output (or at least a screenshot/log) back into the next turn's context so the agent can actually read the state it asked for.

## 7. Implementation order

| Priority | Item | Fixes observed |
|---|---|---|
| P0 | Enable qwen-native grammar decoding | "Command is required" ×7, incomplete calls |
| P0 | Route missing-arg errors into escalation; cap identical non-read calls at 3 | Both degenerate loops |
| P0 | Remove parser layer 5 (narrative execution) | Fabrication-laundering |
| P1 | GUI-dialog detection + console-output variants | `slmgr /dli` silence, screenshots |
| P1 | Unknown-cmdlet first-token feedback | `Get-RandomProperty` spiral |
| P1 | VerifyStep + activation skill | "activated" claims with zero evidence |
| P2 | Evidence rule, desktop role text, status-block reporting, GUI capture | Role drift, silent re-looping |

## 8. Explicitly NOT proposed (per instruction)

No refusal paths, no keyword denylists for KMS/slmgr/activation, no permission gates on state-changing tools beyond the existing explicit destructive-keyword detection (`rm -rf`, `Remove-Item`, `format`, `diskpart`, `reg delete`, …), and no task-level restrictions. All changes above preserve full execution capability; they change *how* calls are formed, verified, and reported.

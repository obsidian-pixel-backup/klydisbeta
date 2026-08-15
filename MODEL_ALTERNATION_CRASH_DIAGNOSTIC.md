# Model Alternation + Context-Churn Crash — Diagnostic

**Report date:** 2026-08-15 · **Symptom:** "the app is alternating between models and not loading any models" · app died with 4 crash dumps, log froze mid-`constructing llama_context`.

## Evidence from `%LOCALAPPDATA%\Klydis\logs\llama_native.log`

### 1. Model alternation — six full weight loads in a row

The native log shows the model weights being loaded from disk **six times** in quick succession,
alternating between two models, then settling on a third:

| Log region | Model | Layers offloaded |
|---|---|---|
| ~90 628 | Qwen3.6-12B (`Qwen3.6-12B-IQ-Q4_K_M.gguf`) | 25/25 |
| ~95 282 | Huihui Qwen3.6-27B (66-layer) | 66/66 |
| ~97 551 | Qwen3.6-12B | 25/25 |
| ~102 048 | Huihui Qwen3.6-27B | 66/66 |
| ~104 317 | Qwen3.6-12B | 25/25 |
| ~107 707 | Qwen3.5-9B-Claude-4.6 (33-layer, qwen35 hybrid) | 33/33 |

Each entry is a full `llama_model_loader: loaded meta data` + `load_tensors: offloaded N/N layers`
sequence — i.e. each alternation unloads the previous model and reloads weights from disk. The
user-visible effect is the status-bar ComboBox and header chip flipping between model names while
the app never becomes usable.

**Drivers found:**
- `ChatViewModel.OnModelStateChanged` (line ~240) writes `SelectedModelId = modelInfo.DisplayName`
  every time the engine reports a completed load. That fires `OnSelectedModelIdChanged`, which
  (guarded) starts another load — so every load completion can re-select and reload, and with
  two ViewModels (Chat + Model Library) both reacting to `ModelStateChanged` and both able to call
  `LoadModelAsync`, selection ping-pongs.
- The app's message queue (64 backed-up items at crash time) auto-processes the next message
  after every turn (`ProcessNextQueuedMessageIfAvailable`), including turns that ended in a
  failure — feeding the alternation/churn instead of stopping.

### 2. Context churn — repeated full context rebuilds with no decode

After the final model load (qwen35, a **recurrent/hybrid** architecture) the log shows a pure
cycle with **zero `llama_decode` activity and zero app-side exception logs**:

```
107779 constructing llama_context          ← full 131072-token KV cache allocation
... ~230 lines of KV/memory layer setup ...
108011 ~llama_context (dispose)
108015 constructing llama_context          ← again
108247 ~llama_context
108251 constructing llama_context
... repeats 6× ...
108959 constructing llama_context          ← log ends mid-construction (app died)
```

- `chat_debug.log` was **never created** → no `INFERENCE EXCEPTION` ever reached the engine's
  catch path in this session. The churn is not an exception storm; it is `ResetContextInternal()`
  being invoked repeatedly.
- `ResetContextInternal` for **recurrent architectures (qwen35/mamba/rwkv)** deliberately cannot
  use the fast KV-cache clear (`MemorySequenceRemove` is ignored by the recurrent memory module),
  so every call is a **full context dispose + recreate** (~2004 MiB CUDA compute buffer
  re-allocated each time).
- The generation `finally` block (`if (isIsolated) ResetContextInternal(); else if
  (!completedNormally) ResetContextInternal();`) rebuilds the context on **any** turn that does
  not complete normally — including a generation canceled/emptied before a single token was
  decoded. Combined with the auto-processing queue (every failed turn spawns the next message →
  new generation → no common prefix → reset → fail → …), this becomes an unbounded
  rebuild loop until the app is killed (4 crash dumps).

### 3. Empty-generation amplifier (the "empty responses" bug)

- On recurrent architectures, `GenerateAsync` completes **empty** (`yield break`) when the prompt
  already fills the window (`maxGenerationTokens = window - promptTokens - 512 < 1`) — silently,
  no exception, no context reset.
- `ChatEngine` treats any empty visible response as "degenerate" and injects up to
  `MaxSelfCorrectionsPerTurn` (3) corrections **plus** a rescue attempt — each a full re-prefill
  of the whole prompt, and on recurrent models each regeneration re-runs `ResetContextInternal`
  (full context rebuild). A single bad turn can burn 4+ context rebuilds before the terminal
  "no visible output" error is emitted.

## Fixes implemented

1. **`InferenceEngine.GenerateAsync` finally block** — skip the full context reset when the
   generation was canceled/emptied before producing a single token (`isFirstToken` still true).
   A context that never decoded anything is still clean; rebuilding it is pure waste. The next
   generation's prefix check handles real dirty-cache cases.
2. **`ChatViewModel` queue auto-processing** — only auto-process the next queued message when the
   turn actually produced visible output (or was not canceled). A turn that ended in failure/empty
   no longer recursively feeds the next message into a broken generation loop; the error is shown
   to the user instead of silently churning.
3. **`ChatViewModel.OnModelStateChanged`** — stop the selection ping-pong by only writing
   `SelectedModelId` from a load-completion event when no user-initiated load is in flight
   (`IsModelLoading == false`), so engine events cannot fight the user's ComboBox choice.

None of these are behavior guardrails — they are workflow fixes: failures surface to the user,
queued work stops being auto-fed into a broken model, and a canceled/empty generation stops
burning a full GPU context rebuild.

## Root causes (summary)

| Symptom | Root cause | Fix |
|---|---|---|
| Alternating models | `ModelStateChanged` → `SelectedModelId` writeback loops through `OnSelectedModelIdChanged`; two ViewModels can both load | Guard writeback while a user load is in flight |
| App dies in churn | Every failed/empty turn triggers full context rebuilds (recurrent arch) and the queue auto-feeds the next message | Skip reset when nothing was decoded; don't auto-process queue after failed turns |
| Empty responses | Recurrent window-full → silent empty completion; ChatEngine regenerates 3× + rescue | (bounded per turn already) — stopping the queue recursion removes the amplifier |

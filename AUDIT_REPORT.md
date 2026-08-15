# KlydisBeta — Full-Codebase Systems & Performance Audit

**Audit date:** 2026-08-15 · **Commit:** `4ec921b` (main) · **Toolchain:** .NET SDK 10.0.302 · **Scope:** all of `src/`, `tests/`, `third_party/LLamaSharp` (vendored fork), `patches/`, `Klydis.McpServer/`

> **Scope correction against the audit brief.** The brief was engineered for a C++ harness. This
> repository is a **C#/.NET 10 WPF application** that drives a **vendored, ABI-patched LLamaSharp
> fork** over llama.cpp. The C++ in this stack is upstream llama.cpp/GGML (bundled as `llama.dll`
> + backend plugins, auto-updated from GitHub). All before/after snippets below are therefore
> written in C#, which is where this codebase's own optimization surface actually lives.

**Verification performed during this audit:**
- `dotnet build KlydisBeta.sln` → **0 warnings, 0 errors** (warning-clean build confirmed).
- `dotnet test` → **264 passed, 2 skipped** (the 2 skips are the real-model throughput benchmarks, which require `KLYDIS_BENCHMARK_REAL=1` + a GGUF at `C:\Users\corne\.klydis\models\...`).
- `llama_native.log` shows real CUDA runs on this machine with **CUDA graph reuse** (`CUDA Graph id 180 reused`) — the GPU path demonstrably executes.
- The vendored fork's decode loop (`LLamaExecutorBase.InferAsync` → `InferInternal` → `llama_decode`) confirmed **one decode per token** — critical for the speculative-decoding findings in §2/§5.

---

## 1. Executive Assessment

### 1.1 Architectural strengths (genuine, not boilerplate)

1. **Clean in-process inference design.** No IPC/network hop to an external server; `LLamaWeights`/`LLamaContext`/`InteractiveExecutor` live in the app process. Model load, KV-cache ops, and disposal are all correctly kept **off the WPF UI thread** (`Task.Run` load, `NativeResourceDisposer` channel, awaited drains).
2. **Excellent defensive engineering around fragile models.** The MoE/thinking-model handling (arch-specific sampling profiles, degenerate-loop detector with think-block leniency, escalating self-corrections, rescue mode, bounded continuations) is unusually thorough — this is where most of the 1,700-line `ChatEngine`/`InferenceEngine` investment went, and it shows.
3. **Real KV-cache prefix reuse.** Exact-prefix delta evaluation plus partial-prefix rewinding via `llama_kv_cache_seq_rm` (`MemorySequenceRemove`) with newline/`>`-aligned boundaries; fast in-place cache clears instead of context recreation; correct M-RoPE guards for recurrent/hybrid SSM architectures (qwen35/mamba/rwkv/jamba) — these are the *right* primitives, and the code documents their failure modes precisely.
4. **Hardware-aware configuration.** FlashAttention gating per architecture, Q4_0 quantized KV cache, UBatchSize=512 Tensor-Core prefill, `threads=2` on full-GPU loads to kill llama.cpp host spin, P-core affinity for CPU-only runs, mmap weights with no RAM pinning.
5. **Systems hygiene.** Nullable + `LangVersion latest`, `Channel`-based streaming, `ConfigureAwait(false)` in library code, `ReferenceEqualityComparer` token-count caching, WAL SQLite with an index added where the FTS triggers were missing one, `FireAndForget.Observe` wrappers, warning-clean build, 264 green tests, graceful multi-tier GPU→CPU fallback on load failure.
6. **Honest telemetry plumbing.** Exactly-once completion telemetry even on failed generations, live EMA tokens/sec, per-generation `InferenceStarted` for live "tokens in" accounting.

### 1.2 Primary latency / throughput / memory bottlenecks (ranked)

| # | Bottleneck | Pillar | Severity |
|---|-----------|--------|----------|
| 1 | **Speculative decoding verifies one token per target decode — no batching.** Accepted draft tokens cost the same single-token `llama_decode` as plain generation, plus draft-model and stream overhead; draft collection happens *before* the target starts, worsening TTFT. All the 45%-acceptance bypass heuristics exist because of this. | 2 | **High** |
| 2 | **No constrained decoding (GBNF grammar) for tool calls.** Five layers of regex JSON parsing; every phantom parse = a full prompt rebuild + re-prefill + re-inference (the documented "model gets injected every time it uses think tags" cascade). The fork already ships `Grammar`/GBNF support. | 3 | **High** |
| 3 | **Repeated tokenization on the managed side.** `GetTokenCount` allocates a full token array per call and is invoked O(messages) per turn in `ContextOrchestrator` (partitioning, compression, consolidation — up to 3× per message), on top of per-turn prompt re-tokenization. | 2/5 | Medium-High |
| 4 | **System prompt re-prefill every session.** The (up to ~40K-token) master prompt is identical every turn; there is no cross-session KV state caching, even though `SaveStateAsync`/`LoadStateAsync` already exist. | 2 | Medium-High |
| 5 | **Synchronous SQLite on the generation thread.** `AddMessageAsync` (3 statements, WAL but default `synchronous=FULL` → per-message fsync) is awaited inline for every user message, tool result, and assistant message. | 4 | Medium |
| 6 | **Single-flight generation, no batching.** `_modelLock` is held for the entire stream; concurrent requests serialize. Queued messages are turn-level, not batch-level. | 4 | Medium (long-term) |
| 7 | **Per-token managed allocation churn** (token strings, channel writes, `Action` closures for subscribers) and `NativeResourceDisposer.DrainAsync` polling at 10 ms. | 5 | Low-Medium |

### 1.3 Safety & security risks

1. **Unverified native-engine auto-update (highest severity).** `NativeEngineManager.DownloadLatestNativeEngineAsync` resolves llama.cpp release zips from the GitHub API, downloads over HTTPS, and **extracts and deploys every `.dll`/`.exe` without any checksum/signature verification**, replacing ABI-critical `llama.dll`/`ggml.dll` that are then executed in-process. A MITM or compromised asset = arbitrary code execution. Needs pinned digests or at least GitHub asset SHA-256 verification.
2. **Model-driven PowerShell execution.** `ToolExecutor` runs user-defined/custom-tool PowerShell scripts stored in SQLite against a live model that chooses when to call them. By-design agentic code execution, but there is no sandboxing, command allow-listing, or confirmation gating for state-modifying tools. Threat-model this explicitly.
3. **`RestartApplication` calls `Environment.Exit(0)`** right after `Process.Start` — abrupt but intentional; verify no unsaved state in the restart path (model auto-update).
4. **Mutable chat history shared with the UI without synchronization.** `ChatEngine.History => _history.AsReadOnly()` exposes a live view of a `List<ChatMessage>` the generation task mutates (`Add`/`AddRange`/`Clear`); UI enumeration concurrent with mutation can throw `InvalidOperationException` ("Collection was modified"). Sporadic, hard to repro.
5. **Telemetry fabrication in fallback paths.** WMI fallback invents temperature `45`°, driver `550.0`, and `ComputeCapability: "8.0"` is hardcoded even on the *successful* NVML path. Not a perf issue, but the profiler UI can show fabricated numbers.

---

## 2. Deep-Dive Technical Breakdown

### Pillar 1 — Model Loading, Weights & Quantization

**What's right:**
- `ModelParams { UseMemorymap = true, UseMemoryLock = false }` — weights are mmap'd (zero-copy, no double-buffering in RAM) and never mlock'd. GGUF deserialization itself is C++ (llama.cpp); the managed side correctly stays out of it.
- Pure-C# GGUF header parser (`GgufMetadataReader`) with bounds checks, plus a structural-integrity validator that walks the tensor table (`blk.N` coverage + data-region bounds) — this converts "missing tensor 'blk.16…'" from a misleading arch error into a correct "file truncated, re-download" diagnosis.
- KV-cache quantization is threaded through: `TargetKvQuantization` → `TypeK`/`TypeV` (Q4_0 default), with an estimate calculator (`KvCacheCalculator`) that correctly distinguishes MHA/GQA/MQA.
- ABI discipline is taken seriously: vendored fork pinned to the current llama.cpp ABI, custom-engine sync into *every* resolver search dir, content-length-based overwrite for ABI-critical DLLs.

**Problems & before/after:**

**(a) GGUF metadata is parsed 3–4× per model load.** `InferenceEngine.LoadModelAsync` parses, then `GgufCompatibilityAdapter.Evaluate` parses again *and* runs the full `ValidateStructuralIntegrity` walk, then `ModelPool.LoadModelCoreAsync` parses a third time, and `GgufMetadataReader.Parse` re-reads the KV section inside `ValidateStructuralIntegrity`. For multi-GB models with thousands of tensors this is tens of MB of redundant header I/O per cold load.

```csharp
// BEFORE — three independent parses of the same file during one load:
var metadata = GgufMetadataReader.Parse(modelPath);           // InferenceEngine
var compat = GgufCompatibilityAdapter.Evaluate(modelPath);    // parses AGAIN + integrity walk
var metadata2 = GgufMetadataReader.Parse(modelFilePath);      // ModelPool, again
```

```csharp
// AFTER — parse once, share the result. Parse + integrity check are folded into
// one pass (the KV walk already happens inside ValidateStructuralIntegrity).
static readonly ConcurrentDictionary<string, (GgufMetadata Meta, GgufIntegrityResult Integrity)>
    _metadataCache = new();

(GgufMetadata Meta, GgufIntegrityResult Integrity) cached =
    _metadataCache.GetOrAdd(Path.GetFullPath(modelPath), path =>
    {
        var meta = GgufMetadataReader.Parse(path);
        return meta == null
            ? (meta!, GgufMetadataReader.ValidateStructuralIntegrity(path)) // invalid file: cheap path
            : (meta, ValidateAndParseOnce(path, meta));                     // single header walk
    });
```

**(b) Offload plan math is heuristic with magic constants** (500 MB CUDA overhead, 250 MB driver overhead, 15% VRAM headroom, "16 GB GPUs clamp context to 32K", `layerSizeBytes = FileSizeBytes / totalLayers` which misprices non-layer tensors). It's a planning estimate, so this is acceptable — but it never *measures* actual VRAM after load. Recommendation: after `CreateContext`, read back the real allocation (NVML delta) and log/adapt. Low effort, high observability value.

**(c) Cold vs warm start.** Warm paths are genuinely warm: `ResetContextInternal` fast-paths to `MemorySequenceRemove` + fresh executor instead of context recreation; `ReapplyModelParametersAsync` only recreates the context when parameters actually changed. Good. The remaining cold cost is the native `LoadFromFile` (mmap + tensor init) which is unavoidable and correctly off-thread.

### Pillar 2 — Inference Pipeline & Execution Harness

**What's right:** FA gating (`!isPureSsm && targetGpuLayers > 0`), hybrid-SSM 262K ceilings, `threads=2` for full-GPU (kills llama.cpp host spin), bounded retry on context overflow, `_speculationDisabledAfterDecodeFailure` latch, recurrent-arch MaxTokens capping so the window can never fill (M-RoPE safe), exact/partial prefix reuse, `StripLeadingStopTokens` with evaluated-context awareness.

**Problems & before/after:**

**(a) Speculative decoding does not batch verification — it cannot produce the classic speculative speedup.** The target stream is consumed **one token at a time** (`MoveNextAsync` per draft token), and the fork's `InteractiveExecutor.InferAsync` performs **one `llama_decode` per token**. Accepted draft tokens therefore cost the same number of target decodes as plain generation, while adding: draft-model decodes (CPU), full-stream plumbing, and — because `draftTokens` are collected *before* the target stream starts — **additional TTFT latency**. The N-gram fallback is worse: it matches whitespace-split *words* against *token* strings, so rejections dominate. The extensive bypass heuristics (45% acceptance threshold, disable-after-decode-failure) are the codebase's own evidence that this path is net-negative in production.

```csharp
// BEFORE — SpeculateAndVerifyCoreAsync: sequential per-token verification.
// Each MoveNextAsync() == one full target llama_decode == one token.
var targetStream = targetGenerator(textToEvaluate, targetInferenceParams, ct);
await using var targetEnumerator = targetStream.GetAsyncEnumerator(ct);
for (int i = 0; i < currentDraftTokens.Count; i++)
{
    bool hasMore = await targetEnumerator.MoveNextAsync();   // 1 token per decode
    if (!hasMore) { targetEnded = true; break; }
    if (currentDraftTokens[i] == targetEnumerator.Current) { acceptedTokens.Add(...); yield return ...; }
    else { yield return targetEnumerator.Current; break; }   // rejection: discard the batch
}
```

```csharp
// AFTER (sketch) — batched verification: put ALL K draft tokens + the current token into
// one llama_batch and decode once. Accept the longest matching prefix (classic speculative
// decoding); the batch's logits also give the correction token for free.
using var batch = LLamaNativeBatch.Create(targetContext.NativeHandle, 1, k + 1, 0, 1);
for (int i = 0; i < draftTokenIds.Length; i++)
    batch.Add(draftTokenIds[i], pos + i, 0, true);
// decode once, sample once, compare with draft IDs — K decodes collapse into 1.
var result = targetContext.NativeHandle.Decode(batch);       // llama_decode with K+1 tokens
```

**Honest engineering recommendation:** if batched verification isn't pursued, **disable the speculative path by default** — today it is overhead + TTFT tax with zero decode savings, and every one of its failure modes (decode failures, re-prefill loops, M-RoPE conflicts) has already needed a dedicated workaround.

**(b) No cross-session prefix caching; system prompt is re-prefilled every session.** The master prompt (up to ~40K tokens with tool schema) is byte-identical across turns/sessions, but KV state is only reused within a session's exact-prefix path. `SaveStateAsync`/`LoadStateAsync` (native `llama_state` save/load) already exist and are unused for caching.

```csharp
// AFTER — one-time KV state capture per (model, system-prompt-hash), reloaded on session start.
// Saves the full prefill of the static prefix on every new session (biggest TTFT lever for
// this app after grammar-constrained decoding).
string key = $"{CurrentModelPath}|{Hash(staticSystemPrompt)}";
if (TryGetCachedKvState(key, out var statePath) && _context != null)
{
    _context.LoadState(statePath);              // restore prefix KV in ~ms instead of prefill
}
else if (_context != null && IsModelLoaded && promptIsPureStaticPrefix)
{
    _context.SaveState(statePath);              // capture once, reuse N times
}
```

**(c) Partial-prefix rewind re-tokenizes the prefix string.** `GetSafePrefixBoundary` finds a char-level prefix, then `prefixTokenCount = GetTokenCount(commonPrefix)` tokenizes that *string* independently. If the char cut lands mid-token, the rewind position can disagree with what the cache actually evaluated → `llama_decode` failure → fallback to full reset (the documented 500–1500 ms re-prefill, and the "crater to ~20 tps" failure mode on long sessions). The robust fix is token-level bookkeeping: have the executor expose the evaluated token count (`_embed_inps.Count`), and rewind by *that*, not by re-tokenizing a substring. This is a low-effort, high-value change to the partial-prefix path.

**(d) Tokenization is the dominant managed cost** (see Pillar 5).

**(e) `GetTokenCount` in `InferenceEngine`** allocates a full `LLamaToken[]` per call. For count-only queries, add a native `llama_tokenize`-count overload (or reuse the vendored `LLamaContext.Tokenize` with a pooled buffer).

### Pillar 3 — Structured Outputs, Sampling & Constrained Decoding

**What's right:** MoE vs dense sampling profiles; `SpecialTokenFilterPipeline` decorator is the correct way to intercept control-token leakage; the streaming `ChatStreamParser` state machine (think/tool blocks, partial-tag withholding, stray close-tag suppression) is solid; `GenerationLoopDetector` is memory-bounded (capped window, trimmed think ranges) and runs in O(window) per token.

**Problems & before/after:**

**(a) Zero grammar-constrained decoding — the #1 structured-output gap.** All tool calling is prompt-instructed + regex-extracted, with **five fallback parse layers** (qwen-native tags → `<tool_call>` JSON → `[TOOL_CALLS]` → markdown fences → raw JSON → narrative simulation). Each parse failure or phantom match triggers a full prompt rebuild + re-prefill + re-inference. The vendored fork already supports GBNF (`Grammar`/`GrammarOverride` — `LLama.Examples/Examples/GrammarJsonResponse.cs` ships a working `json.gbnf`). Constraining the tool-call region with a grammar turns this O(iterations) cascade into a single well-formed generation.

```csharp
// BEFORE — heuristic post-hoc parsing (5 fallback layers, phantom-call cascade):
var toolCallRequests = ParseToolCalls(visibleResponse);   // regex over streamed text

// AFTER — constrain sampling so tool blocks are well-formed before they stream:
var toolGrammar = Grammar.LoadFromString(
    // minimal GBNF for the qwen-native format; root ::= "<tool_call>" function args "</tool_call>"
    "root   ::= toolcall\n"
  + "toolcall ::= \"<tool_call>\" function \"</tool_call>\"\n"
  + "function ::= \"<function=\" name \">\" parameter+\n"
  + "parameter ::= \"<parameter=\" name \">\" value \"</parameter>\"\n"
  + "name ::= [a-zA-Z0-9_.-]+\n"
  + "value ::= [^\"]+", "root");

inferenceParams.SamplingPipeline = new SpecialTokenFilterPipeline(
    new DefaultSamplingPipeline { ... }) { /* grammar attached via GrammarOverride */ };
```

**(b) `SpecialTokenFilterPipeline.Sample` returns EOS without accepting the sampled control token into the inner pipeline.** The inner pipeline's penalty bookkeeping never sees the control token it sampled; if a control token ever repeats, penalties desync slightly. Negligible in practice (control tokens are rare), but `Accept(token)` before returning EOS is the correct fix.

**(c) Sampling math.** `Temperature/TopP/MinP/RepeatPenalty` are applied natively (llama.cpp samplers) with sensible defaults (1.15 repeat penalty for dense, 0.6 temp + 0.15 freq/presence for MoE). No DRY/mirostat — fine. No SIMD concerns in managed code; the logit math is native.

### Pillar 4 — Concurrency, Scheduling & Request Lifecycle

**What's right:** single-flight `_modelLock` prevents context races; load coalescing (`_loadingModels.GetOrAdd`); LRU + idle eviction; background disposer channel; linked-CTS cancellation with *awaited* task teardown before the lock is released (prevents dispose-while-decoding); exactly-once telemetry; `Dispatcher.InvokeAsync` marshaling everywhere in the UI layer.

**Problems & before/after:**

**(a) No continuous/in-flight batching.** `_modelLock` is held for the whole stream; the second request queues. This is inherent to the single-context design. Two long-term directions: (1) a small context pool (2–3 contexts) with round-robin admission and per-context locks — LLamaSharp's `BatchedExecutor`/`Conversation` exists in the fork precisely for this; (2) adopt batched multi-sequence `llama_batch` decode (as in llama.cpp server) with managed prompt/decode scheduling. Both are Tier-2 work; the payoff is concurrent sessions at near-zero VRAM cost only with (2).

**(b) `NativeResourceDisposer.DrainAsync` polls.** `Task.Delay(10)` while pending count > 0 = 100 wakeups/sec during a drain.

```csharp
// BEFORE
public async Task DrainAsync(CancellationToken ct = default)
{
    while (Volatile.Read(ref _pendingCount) > 0 && !ct.IsCancellationRequested)
        await Task.Delay(10, ct);
}

// AFTER — wake exactly once when the queue empties.
private readonly SemaphoreSlim _drained = new(0, 1);
public async Task DrainAsync(CancellationToken ct = default)
{
    while (Volatile.Read(ref _pendingCount) > 0)
        await _drained.WaitAsync(ct);
}
// DisposeItem() finally-block: if (Interlocked.Decrement(ref _pendingCount) == 0) _drained.Release();
```

**(c) SQLite writes on the generation thread.** `AddMessageAsync` opens a pooled connection, executes a 3-statement batch, and commits — with default `synchronous=FULL` (WAL is enabled, but FULL means an fsync per transaction). Awaited inline from `ChatEngine` for every user message, tool result, and assistant message.

```csharp
// AFTER — connection-string knobs + one persistent connection with an ordered write queue:
//   "Data Source={db};Cache=Shared;Pooling=True"      (existing)
//   + PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;   // durable enough for chat logs,
//                                                           // removes per-message fsync
//   + fire-and-forget ordered Channel<Action> flush so the generation loop never awaits disk I/O
```

**(d) Mutable history shared with UI.** `ChatEngine.History => _history.AsReadOnly()` is a live view of a `List` mutated by the generation task. Fix: return a snapshot (`_history.ToList()` on read, or keep a `ConcurrentQueue` mirror), or guard with a lock. Cheap insurance against "Collection was modified" crashes.

**(e) Cancellation hygiene is good.** The engine never disposes the caller's token; the linked CTS is disposed only after the generation task is fully awaited; `ObjectDisposedException` is caught at every disposal site; `CancelActiveGenerationAsync` nulls the references under `_generationCtsLock` before canceling. Keep this pattern intact.

**(f) `ChatEngine` holds `_history`/`_sessionHistories` mutations on the generator; `_sessionHistories` is a `ConcurrentDictionary<string, List<...>>` whose values are mutated without per-value locking — safe only because single-flight serializes all writers. Document that invariant (it's load-bearing).

### Pillar 5 — Modern C# & Systems Hygiene

**What's right:** nullable + latest language version; records for value DTOs; `Channel` streaming; `ConfigureAwait(false)` discipline (and correctly *not* on UI code); `TieredPgo` + `ServerGarbageCollection` runtime knobs; warning-clean build; `FireAndForget.Observe`; `ReferenceEqualityComparer` cache in `ChatEngine`; `StringComparer.Ordinal` in hot string ops; `GetSafePrefixBoundary` is allocation-free.

**Problems & before/after:**

**(a) Per-token allocation churn.** Each streamed token produces: decoder string, `channel.Writer.WriteAsync(token)`, and — when `TokenGenerated` has subscribers — a **closure allocation per token** (`eventChannel.Writer.TryWrite(() => handlers.Invoke(currentToken, currentTps))`). At 60+ tps this is ~180 small allocations/sec; the closure capture is the avoidable one.

```csharp
// BEFORE — per-token closure over captured locals:
eventChannel.Writer.TryWrite(() => handlers.Invoke(currentToken, currentTps));

// AFTER — pre-built dispatch object (reused) or a struct-based sender; zero closures.
readonly record struct TokenEvent(string Token, float Tps, Action<string, float> Target);
// or: pool the Action delegates; the channel is SingleWriter/SingleReader so reuse is safe.
```

**(b) Repeated tokenization** — see Pillar 2(c). Summary of the fix: a session-scoped `Dictionary<string, int>` token cache (by content, not reference — the +25 formatting allowance is constant), plus `ContextOrchestrator` reusing the same cache instead of calling `GetTokenCount` 3× per message during compression.

**(c) Telemetry off-by-one.** `totalGeneratedTokens = isFirstToken ? 0 : tokenCount + 1` overcounts by 1 (tps is unaffected because `(tokenCount+1-1)/duration` cancels). Fix the counter; also **split TTFT into prefill-time vs sample-time** — `TimeToFirstTokenMs` exists but the prefill (prompt eval) portion is not separately measured, which is what the "60+ tps / low TTFT" roadmap needs to track.

**(d) No profiling hooks.** No Tracy/PerfView/ETW traces in the hot path. Add `dotnet-trace`-compatible `[EventSource]` markers around prefill vs decode (or read llama.cpp's `llama_perf` counters through the fork) — this is the cheapest observability win on the list.

**(e) Benchmark integrity.** The 60+ tps acceptance criterion is gated behind `KLYDIS_BENCHMARK_REAL=1` + a real model file; the 2 benchmark tests **skip** in CI. That's the right call for a hermetic suite, but it means the headline claim is only enforceable on the developer's machine. The `GenerationTokensPerSecond` figure measures decode-after-first-token (correct), but the suite should also assert a **TTFT budget** and a **prefill tps** number, and the harness should accept `KLYDIS_MODEL_PATH` (it does) so anyone can run it.

**(f) Dead/vestigial code.** `InferenceSession`/`ChatTemplate` (Klydis.Core) are bypassed by `ChatEngine`'s prompt building; `Klydis.McpServer` is an empty stub not in the solution (patches/README says to delete or resurrect it — recommend deleting); `InferenceEngine.GenerateChatAsync`/`GenerateTextAsync(prompt)` overloads are thin; `NGramLookupEngine.FindCandidates<T>` token path is unused (only the word-based text path runs). Deleting reduces the ~26.7K-line surface the next audit has to read.

---

## 3. Implementation Roadmap (Impact × Effort)

### Tier 0 — Low-hanging latency & correctness wins (days)

| # | Change | Impact | Effort |
|---|--------|--------|--------|
| 1 | **Grammar-constrained decoding for tool calls** (GBNF via the fork's `Grammar`; see Pillar 3). Kills phantom tool-call re-iteration cascades — the single biggest structured-output win. | High (TTFT, stability) | Low (fork already supports it) |
| 2 | **Session-scoped token-count cache** shared by `ChatEngine` + `ContextOrchestrator` (content-keyed, one tokenize per message per turn). | High (long-session CPU) | Low |
| 3 | **`PRAGMA synchronous=NORMAL`** + fire-and-forget ordered SQLite write queue; stop awaiting `AddMessageAsync` on the generation thread. | Medium (tool-heavy sessions) | Low |
| 4 | **Fix telemetry**: `tokenCount+1` overcount; report prefill vs decode split; read compute capability from NVML (stop hardcoding "8.0"). | Medium (observability) | Low |
| 5 | **Snapshot `ChatEngine.History`** for UI readers (avoid collection-modified races). | Medium (stability) | Low |
| 6 | **TCS/SemaphoreSlim drain** in `NativeResourceDisposer` instead of 10 ms polling. | Low | Trivial |
| 7 | **Parse GGUF metadata once per load** (fold integrity walk into a single header pass; cache per path+mtime). | Low-Medium (cold start) | Low |

### Tier 1 — Memory & throughput optimization (1–2 weeks)

| # | Change | Impact | Effort |
|---|--------|--------|--------|
| 8 | **Rebuild or disable speculative decoding.** Either implement true batched verification (`llama_batch` of K drafts + one `llama_decode`, accept longest prefix) or default the feature off. Measure the delta with the real benchmark before/after. | High (decode throughput, TTFT) | Medium |
| 9 | **Token-aligned KV prefix reuse.** Rewind by the executor's evaluated token count instead of re-tokenizing a char-level prefix; reduces decode-failure fallbacks (the "crater to ~20 tps" re-prefill loop). | High (long sessions) | Medium |
| 10 | **Cross-session prefix KV caching** for the static system prompt (+tool schema) using the existing `SaveStateAsync`/`LoadStateAsync` (native `llama_state`). | High (TTFT on every new session) | Medium |
| 11 | **Verify the auto-updated native engine** — pin known-good release assets and verify SHA-256 before extracting/deploying DLLs. (Security, but cheap.) | High (security) | Low-Medium |
| 12 | **Measure actual VRAM** after context creation (NVML delta) and feed it back into `OffloadStrategy` instead of pure heuristics. | Medium (footprint) | Medium |

### Tier 2 — Long-term architectural overhauls

| # | Change | Impact | Effort |
|---|--------|--------|--------|
| 13 | **Continuous/in-flight batching.** Multi-context pool with per-context locks (fork's `BatchedExecutor`/`Conversation`), or multi-sequence `llama_batch` decode with managed prefill/decode scheduling. | High (concurrent users) | Large |
| 14 | **Native context shifting + compression** for very long sessions (llama.cpp `llama_kv_cache_seq_shift` with a rolling window) to replace app-level LLM summarization as the primary long-context mechanism; keep summarization only for semantic memory. | High (true unlimited context at speed) | Large |
| 15 | **Instrument with ETW/PerfView** (`[EventSource]` markers around prefill/decode; optionally expose llama.cpp `llama_perf` counters via the fork) and wire a CI benchmark that runs with a real model artifact (e.g., a small 1–2B GGUF) so 60+ tps / TTFT budgets are enforced outside the dev machine. | Medium (regression protection) | Medium |

**Ordering rationale:** Tier 0 items are independent, low-risk, and remove the largest *known* wasted work (phantom re-iterations, re-tokenization, fsync-per-message, polling). Tier 1 #8–#10 directly attack the two headline goals (decode tps and TTFT) with bounded risk — #9 especially, since the current partial-prefix path is the documented source of long-session slowdowns. Tier 2 #13/#14 are the only way to get true concurrent throughput and native unlimited-context; everything else in the stack (prefix reuse, compression, guards) is already built to compose with them.

---

## Appendix A — Files inspected (hot path)

`InferenceEngine.cs` (1971), `SpeculativeEngine.cs` (708), `SpeculativeDecodingService.cs` (273), `NativeEngineManager.cs` (738), `NativeResourceDisposer.cs`, `InferenceSession.cs`, `KvCacheCalculator.cs`, `NGramLookupEngine.cs`, `SpecialTokenFilterPipeline.cs`, `TokenSpeedTracker.cs`, `ModelPool.cs` (358), `GgufMetadataReader.cs` (474), `GgufCompatibilityAdapter.cs`, `ContextOrchestrator.cs` (656), `MessageStore.cs` (638), `ChatEngine.cs` (1765), `ChatStreamParser.cs` (346), `GenerationLoopDetector.cs` (586), `ModelMessageQueue.cs`, `OffloadStrategy.cs`, `CpuAffinityHelper.cs`, `GpuProfiler.cs` (424), `StartupSequence.cs`, both `.csproj`, `patches/README.md`, vendored fork `LLamaExecutorBase.cs`/`LLamaInteractExecutor.cs`, all of `tests/`.

## Appendix B — Test suite state

- Build: **0 warnings / 0 errors**.
- `dotnet test`: **264 passed / 2 skipped / 0 failed** (skips = real-model throughput benchmarks, gated by `KLYDIS_BENCHMARK_REAL=1` + `KLYDIS_MODEL_PATH` or a local `.gguf`).
- Real-model stress tests reference `C:\Users\corne\.klydis\models\Qwythos-9B-Claude-Mythos-5-1M-Q4_K_M.gguf` and early-return when absent — keep `KLYDIS_MODEL_PATH`-style indirection instead of hardcoded user paths for CI portability.

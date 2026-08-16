# Vendored LLamaSharp fork (`third_party/LLamaSharp`)

Klydis vendors a **patched copy of LLamaSharp** under `third_party/LLamaSharp`
instead of referencing the NuGet package. This document explains why, what the
fork changes, and how to regenerate it against an upstream baseline.

## Why a fork?

- **Struct-aligned native ABI.** Klydis ships an auto-updated llama.cpp engine
  (see `src/Klydis.Core/Inference/NativeEngineManager.cs`). The managed structs
  passed to `llama.cpp` (e.g. `n_outputs_max_per_seq`) must match the exact ABI
  of the shipped native build, which lags the NuGet-published LLamaSharp. The
  fork pins those layouts and adds a `llama_max_tensor_buft_overrides` binding.
- **MTMD support** (`MtmdWeights`, `MtmdModel`, `mtmd_*` bindings) for
  multimodal models — not in the upstream NuGet builds Klydis targets.
- **`Batched/Conversation`** and the `InteractiveExecutor` refinements the chat
  loop depends on.
- The fork also keeps the build **warning-clean** for the Klydis solution
  (`TreatWarningsAsErrors` can be enabled; see root `README`).

## Patch surface

| Area | Files | Notes |
|------|-------|-------|
| Native ABI structs / consts | `LLama/Native/*.cs` | struct layout + new bindings (`llama_max_tensor_buft_overrides`, `llama_time_us`) |
| MTMD | `LLama/MtmdWeights.cs`, `LLama/Native/SafeMtmdModelHandle.cs`, `LLama/Native/NativeApi.Mtmd.cs` | multimodal decode/embedding |
| Batched conversation | `LLama/Batched/Conversation.cs` | `Prompt(...)` overloads used by the app |
| Executors | `LLama/LLamaInstructExecutor.cs`, `LLama/LLamaInteractExecutor.cs`, `LLama/Extensions/IntPtrExtensions.cs` | warnings/docs + `InstructExecutor(LLamaContext, MtmdWeights, ...)` |
| Params | `LLama/Native/LLamaParamsFitStatus.cs` | enum used by fit-status reporting |

## Regenerating / upgrading

The fork currently tracks an upstream baseline by hand. To upgrade:

1. Copy the upstream `LLama/` sources you want over `third_party/LLamaSharp/LLama/`.
2. Re-apply the structural deltas above (struct alignment, MTMD, batched
   additions). There is no automated patch file yet — the deltas are small and
   intentional; a `git diff` against the NuGet package sources is the reference.
3. Rebuild and run the full test suite (`dotnet test tests/Klydis.Core.Tests`),
   which exercises the native ABI via the real-model stress tests.

**WARNING:** do not bump the vendored source *and* the auto-updated native
engine independently. The managed ABI must match the native `llama.dll` that
`NativeEngineManager` deploys; struct-layout drift fails with
"Unsupported ctx type" at runtime.


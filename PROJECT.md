# Project: Klydis Beta - Model Synchronization & Async Unloading Fixes

## Architecture & Objectives
1. **R1: Fix Model State Synchronization & Selection Race Conditions**: Ensure UI state (`IsModelReady`, `SelectedModelId`) accurately reflects `InferenceEngine` and `ModelPool` state. Prevent race conditions and un-synchronized concurrent `LoadModelAsync` / `UnloadModelAsync` calls. Properly handle and report failed/cancelled model loads.
2. **R2: Eliminate Model Unloading Latency Spikes & System Freezing**: Asynchronously offload native `LLamaWeights` and `LLamaContext` disposal and VRAM cleanup to background worker threads off the WPF UI Thread.
3. **R3: Comprehensive Verification & Stability Testing**: Implement automated unit and integration tests covering rapid model switching, error handling, and cancellation without UI thread blocking.

## Milestones

| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Exploration & Architecture Analysis | Investigate UI ViewModels, `InferenceEngine`, `ModelPool`, disposal patterns, locking, and race conditions | None | DONE |
| M2 | R1: Fix Model State Sync & Race Conditions | Implement synchronization between UI state (`IsModelReady`, `SelectedModelId`) and `InferenceEngine`/`ModelPool`. Queue/cancel concurrent loads safely. Protect native handles and await background generation. | M1 | DONE |
| M3 | R2: Async VRAM & Model Unloading Cleanup | Offload `LLamaWeights` and `LLamaContext` disposal to background threads off WPF UI thread. Implement `INativeResourceDisposer` / `IAsyncDisposable`. | M1, M2 | DONE |
| M4 | R3: Unit & Integration Test Suite | Implement automated tests in `Klydis.Core.Tests` for model switching, rapid selection, failure recovery, and state reflection. | M2, M3 | DONE |
| M5 | E2E Verification & Forensic Integrity Audit | Reviewer code analysis, Challenger stress testing, and Forensic Auditor integrity verification. | M2, M3, M4 | DONE |

## Code Layout & Key Files
- `src/Klydis.Core/Inference/InferenceEngine.cs`: Core inference engine, model loading/unloading, native weight handles, `ModelStateChanged` event raising.
- `src/Klydis.Core/Inference/ModelPool.cs`: Model caching, lifecycle management, VRAM offload.
- `src/Klydis.Core/Inference/SpeculativeEngine.cs`: Native draft engine handle protection and thread-safe unloading.
- `src/Klydis.Core/Inference/INativeResourceDisposer.cs`: Async off-thread native handle disposal channel.
- `src/Klydis.App/ViewModels/ChatViewModel.cs`: Main WPF ViewModel for model selection, state reporting, and request cancellation/serialization.
- `src/Klydis.App/ViewModels/ModelLibraryViewModel.cs`: Model card state management and loading integration.
- `src/Klydis.App/ViewModels/MainViewModel.cs`: Application state & model loading orchestration.
- `tests/Klydis.Core.Tests/`: Comprehensive test suite (83/83 passing unit and empirical stress tests).

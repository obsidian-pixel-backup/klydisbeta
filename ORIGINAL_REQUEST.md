# Original User Request

## Initial Request — 2026-08-01T14:16:50Z

Full sweep of the KlydisBeta codebase to optimize GPU inference engine performance for 9B parameter GGUF models, achieving 60+ tokens/second throughput while maintaining unlimited context budgets via rolling/shifting memory and KV cache compression.

Working directory: e:\DEVELOPER PROJECTS\klydisbeta
Integrity mode: development

## Requirements

### R1. 60+ Tokens/Sec GPU Inference Throughput Optimization (9B Model)
Optimize the GPU inference architecture in `InferenceEngine.cs`, native CUDA parameters, micro-batching (`UBatchSize`), prompt prefill and token generation pipelines, FlashAttention enablement, and memory mapping so that 9B parameter models consistently achieve 60+ tokens/second generation throughput on GPU hardware.

### R2. Unlimited Context Budget with Rolling/Shifting Memory & Compression
Ensure context windows and context budgets are not artificially capped or restricted. Implement and optimize rolling/shifting context windows and KV cache compression in `ContextOrchestrator.cs` and `InferenceEngine.cs` so that long conversation histories dynamically roll and compress without reducing the context budget or causing VRAM/throughput bottlenecks.

### R3. Comprehensive Codebase Sweep & Empirical Benchmark Verification
Perform a complete codebase sweep across `src/Klydis.Core` and `src/Klydis.App` to remove lock contention, sync stalls, and UI/inference thread friction. Create an empirical benchmark test in `tests/Klydis.Core.Tests` that measures end-to-end token generation speed and verifies 60+ tokens/second throughput alongside zero regressions across all existing unit tests.

## Acceptance Criteria

### Throughput & Performance
- [ ] 9B parameter model benchmark achieves >= 60.0 tokens/second generation throughput on GPU.
- [ ] Micro-batching (`UBatchSize`), `BatchSize`, thread allocation, and FlashAttention are optimized for low token latency.

### Context Budget & Memory Systems
- [ ] Context budget remains unlimited with zero forced artificial truncation limits.
- [ ] Rolling/shifting memory window compaction and KV cache compression in `ContextOrchestrator` maintain active history seamlessly without memory or VRAM leaks.

### Reliability & Test Suite
- [ ] Full test suite in `tests/Klydis.Core.Tests` passes with 100% success rate (including performance benchmark tests).
- [ ] No regression in model state synchronization, async model unloading, or UI thread responsiveness.

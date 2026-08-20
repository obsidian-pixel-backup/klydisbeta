# Project: Klydis Beta

## Architecture
Klydis Beta is an advanced autonomous agentic IDE and local/cloud multi-model execution engine built on .NET 10.0 / C# 13.
The system comprises:
- **Protocol Engine**: Extensible model protocol adapters (`IModelProtocol`) and dynamic Jinja2/Fluid chat templating engine for formatting prompts, tool definitions, and parsing reasoning/tool calls across all major model families.
- **Dynamic GBNF Sampling**: Dynamic grammar synthesis for strict JSON schema and XML tool call generation with native llama.cpp compliance.
- **Inference Gateway**: Unified `IInferenceProvider` abstraction managing in-process GGUF (LLamaSharp) and cloud/local endpoints (OpenAI, Anthropic Claude with extended thinking, DeepSeek, Google Gemini, Ollama, vLLM, SGLang) with Polly resilience.
- **Memory & KV Cache**: Token-level prefix pinning, KV cache quantization (Q8_0/Q4_0), M-RoPE alignment, and session snapshot state serialization.
- **Unbounded Agentic Runtime**: 6-phase OODA-VR (`Observe`, `Orient`, `Decide`, `Act`, `Verify`, `Reflect`) execution state machine with uncapped long-horizon iteration, durable SQLite checkpoints, and tool output disk offloading.
- **Workbench & Tools**: Safe workspace sandboxing, unified diff patching, line-range editing (`replace_lines`), structural AST replacement, and managed background process execution (`manage_process`).
- **Closed-Loop Verification**: Multi-language compiler/test verifiers (.NET, TS, Py, Rust, Go) providing automated diagnostic feedback for targeted self-healing.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---|---|---|---|
| F1 | Universal Model Protocol Adapters | Llama 3.x, DeepSeek-R1/V3, Mistral/Codestral, Gemma 2/3, Phi-3/4, Command-R+ adapters and dynamic profile binding | M1 | ORIGINAL_REQUEST §R1 |
| F2 | Dynamic Jinja2 / Fluid Chat Templating | Fluid engine rendering GGUF `tokenizer.chat_template` dynamically with tools, thinking tags, generation prompts | M1 | ORIGINAL_REQUEST §R2 |
| F3 | Dynamic GBNF Grammar Sampling | Dynamic grammar synthesis for JSON schemas and Anthropic XML tool calls with underscore-free rule sanitization | M1 | ORIGINAL_REQUEST §R4 |
| F4 | Multi-Provider Inference Gateway | `IInferenceProvider` abstraction with OpenAI, Anthropic, DeepSeek, Gemini, Ollama/vLLM/SGLang, and Polly resilience | M2 | ORIGINAL_REQUEST §R3 |
| F5 | KV Cache Optimization & State Snapshots | Token prefix pinning, KV quantization (Q8_0, Q4_0), M-RoPE token alignment, `.kvstate` task resumption | M2 | ORIGINAL_REQUEST §R6 |
| F6 | Speculative Decoding & Telemetry | Token-level PLSD speculative decoding, multi-backend detection (CUDA, Vulkan, ROCm, Metal), live telemetry, benchmarking | M2 | ORIGINAL_REQUEST §R10 |
| F7 | Unbounded Long-Horizon Execution | Removal of hardcoded turn limits (MAX_ITERATIONS=100), anti-stagnation heuristics, SQLite checkpointing, tool output disk offload | M3 | ORIGINAL_REQUEST §R5 |
| F8 | 6-Phase Agentic Loop FSM | Complete Observe-Orient-Decide-Act-Verify-Reflect state machine, scratchpad reasoning, phase-gated tool obligations, replay protection | M3 | ORIGINAL_REQUEST §R7 |
| F9 | Resilient Tool Ergonomics & Sandboxing | Line-range editing (`replace_lines`), background process management (`manage_process`), structural replacement, workspace containment | M4 | ORIGINAL_REQUEST §R8 |
| F10 | Multi-Language Self-Healing Gates | Compiler/test diagnostic parsers & verifiers for .NET, TS, Py, Rust, Go with closed-loop context repair injection | M4 | ORIGINAL_REQUEST §R9 |
| F11 | E2E Integration & Acceptance Verification | Pass 100% of E2E test suite (Tiers 1-4) with zero compilation errors and full acceptance criteria satisfaction | M5 | ORIGINAL_REQUEST §Acceptance Criteria |
| F12 | Adversarial Coverage Hardening | Tier 5 white-box adversarial stress testing, boundary condition verification, forensic integrity audit | M5 | Project Pattern §Phase 2 |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|---|---|---|---|
| M0 | E2E Testing Track | Requirement-driven opaque-box test runner, harness, and comprehensive test cases (Tiers 1-4) | none | IN_PROGRESS |
| M1 | Protocols, Chat Templates & Grammars | R1 (Universal Adapters), R2 (Fluid Jinja2 Templates), R4 (Dynamic GBNF Generator) | none | IN_PROGRESS |
| M2 | Multi-Provider Gateway & Performance | R3 (Inference Gateway & Providers), R6 (KV Cache & Snapshots), R10 (Speculative Decoding & Telemetry) | none | IN_PROGRESS |
| M3 | Unbounded Horizon & 6-Phase Agent Loop | R5 (Budget Elimination, SQLite checkpoints, offloading), R7 (6-Phase OODA-VR FSM, replay protection) | none | IN_PROGRESS |
| M4 | Resilient Tools & Self-Healing Gates | R8 (replace_lines, manage_process, sandboxing), R9 (Multi-language verifiers & self-healing loop) | none | IN_PROGRESS |
| M5 | Final Milestone: 100% E2E Pass & Tier 5 Hardening | Full integration verification (Tiers 1-4) followed by Tier 5 adversarial hardening and forensic audit | M0, M1, M2, M3, M4 | PLANNED |

## Interface Contracts
### Protocol Engine ↔ Chat Engine / Prompt Pipeline
- `IModelProtocol.BuildPrompt(ConversationHistory history, IReadOnlyList<ToolDefinition>? tools, ModelProfile profile, PromptFormatOptions? options)` -> `string`
- `IModelProtocol.ParseOutput(string rawOutput, ModelProfile profile)` -> `ParsedModelOutput` (Text, Reasoning, ToolCalls)
- `IModelProtocol.FormatToolResult(string toolName, string callId, string output, ModelProfile profile)` -> `string`
- `IModelProtocol.GetStopTokens(ModelProfile profile)` -> `IReadOnlyList<string>`
- `IChatTemplateEngine.Render(string chatTemplate, ConversationHistory history, IReadOnlyList<ToolDefinition>? tools, ModelProfile profile, TemplateRenderOptions? options)` -> `string`

### Grammar Synthesis ↔ Native Inference
- `DynamicGbnfGenerator.GenerateToolGrammar(IReadOnlyList<ToolDefinition> tools, ToolProtocol protocol, GrammarOptions? options)` -> `string` (GBNF grammar text with sanitized `[a-zA-Z0-9-]` rule names)

### Inference Gateway ↔ Consumers (ChatEngine / App)
- `IInferenceProvider.ProviderName` -> `string`
- `IInferenceProvider.Capabilities` -> `ProviderCapabilities`
- `IInferenceProvider.GenerateAsync(ProviderInferenceRequest request, CancellationToken ct)` -> `Task<ProviderInferenceResponse>`
- `IInferenceProvider.StreamTokensAsync(ProviderInferenceRequest request, CancellationToken ct)` -> `IAsyncEnumerable<ProviderInferenceStreamChunk>`
- `IInferenceEngine` facade wraps `InferenceGateway` and preserves existing in-process and remote transparent dispatch.

### Agent Loop ↔ Action Gate & Tool Sandbox
- `AgentLoopStateMachine.StepAsync(AgentLoopContext context, CancellationToken ct)` -> `Task<AgentLoopStepResult>`
- `ActionGate.ValidateAction(ActionObligation obligation, ToolCall call, AgentPhase currentPhase)` -> `ActionValidationResult`
- `IToolExecutor.ExecuteAsync(string toolName, string argumentsJson, ToolExecutionContext ctx, CancellationToken ct)` -> `Task<ToolExecutionResult>`

### Verification Engine ↔ Closed-Loop Repair
- `IEvidenceVerifier.VerifyAsync(VerificationRequest request, CancellationToken ct)` -> `Task<VerificationEvidence>`
- `EvidenceVerificationEngine.GenerateRepairDirectives(IReadOnlyList<VerificationEvidence> failures)` -> `IReadOnlyList<SelfRepairDirective>`

## Code Layout
- `src/Klydis.Core/Protocol/`: Protocol adapters, `IModelProtocol.cs`, `ModelProfile.cs`, `ProtocolRegistry.cs`, `FluidChatTemplateEngine.cs`
- `src/Klydis.Core/Inference/`: `DynamicGbnfGenerator.cs`, `InferenceGateway.cs`, `IInferenceProvider.cs`, `Providers/`, `PrefixPinningCoordinator.cs`, `KvStateSnapshotManager.cs`, `TokenPromptLookupEngine.cs`, `BackendDetector.cs`
- `src/Klydis.Core/Chat/`: `ChatEngine.cs`, `GoalBudget.cs`, `ToolExecutor.cs`
- `src/Klydis.Core/Tasks/`: `AgentLoopStateMachine.cs`, `AgentSupervisor.cs`, `ActionGate.cs`, `ExecutionEvidenceLedger.cs`, `WorkspaceBoundaryValidator.cs`
- `src/Klydis.Core/Diagnostics/`: `DiagnosticsParser.cs`, `IEvidenceVerifier.cs`, `Verifiers/` (.NET, TS, Py, Rust, Go)
- `src/Klydis.Core/Workbench/`: `UnifiedDiff.cs`, `StructuralReplacer.cs`
- `tests/Klydis.Core.Tests/`: Unit and integration test fixtures per module
- `tests/Klydis.E2ETests/`: Dedicated opaque-box E2E test suite (Tiers 1-4)

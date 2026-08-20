# Original User Request

## 2026-08-19T05:35:46Z

Develop and complete the full multi-model protocol engine, Jinja2 template execution, cloud/local multi-provider gateway, uncapped long-horizon execution, resilient tool patching, and self-healing compiler/linter verification loop for Klydis Beta.

Working directory: e:\DEVELOPER PROJECTS\klydisbeta
Integrity mode: development

## Requirements

### R1. Universal Model Protocol Adapters & Dialects
Implement first-class protocol adapters for all major model families: Llama 3.x (Llama3ProtocolAdapter.cs), DeepSeek-R1 / V3 (DeepSeekProtocolAdapter.cs), Mistral / Codestral (MistralProtocolAdapter.cs), Gemma 2/3 (GemmaProtocolAdapter.cs), Phi-3/4 (PhiProtocolAdapter.cs), and Command-R+ (CommandRProtocolAdapter.cs). Refactor GenericJsonProtocolAdapter.cs, AntmlProtocolAdapter.cs, and OpenAiProtocolAdapter.cs to bind dynamically to their model profiles rather than hardcoding generic defaults.

### R2. Dynamic Jinja2 / Fluid Chat Templating Engine
Integrate a managed template engine (Fluid) to compile and render embedded GGUF tokenizer.chat_template strings dynamically with native support for tools, generation prompts, thinking tags, and custom overrides.

### R3. Multi-Provider Inference Gateway
Generalize inference beyond in-process GGUF by creating an IInferenceProvider abstraction with implementations for OpenAI (GPT-4o, o1, o3-mini), Anthropic (Claude 3.5 & Claude 3.7 with extended thinking), DeepSeek API (V3 / R1), Google Gemini API (2.0 Flash/Pro), and local servers (vLLM, SGLang, Ollama) with Polly resilience and streaming.

### R4. Universal Dynamic Grammar-Constrained Sampling
Generalize ToolCallGrammar.cs into a dynamic GBNF grammar generator for arbitrary JSON schemas and Anthropic XML tool calls, complete with underscore-safe rule sanitization and fail-open recovery.

### R5. Budget Elimination & Unbounded Long-Horizon Execution
Remove all hardcoded turn limits (MAX_ITERATIONS = 100 in ChatEngine.cs), token caps, and wall-time ceilings. Ensure infinite mode runs safely with intelligent anti-stagnation heuristics, durable SQLite checkpointing, and disk offloading for large tool outputs.

### R6. KV Cache Optimization & Prefix Retention
Optimize KV cache prefix pinning across turns, support KV cache quantization (Q8_0, Q4_0), ensure M-RoPE token alignment for multi-turn reasoning models, and implement state snapshot serialization for instantaneous task resumption.

### R7. Perfect Agentic Loop (Observe–Orient–Decide–Act–Verify–Reflect)
Complete the 6-phase state machine with scratchpad reasoning, phase-gated tool obligations, dynamic replanning directives, execution replay protection, and parallel tool calling capabilities.

### R8. Tool Ergonomics, Unified Patching & Sandboxing
Implement line-range editing (replace_lines), Tree-Sitter structural replacement, high-speed file search, and background process management (manage_process) while enforcing workspace boundary containment.

### R9. Self-Healing & Closed-Loop Verification Gates
Implement compiler and runtime diagnostic parsers for .NET, TypeScript, Python, Rust, and Go that automatically inject structured error diagnostics back into context for targeted self-repair, backed by automated test runner integration.

### R10. Speculative Decoding, Throughput & Telemetry
Enhance prompt lookup speculative decoding, native backend update checks, multi-backend support (CUDA, Vulkan, ROCm, Metal), live performance telemetry, and automated comparative benchmarking.

---

## Acceptance Criteria

### Automated Build & Test Suite
- [ ] dotnet build succeeds across Klydis.Core, Klydis.App, and Klydis.Core.Tests with zero compilation errors.
- [ ] All existing and new unit/integration tests in tests/Klydis.Core.Tests/ pass cleanly via dotnet test.

### Multi-Model Protocol Verification
- [ ] Each protocol adapter correctly formats prompts, parses dialect-specific tool calls (JSON, XML, Function tags), and handles reasoning blocks for its respective model family.
- [ ] Unknown models cleanly fallback to probed capabilities without crashing native samplers.

### Unbounded Autonomous Execution
- [ ] Autonomous goal loop runs beyond 100 iterations without artificial termination when work remains.
- [ ] Tool outputs exceeding character budgets are offloaded to disk with actionable context pointers.

### Deterministic State Machine & Self-Healing
- [ ] Tool validation gate strictly rejects forbidden actions per step and prevents duplicate side-effect execution.
- [ ] Build failures and syntax errors automatically trigger targeted repair directives in the agent loop.

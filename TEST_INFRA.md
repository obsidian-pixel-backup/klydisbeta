# E2E Test Infra: Klydis Beta

## Test Philosophy
- Opaque-box, requirement-driven. No dependency on internal implementation details.
- Methodology: Category-Partition + BVA + Pairwise + Workload Testing.

## Feature Inventory
| # | Feature | Source (requirement) | Tier 1 | Tier 2 | Tier 3 |
|---|---|---|:---:|:---:|:---:|
| 1 | Universal Protocol Adapters | ORIGINAL_REQUEST §R1 | 5 | 5 | ✓ |
| 2 | Dynamic Jinja2 / Fluid Templates | ORIGINAL_REQUEST §R2 | 5 | 5 | ✓ |
| 3 | Multi-Provider Inference Gateway | ORIGINAL_REQUEST §R3 | 5 | 5 | ✓ |
| 4 | Dynamic GBNF Grammar Sampling | ORIGINAL_REQUEST §R4 | 5 | 5 | ✓ |
| 5 | Unbounded Long-Horizon Execution | ORIGINAL_REQUEST §R5 | 5 | 5 | ✓ |
| 6 | KV Cache & Prefix Retention | ORIGINAL_REQUEST §R6 | 5 | 5 | ✓ |
| 7 | 6-Phase Agentic Loop FSM | ORIGINAL_REQUEST §R7 | 5 | 5 | ✓ |
| 8 | Tool Ergonomics & Sandboxing | ORIGINAL_REQUEST §R8 | 5 | 5 | ✓ |
| 9 | Self-Healing Verification Gates | ORIGINAL_REQUEST §R9 | 5 | 5 | ✓ |
| 10 | Speculative Decoding & Telemetry | ORIGINAL_REQUEST §R10 | 5 | 5 | ✓ |

## Test Architecture
- Test runner: `dotnet test tests/Klydis.Core.Tests/Klydis.Core.Tests.csproj`
- Test case format: NUnit 4.6.1 test fixtures with comprehensive input equivalence classes, boundary value inputs, mock/simulated HTTP handlers for cloud providers, GBNF grammar syntax checks, and state machine transitions.
- Directory layout: `tests/Klydis.Core.Tests/` (and dedicated subdirectories `Protocols/`, `Inference/`, `Tasks/`, `Tools/`, `Diagnostics/`, `E2E/`).

## Real-World Application Scenarios (Tier 4)
| # | Scenario | Features Exercised | Complexity |
|---|---|---|---|
| 1 | Multi-Turn DeepSeek-R1 Autonomous Reasoning with Tool Offloading | F1, F2, F7, F8, F9 | High |
| 2 | Cloud OpenAI & Anthropic Streaming Failover with Dynamic GBNF Constraint | F3, F4, F8 | High |
| 3 | Multi-Language (.NET + Python + Rust) Closed-Loop Self-Repair | F8, F9, F10 | High |
| 4 | Instantaneous Session Resumption with KV State Snapshot & Prefix Pinning | F5, F6, F7 | High |
| 5 | Uncapped 150-Turn Goal Execution with SQLite Checkpointing & Replay Protection | F7, F8, F9 | High |
| 6 | Token Speculative Decoding Acceleration with Telemetry & Benchmark Assertion | F4, F6, F10 | Medium |

## Coverage Thresholds
- Tier 1: ≥5 per feature (50+ test cases across 10 features)
- Tier 2: ≥5 per feature boundary cases (50+ test cases across 10 features)
- Tier 3: pairwise coverage of major feature interactions (15+ cross-feature tests)
- Tier 4: ≥6 realistic application scenarios
- Total Target: ≥120 new comprehensive test cases (extending existing 712 tests)

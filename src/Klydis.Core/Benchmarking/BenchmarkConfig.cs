using System;
using System.Collections.Generic;

namespace Klydis.Core.Benchmarking;

/// <summary>
/// Categories of benchmark workloads for comparative evaluation.
/// </summary>
public enum BenchmarkWorkloadType
{
    ShortQA,            // ~50 prompt tokens, 100 max response tokens
    CodeGeneration,     // ~200 prompt tokens, 300 max response tokens
    LongReasoning,      // ~800 prompt tokens, 512 max response tokens
    MultiTurnContext    // Multi-turn conversation context (~1500 tokens)
}

/// <summary>
/// Prompt profile definition for standard benchmark evaluation.
/// </summary>
public record BenchmarkPromptProfile(
    BenchmarkWorkloadType WorkloadType,
    string Name,
    string PromptText,
    int MaxTokensToGenerate,
    float Temperature = 0.7f,
    float TopP = 0.9f
)
{
    /// <summary>
    /// Returns the standard default benchmark suite of prompt profiles.
    /// </summary>
    public static List<BenchmarkPromptProfile> GetDefaultSuite() => new()
    {
        new BenchmarkPromptProfile(
            BenchmarkWorkloadType.ShortQA,
            "ShortQA Workload",
            "Explain the concept of speculative decoding in large language models in three concise bullet points.",
            100,
            0.7f,
            0.9f
        ),
        new BenchmarkPromptProfile(
            BenchmarkWorkloadType.CodeGeneration,
            "CodeGeneration Workload",
            "Write a thread-safe C# implementation of an AsyncBoundedQueue<T> using SemaphoreSlim and System.Threading.Channels with complete exception handling.",
            300,
            0.2f,
            0.95f
        ),
        new BenchmarkPromptProfile(
            BenchmarkWorkloadType.LongReasoning,
            "LongReasoning Workload",
            "Software System Architecture Overview:\nThe high-throughput speculative inference engine uses LLamaSharp in-process execution with dual-context synchronization, Q4_0 KV cache precision, N-gram prompt lookup draftless fallback, and dynamic candidate scheduling ($K \\in [2, 10]$ based on rolling alpha). Analyze the potential memory bottlenecks, lock contention points, and cache invalidation challenges in this design.",
            512,
            0.7f,
            0.9f
        ),
        new BenchmarkPromptProfile(
            BenchmarkWorkloadType.MultiTurnContext,
            "MultiTurnContext Workload",
            "<|im_start|>system\nYou are an expert assistant.<|im_end|>\n<|im_start|>user\nWhat is KV cache rewinding?<|im_end|>\n<|im_start|>assistant\nKV cache rewinding truncates native LLamaContext sequence position without re-allocating context.<|im_end|>\n<|im_start|>user\nExplain how this optimizes speculative draft candidate verification.<|im_end|>\n<|im_start|>assistant\n",
            256,
            0.7f,
            0.9f
        )
    };
}

/// <summary>
/// Execution configuration for comparative benchmarks.
/// </summary>
public record BenchmarkConfig(
    string TargetModelPath = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
    string? DraftModelPath = "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf",
    List<BenchmarkPromptProfile>? Prompts = null,
    int WarmupIterations = 1,
    int BenchmarkIterations = 5,
    int SpeculativeDraftCount = 24,
    bool ForceAcceptDraftTokens = false,
    bool IsMockExecution = false,
    double MockBaselineTokSec = 25.0,
    double MockSpeculativeTokSec = 60.0,
    double MockDraftAcceptanceRate = 0.70,
    double MockBaselineTtftMs = 120.0,
    double MockSpeculativeTtftMs = 135.0
)
{
    public List<BenchmarkPromptProfile> EffectivePrompts => Prompts ?? BenchmarkPromptProfile.GetDefaultSuite();
}

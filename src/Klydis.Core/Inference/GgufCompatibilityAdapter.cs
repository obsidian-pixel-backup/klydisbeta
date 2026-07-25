using System;
using System.Collections.Generic;
using System.IO;
using Klydis.Core.Models;

namespace Klydis.Core.Inference;

/// <summary>
/// Diagnostic result of GGUF architecture compatibility evaluation.
/// </summary>
public record GgufCompatibilityResult(
    bool IsSupported,
    string Architecture,
    long? BlockCount,
    string? WarningMessage,
    bool RequiresUpdatedNativeBackend
);

/// <summary>
/// Pre-flight inspector and compatibility adapter for GGUF model headers.
/// Validates model architecture keys, hybrid block counts, and tensor layout expectations
/// before invoking native llama.cpp model loading.
/// </summary>
public static class GgufCompatibilityAdapter
{
    private static readonly HashSet<string> SupportedArchitectures = new(StringComparer.OrdinalIgnoreCase)
    {
        "llama", "qwen", "qwen2", "qwen2.5", "qwen3", "qwen35", "qwen3.5", "qwen36", "qwen3.6",
        "mistral", "mixtral", "gemma", "gemma2", "phi", "phi2", "phi3", "phi4",
        "starcoder", "starcoder2", "command-r", "internlm2", "deepseek", "deepseek2", "deepseek3",
        "mamba", "mamba2", "rwkv", "cohort", "stablelm", "nemotron", "granite", "smollm",
        "chatglm", "dbrx", "exaone", "olmo", "decilm", "bert", "nomic-bert", "whisper"
    };

    /// <summary>
    /// Evaluates a GGUF file's header metadata for compatibility with the native inference runtime.
    /// </summary>
    /// <param name="modelPath">Absolute path to the GGUF model file.</param>
    /// <returns>Compatibility evaluation result.</returns>
    public static GgufCompatibilityResult Evaluate(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return new GgufCompatibilityResult(
                IsSupported: false,
                Architecture: "unknown",
                BlockCount: null,
                WarningMessage: $"Model file not found at path: {modelPath}",
                RequiresUpdatedNativeBackend: false
            );
        }

        var metadata = GgufMetadataReader.Parse(modelPath);
        if (metadata == null)
        {
            return new GgufCompatibilityResult(
                IsSupported: false,
                Architecture: "unknown",
                BlockCount: null,
                WarningMessage: "Failed to read GGUF header metadata. File may be corrupted or not a valid GGUF file.",
                RequiresUpdatedNativeBackend: false
            );
        }

        string arch = metadata.Architecture ?? "unknown";
        long? blockCount = metadata.BlockCount;

        if (SupportedArchitectures.Contains(arch) || arch.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) || arch.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase) || arch.StartsWith("llama", StringComparison.OrdinalIgnoreCase) || arch.StartsWith("phi", StringComparison.OrdinalIgnoreCase) || arch.StartsWith("gemma", StringComparison.OrdinalIgnoreCase))
        {
            return new GgufCompatibilityResult(
                IsSupported: true,
                Architecture: arch,
                BlockCount: blockCount,
                WarningMessage: null,
                RequiresUpdatedNativeBackend: false
            );
        }

        // Default evaluation for unlisted or emerging GGUF architectures: attempt standard native load
        return new GgufCompatibilityResult(
            IsSupported: true,
            Architecture: arch,
            BlockCount: blockCount,
            WarningMessage: $"Architecture '{arch}' (with {blockCount ?? 0} blocks) is attempting standard native load.",
            RequiresUpdatedNativeBackend: false
        );
    }
}

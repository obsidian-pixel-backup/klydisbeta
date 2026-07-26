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
/// Reads model architecture metadata and reports diagnostic information
/// before invoking native llama.cpp model loading.
/// No architecture whitelist is used — any architecture is passed through to llama.cpp,
/// which will report its own errors if the architecture is truly unsupported.
/// </summary>
public static class GgufCompatibilityAdapter
{
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
                WarningMessage: "Failed to read GGUF header metadata. File may be corrupted, truncated, or not a valid GGUF file.",
                RequiresUpdatedNativeBackend: false
            );
        }

        string arch = metadata.Architecture ?? "unknown";
        long? blockCount = metadata.BlockCount;

        return new GgufCompatibilityResult(
            IsSupported: true,
            Architecture: arch,
            BlockCount: blockCount,
            WarningMessage: null,
            RequiresUpdatedNativeBackend: false
        );
    }
}

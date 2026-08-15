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
    bool RequiresUpdatedNativeBackend)
{
    /// <summary>The GGUF tokenizer pre-type (tokenizer.ggml.pre), if declared by the model.</summary>
    public string? PreTokenizer { get; init; }
}

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
    /// Native backend shipped with this build: LLamaSharp 0.27.0 (llama.cpp 3f7c29d318e317b63f54c558bc69803963d7d88c).
    /// </summary>
    public const string BundledNativeBackendLabel = "LLamaSharp 0.27.0";

    /// <summary>
    /// Tokenizer pre-types (tokenizer.ggml.pre) that the BUNDLED native backend cannot load
    /// because they were added to llama.cpp after the bundled version. A model declaring one of
    /// these will fail natively with "unknown pre-tokenizer type: '&lt;X&gt;'" — it is a vocab
    /// limitation, NOT an architecture problem, so we flag it pre-flight with an actionable
    /// message instead of a misleading "architecture not supported" error.
    ///
    /// VERIFIED against the bundled llama.dll (CUDA12 win-x64 + CPU avx2) and llama.cpp history:
    ///   - "minicpm5": added to llama.cpp in PR #23384 (post-b9354), absent from the bundled binary.
    ///
    /// IMPORTANT: when LLamaSharp / the bundled llama.cpp is upgraded, re-verify this list —
    /// entries move from here to supported once the new backend lands. Keep it minimal and
    /// deliberately conservative: anything not listed here passes through to the native loader,
    /// whose own error is diagnosed precisely at load time.
    /// </summary>
    private static readonly HashSet<string> KnownRequiresUpdatedNativeBackendPreTokenizers = new(StringComparer.OrdinalIgnoreCase)
    {
        "minicpm5"
    };

    /// <summary>
    /// Evaluates a GGUF file's header metadata for compatibility with the native inference runtime.
    /// </summary>
    /// <param name="modelPath">Absolute path to the GGUF model file.</param>
    /// <param name="usesUpdatedNativeBackend">
    /// When true, an updated native engine (newer than the bundled one) is active, so the
    /// hardcoded pre-tokenizer limitation list does not apply — newer tokenizers pass through
    /// to the native loader, which reports its own error if it still can't load them.
    /// Defaults to auto-detection: true when a custom native engine is installed in
    /// %USERPROFILE%\.klydis\native\.
    /// </param>
    /// <returns>Compatibility evaluation result.</returns>
    public static GgufCompatibilityResult Evaluate(string modelPath, bool? usesUpdatedNativeBackend = null)
    {
        bool hasUpdatedBackend = usesUpdatedNativeBackend ?? NativeEngineManager.HasCustomNativeEngine();
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

        // Cached: the same file was already header-parsed by InferenceEngine.LoadModelAsync
        // and the structural walk below re-reads the header as well.
        var metadata = GgufMetadataReader.ParseCached(modelPath);
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

        // Structural pre-flight check, independent of architecture: a file that declares N
        // transformer blocks but only ships tensors through blk.N-2 (or whose data region
        // overruns the file) is corrupt/truncated, NOT an unsupported model type. This runs
        // before any native load so the user gets a "re-download" message instead of a
        // misleading "architecture not supported" error.
        var integrity = GgufMetadataReader.ValidateStructuralIntegrityCached(modelPath);
        if (!integrity.IsValid)
        {
            return new GgufCompatibilityResult(
                IsSupported: false,
                Architecture: arch,
                BlockCount: blockCount,
                WarningMessage: $"Model file '{Path.GetFileName(modelPath)}' appears corrupt or truncated: {integrity.Issue}",
                RequiresUpdatedNativeBackend: false
            );
        }

        // Tokenizer pre-type check: models that declare a newer pre-tokenizer than the bundled
        // native backend knows will fail natively with "unknown pre-tokenizer type". Catching it
        // here gives the user a precise, actionable message instead of a confusing native error.
        // When an updated native engine is installed (the app auto-installs the latest llama.cpp
        // release), this list does not apply — the updated backend is expected to know newer
        // tokenizers, so the model passes through to the native loader.
        if (!hasUpdatedBackend &&
            !string.IsNullOrWhiteSpace(metadata.PreTokenizer) &&
            KnownRequiresUpdatedNativeBackendPreTokenizers.Contains(metadata.PreTokenizer))
        {
            string fileName = Path.GetFileName(modelPath);
            return new GgufCompatibilityResult(
                IsSupported: false,
                Architecture: arch,
                BlockCount: blockCount,
                WarningMessage:
                    $"Model '{fileName}' declares tokenizer pre-type '{metadata.PreTokenizer}', which the bundled native engine ({BundledNativeBackendLabel}) does not support. " +
                    $"This model requires a newer llama.cpp native backend. " +
                    $"Klydis auto-installs the latest llama.cpp engine on startup — restart Klydis while online so it can download the update (or use a different model/quantization).",
                RequiresUpdatedNativeBackend: true
            )
            {
                PreTokenizer = metadata.PreTokenizer
            };
        }

        return new GgufCompatibilityResult(
            IsSupported: true,
            Architecture: arch,
            BlockCount: blockCount,
            WarningMessage: null,
            RequiresUpdatedNativeBackend: false
        )
        {
            PreTokenizer = metadata.PreTokenizer
        };
    }
}

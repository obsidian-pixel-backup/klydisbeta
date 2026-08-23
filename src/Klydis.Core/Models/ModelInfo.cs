using System;

namespace Klydis.Core.Models;

/// <summary>
/// Source of the model indicating how it was acquired.
/// </summary>
public enum ModelSource
{
    /// <summary>
    /// Local file installed by the user.
    /// </summary>
    Local,
    
    /// <summary>
    /// Downloaded from HuggingFace.
    /// </summary>
    HuggingFace,
    
    /// <summary>
    /// Discovered automatically by the discovery service.
    /// </summary>
    Discovered,

    /// <summary>
    /// Bundled directly with the application distribution.
    /// </summary>
    Bundled
}

/// <summary>
/// Contains metadata and information about a language model.
/// </summary>
public record ModelInfo(
    string Id,
    string DisplayName,
    string FilePath,
    string FileName,
    long FileSizeBytes,
    string? Architecture,
    double? ParameterCount,
    string? QuantizationType,
    long? BlockCount,
    long? ContextLength,
    long? EstimatedVramMb,
    ModelSource Source,
    DateTime InstalledAt,
    DateTime LastUsedAt,
    string? ChecksumSha256,
    string? Role = null,
    string? RawChatTemplate = null,
    string? DetectedChatTemplate = null,
    string? ChatTemplateOverride = null
);

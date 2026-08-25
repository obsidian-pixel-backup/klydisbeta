using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Models;

/// <summary>
/// Result of a model integrity verification.
/// </summary>
public sealed record ModelIntegrityResult(bool IsValid, bool IsMissing, string? Issue)
{
    public static ModelIntegrityResult Valid { get; } = new(true, false, null);

    public static ModelIntegrityResult Missing(string filePath)
        => new(false, true, $"Model file not found: {filePath}");

    public static ModelIntegrityResult Invalid(string issue) => new(false, false, issue);
}

/// <summary>
/// Verifies a model file against the expectations declared by its manifest instead of
/// trusting the manifest blindly. Two gates:
///
/// <list type="bullet">
/// <item><description>
/// <see cref="VerifyCheap"/> — existence + size + GGUF structural integrity + architecture.
/// Header-only and cached, so it is fast enough to run on the interactive load path and at
/// registration time.
/// </description></item>
/// <item><description>
/// <see cref="VerifyAsync"/> — the cheap gate plus a full SHA-256 of the file. Hashing a
/// multi-GB GGUF takes tens of seconds, so this belongs at install/startup (or whenever the
/// file is known to have changed), never on the per-load path.
/// </description></item>
/// </list>
/// </summary>
public static class ModelIntegrityVerifier
{
    /// <summary>
    /// Cheap, header-only verification: file exists, size matches the manifest expectation
    /// (when provided), the GGUF structure is sound, and the GGUF metadata architecture
    /// matches the manifest expectation (when provided). No full-file hashing.
    /// </summary>
    public static ModelIntegrityResult VerifyCheap(
        string filePath,
        long? expectedSizeBytes = null,
        string? expectedArchitecture = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return ModelIntegrityResult.Invalid("Model path is empty.");
        }

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return ModelIntegrityResult.Missing(filePath);
        }

        if (expectedSizeBytes is > 0 && info.Length != expectedSizeBytes.Value)
        {
            return ModelIntegrityResult.Invalid(
                $"Model size mismatch: expected {expectedSizeBytes.Value} bytes but the file is {info.Length} bytes. " +
                "The file is corrupt, truncated, or a different artifact.");
        }

        var integrity = GgufMetadataReader.ValidateStructuralIntegrityCached(filePath);
        if (!integrity.IsValid)
        {
            return ModelIntegrityResult.Invalid(integrity.Issue ?? "GGUF structural integrity check failed.");
        }

        if (!string.IsNullOrWhiteSpace(expectedArchitecture))
        {
            var metadata = GgufMetadataReader.ParseCached(filePath);
            if (metadata?.Architecture == null)
            {
                return ModelIntegrityResult.Invalid(
                    $"Model metadata does not declare an architecture (expected '{expectedArchitecture}').");
            }

            if (!metadata.Architecture.Equals(expectedArchitecture, StringComparison.OrdinalIgnoreCase))
            {
                return ModelIntegrityResult.Invalid(
                    $"Model architecture '{metadata.Architecture}' does not match expected '{expectedArchitecture}'.");
            }
        }

        return ModelIntegrityResult.Valid;
    }

    /// <summary>
    /// Full verification: the cheap gate plus a SHA-256 comparison against the manifest's
    /// pinned checksum (when provided). Expensive for multi-GB models — call from install,
    /// startup, or update paths, never from the interactive per-load path.
    /// </summary>
    public static async Task<ModelIntegrityResult> VerifyAsync(
        string filePath,
        string? expectedSha256 = null,
        long? expectedSizeBytes = null,
        string? expectedArchitecture = null,
        CancellationToken ct = default)
    {
        var cheap = VerifyCheap(filePath, expectedSizeBytes, expectedArchitecture);
        if (!cheap.IsValid)
        {
            return cheap;
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actual = await ComputeSha256Async(filePath, ct);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return ModelIntegrityResult.Invalid(
                    $"SHA-256 mismatch: expected {expectedSha256} but the file hashes to {actual}. " +
                    "The file is corrupt or a different artifact.");
            }
        }

        return ModelIntegrityResult.Valid;
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of a file. Streams the file so a multi-GB GGUF
    /// never needs to be loaded into memory.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
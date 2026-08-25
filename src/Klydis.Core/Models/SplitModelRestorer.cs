using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Models;

/// <summary>
/// Result of a model-restore pass across all scanned model directories.
/// </summary>
public sealed record SplitModelRestoreResult(int Restored, int AlreadyValid, int Failed);

/// <summary>
/// Reassembles split GGUF part files back into the full model binary the runtime loads.
///
/// Large GGUFs (Smeagle Q8_0 is ~4.3 GiB) cannot be committed to GitHub as a single file
/// (100 MB per-file push limit), so they are stored in the repository as zero-padded parts
/// named <c>&lt;fileName&gt;.partNN</c> next to their manifest. On every launch this restorer
/// ensures the real <c>.gguf</c> exists and is valid:
///
/// <list type="bullet">
/// <item><description>
/// Already present and passing the cheap gate (existence + size + GGUF structural integrity +
/// architecture) — left untouched, no expensive hashing on the startup path.
/// </description></item>
/// <item><description>
/// Missing or corrupt — reassembled from the parts and verified with a full SHA-256 against
/// the manifest's pinned checksum before it is considered restored. A failed assembly is
/// deleted so a corrupt model is never launched.
/// </description></item>
/// </list>
/// </summary>
public sealed class SplitModelRestorer
{
    private readonly ILogger<SplitModelRestorer>? _logger;
    private readonly List<string> _scanDirs;

    /// <summary>
    /// Default constructor: scans the user models directory plus the bundled model locations
    /// (application assets/models, application models, and the dev repository root).
    /// </summary>
    public SplitModelRestorer(ILogger<SplitModelRestorer>? logger = null)
        : this(logger, DiscoverDefaultScanDirs())
    {
    }

    /// <summary>
    /// Test seam: points the restorer at explicit directories instead of the discovered ones.
    /// </summary>
    internal SplitModelRestorer(ILogger<SplitModelRestorer>? logger, IEnumerable<string> scanDirs)
    {
        _logger = logger;
        _scanDirs = scanDirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Ensures every manifest-declared model in the scanned directories is present and valid,
    /// reassembling it from its part files when necessary.
    /// </summary>
    public async Task<SplitModelRestoreResult> RestoreAsync(CancellationToken ct = default)
    {
        int restored = 0, alreadyValid = 0, failed = 0;

        foreach (string dir in _scanDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // A manifest may sit directly in a scanned dir or in a subdirectory.
            foreach (string manifestDir in EnumerateManifestDirectories(dir))
            {
                string manifestPath = Path.Combine(manifestDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();

                try
                {
                    var outcome = await RestoreOneAsync(manifestDir, manifestPath, ct).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case RestoreOutcome.Restored: restored++; break;
                        case RestoreOutcome.AlreadyValid: alreadyValid++; break;
                        default: failed++; break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger?.LogError(ex, "Failed to restore model declared by {Manifest}", manifestPath);
                }
            }
        }

        _logger?.LogInformation(
            "Split-model restore complete: {Restored} restored, {AlreadyValid} already valid, {Failed} failed.",
            restored, alreadyValid, failed);

        return new SplitModelRestoreResult(restored, alreadyValid, failed);
    }

    private enum RestoreOutcome
    {
        Restored,
        AlreadyValid,
        Failed
    }

    private async Task<RestoreOutcome> RestoreOneAsync(string manifestDir, string manifestPath, CancellationToken ct)
    {
        Manifest target = ReadManifest(manifestPath);
        if (target.FileName == null)
        {
            _logger?.LogDebug("Manifest {Manifest} has no 'fileName'; nothing to restore.", manifestPath);
            return RestoreOutcome.AlreadyValid;
        }

        // Only ever restore models the application bundles as split parts. A user-downloaded
        // model (e.g. from the HuggingFace library into ~/.klydis/models/<repo>/) must never be
        // touched, even when its repo happens to ship a manifest.json of its own.
        if (!target.Bundled)
        {
            _logger?.LogDebug("Manifest {Manifest} is not a bundled model; skipping.", manifestPath);
            return RestoreOutcome.AlreadyValid;
        }

        string targetPath = Path.Combine(manifestDir, target.FileName);

        // Fast path: the binary already exists and passes the cheap header-only gate.
        // No full-file hashing on the startup path — that is reserved for reassembly.
        if (File.Exists(targetPath))
        {
            var cheap = ModelIntegrityVerifier.VerifyCheap(targetPath, target.SizeBytes, target.Architecture);
            if (cheap.IsValid)
            {
                _logger?.LogDebug("Model {File} already present and valid.", target.FileName);
                return RestoreOutcome.AlreadyValid;
            }

            _logger?.LogWarning(
                "Existing model {File} failed validation ({Issue}); deleting and restoring from parts.",
                target.FileName, cheap.Issue);
            try
            {
                File.Delete(targetPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Could not delete corrupt model {File}.", targetPath);
                return RestoreOutcome.Failed;
            }
        }

        // Collect the part files, ordered by their numeric suffix (part00, part01, ...).
        var parts = Directory.EnumerateFiles(manifestDir)
            .Where(f => IsPartFile(f, target.FileName))
            .OrderBy(f => PartNumber(f, target.FileName))
            .ToList();

        if (parts.Count == 0)
        {
            _logger?.LogWarning(
                "Model {File} is missing and no part files were found next to it; cannot restore.",
                target.FileName);
            return RestoreOutcome.Failed;
        }

        long totalBytes = parts.Sum(p => new FileInfo(p).Length);
        if (target.SizeBytes is > 0 && totalBytes != target.SizeBytes.Value)
        {
            _logger?.LogError(
                "Parts for {File} sum to {Sum} bytes but the manifest declares {Expected} — parts are corrupt or incomplete.",
                target.FileName, totalBytes, target.SizeBytes.Value);
            return RestoreOutcome.Failed;
        }

        _logger?.LogInformation(
            "Reassembling {File} from {Count} parts ({Sum} bytes)...",
            target.FileName, parts.Count, totalBytes);

        await AssembleAsync(parts, targetPath, ct).ConfigureAwait(false);

        // The pinned SHA-256 is the authoritative check that the reassembly is lossless.
        if (!string.IsNullOrWhiteSpace(target.Sha256))
        {
            string actual = await ModelIntegrityVerifier.ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
            if (!actual.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError(
                    "Reassembled {File} hashes to {Actual} but the manifest pins {Expected}; deleting the corrupt result.",
                    target.FileName, actual, target.Sha256);
                try { File.Delete(targetPath); } catch { /* best effort */ }
                return RestoreOutcome.Failed;
            }
        }

        _logger?.LogInformation("Model {File} restored and verified.", target.FileName);
        return RestoreOutcome.Restored;
    }

    private async Task AssembleAsync(List<string> parts, string targetPath, CancellationToken ct)
    {
        // Write to the final path only after every part has been copied; a crash mid-assembly
        // leaves no half-written file that could be mistaken for a valid model. The target is
        // then verified (size/SHA-256) before it is ever used.
        using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        foreach (string part in parts)
        {
            ct.ThrowIfCancellationRequested();
            using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enumerates directories that contain a manifest.json: the scanned dir itself plus each
    /// immediate subdirectory (models live one level deep, e.g. assets/models/&lt;model-id&gt;/).
    /// </summary>
    private static IEnumerable<string> EnumerateManifestDirectories(string dir)
    {
        if (File.Exists(Path.Combine(dir, "manifest.json")))
        {
            yield return dir;
        }

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            if (File.Exists(Path.Combine(sub, "manifest.json")))
            {
                yield return sub;
            }
        }
    }

    private static bool IsPartFile(string path, string fileName)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith(fileName + ".part", StringComparison.OrdinalIgnoreCase);
    }

    private static int PartNumber(string path, string fileName)
    {
        string name = Path.GetFileName(path);
        string suffix = name.Substring(fileName.Length + ".part".Length);
        return int.TryParse(suffix, out int n) ? n : int.MaxValue;
    }

    private record Manifest(string? FileName, long? SizeBytes, string? Sha256, string? Architecture, bool Bundled);

    private static Manifest ReadManifest(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        string? fileName = null;
        long? sizeBytes = null;
        string? sha256 = null;
        string? architecture = null;
        bool bundled = false;

        if (root.TryGetProperty("fileName", out var fn) && !string.IsNullOrWhiteSpace(fn.GetString()))
        {
            fileName = fn.GetString();
        }
        if (root.TryGetProperty("sizeBytes", out var sz) && sz.TryGetInt64(out long size))
        {
            sizeBytes = size;
        }
        if (root.TryGetProperty("sha256", out var sha) && !string.IsNullOrWhiteSpace(sha.GetString()))
        {
            sha256 = sha.GetString();
        }
        if (root.TryGetProperty("architecture", out var arch) && !string.IsNullOrWhiteSpace(arch.GetString()))
        {
            architecture = arch.GetString();
        }
        if (root.TryGetProperty("bundled", out var bundledProp) && bundledProp.ValueKind == JsonValueKind.True)
        {
            bundled = true;
        }

        return new Manifest(fileName, sizeBytes, sha256, architecture, bundled);
    }

    private static List<string> DiscoverDefaultScanDirs()
    {
        var dirs = new List<string>();

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(userHome, ".klydis", "models"));

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        dirs.Add(Path.Combine(baseDir, "assets", "models"));
        dirs.Add(Path.Combine(baseDir, "models"));

        string? devRoot = FindDevRoot(baseDir);
        if (devRoot != null)
        {
            dirs.Add(Path.Combine(devRoot, "assets", "models"));
        }

        return dirs;
    }

    private static string? FindDevRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KlydisBeta.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
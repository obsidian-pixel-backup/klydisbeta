using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

namespace Klydis.Core.Capabilities.Providers;

/// <summary>
/// Capability: filesystem.read
/// Reads content from a file with optional line-range slicing.
/// </summary>
public sealed class FilesystemReadCapability : ICapability
{
    public string Id => "filesystem.read";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Reads text content from a file. Supports optional 1-indexed line range slicing (start_line, end_line) for large files.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Absolute or relative file path to read.", true),
            new("start_line", "integer", "Optional 1-based start line (inclusive).", false),
            new("end_line", "integer", "Optional 1-based end line (inclusive).", false),
            new("encoding", "string", "Text encoding (default: 'utf-8').", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return Task.FromResult(PreconditionCheckResult.Failed($"File not found: '{fullPath}'"));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
            int? startLine = request.GetParam<int?>("start_line");
            int? endLine = request.GetParam<int?>("end_line");

            var allLines = await File.ReadAllLinesAsync(fullPath, ct);
            int totalLines = allLines.Length;

            int start = Math.Clamp((startLine ?? 1) - 1, 0, Math.Max(0, totalLines - 1));
            int count = endLine.HasValue
                ? Math.Clamp(endLine.Value - start, 0, totalLines - start)
                : totalLines - start;

            var slicedLines = totalLines > 0 ? allLines.Skip(start).Take(count).ToArray() : Array.Empty<string>();
            string content = string.Join(Environment.NewLine, slicedLines);

            string sha256;
            using (var sha = SHA256.Create())
            await using (var fs = File.OpenRead(fullPath))
            {
                var hashBytes = await sha.ComputeHashAsync(fs, ct);
                sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            var data = new
            {
                Path = fullPath,
                TotalLines = totalLines,
                StartLine = start + 1,
                EndLine = start + slicedLines.Length,
                LinesReturned = slicedLines.Length,
                Sha256 = sha256,
                Content = content
            };

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: content,
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?>
                {
                    ["Path"] = fullPath,
                    ["TotalLines"] = totalLines,
                    ["Sha256"] = sha256
                }
            );

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null)
            return Task.FromResult(VerificationResult.Failed("Failed to read file."));

        string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
        var facts = new List<FactAssertion>
        {
            new("filesystem", fullPath, "exists", true, TimeSpan.FromMinutes(10), Id)
        };

        return Task.FromResult(VerificationResult.Verified("File read verified.", facts));
    }
}

/// <summary>
/// Capability: filesystem.write
/// Writes text content to a file with backup and side-effect tracking.
/// </summary>
public sealed class FilesystemWriteCapability : ICapability
{
    public string Id => "filesystem.write";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Writes text content to a destination file. Creates parent directories automatically. Backs up existing content.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Target file path.", true),
            new("content", "string", "The full text content to write.", true),
            new("overwrite", "boolean", "Whether to overwrite if file exists (default: true).", false)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));

        string? content = request.GetParam<string>("content");
        if (content is null)
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'content' is required."));

        string fullPath = Path.GetFullPath(path);
        bool overwrite = request.GetParam<bool>("overwrite", true);
        if (File.Exists(fullPath) && !overwrite)
        {
            return Task.FromResult(PreconditionCheckResult.Failed($"File already exists and overwrite is false: '{fullPath}'"));
        }

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
            string content = request.GetParam<string>("content")!;

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool existed = File.Exists(fullPath);
            string? backupContent = existed ? await File.ReadAllTextAsync(fullPath, ct) : null;

            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(
                    Kind: existed ? SideEffectKind.FileMutated : SideEffectKind.FileCreated,
                    Target: fullPath,
                    Description: existed ? $"Overwrote {fullPath} ({content.Length} chars)" : $"Created {fullPath} ({content.Length} chars)",
                    IsReversible: existed,
                    RevertAction: existed ? () => File.WriteAllTextAsync(fullPath, backupContent!, Encoding.UTF8) : null
                )
            };

            var data = new
            {
                Path = fullPath,
                BytesWritten = Encoding.UTF8.GetByteCount(content),
                LinesCount = content.Split('\n').Length,
                Created = !existed
            };

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: $"Successfully wrote {content.Length} characters to {fullPath}",
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?> { ["Path"] = fullPath, ["BytesWritten"] = data.BytesWritten }
            );

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence, sideEffects);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success)
            return Task.FromResult(VerificationResult.Failed("File write execution failed."));

        string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(VerificationResult.Failed($"Postcondition failed: Target file '{fullPath}' does not exist on disk after write."));
        }

        var facts = new List<FactAssertion>
        {
            new("filesystem", fullPath, "exists", true, TimeSpan.FromMinutes(10), Id)
        };
        var invalidations = new List<FactAssertion>
        {
            new("filesystem", fullPath, "sha256", "", TimeSpan.Zero, Id)
        };

        return Task.FromResult(VerificationResult.Verified($"File write verified on physical disk: {fullPath}", facts));
    }
}

/// <summary>
/// Capability: filesystem.edit
/// Applies targeted string replacement to an existing file.
/// </summary>
public sealed class FilesystemEditCapability : ICapability
{
    public string Id => "filesystem.edit";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Applies targeted text replacement inside an existing file. Matches old_text exactly, replacing with new_text.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Target file path.", true),
            new("old_text", "string", "The exact text chunk to replace.", true),
            new("new_text", "string", "The replacement text.", true)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return Task.FromResult(PreconditionCheckResult.Failed($"File not found: '{fullPath}'"));

        string? oldText = request.GetParam<string>("old_text");
        if (string.IsNullOrEmpty(oldText))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'old_text' cannot be empty."));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
            string oldText = request.GetParam<string>("old_text")!;
            string newText = request.GetParam<string>("new_text") ?? string.Empty;

            string originalContent = await File.ReadAllTextAsync(fullPath, ct);
            if (!originalContent.Contains(oldText))
            {
                return CapabilityResult.Failed(Id, $"Target text 'old_text' was not found inside '{fullPath}'.", sw.Elapsed);
            }

            int occurrences = CountOccurrences(originalContent, oldText);
            if (occurrences > 1)
            {
                return CapabilityResult.Failed(Id, $"Ambiguous edit: 'old_text' matched {occurrences} times in '{fullPath}'. Provide more surrounding context lines.", sw.Elapsed);
            }

            string updatedContent = originalContent.Replace(oldText, newText);
            await File.WriteAllTextAsync(fullPath, updatedContent, Encoding.UTF8, ct);

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(
                    Kind: SideEffectKind.FileMutated,
                    Target: fullPath,
                    Description: $"Replaced targeted chunk in {fullPath}",
                    IsReversible: true,
                    RevertAction: () => File.WriteAllTextAsync(fullPath, originalContent, Encoding.UTF8)
                )
            };

            var data = new
            {
                Path = fullPath,
                OldTextLength = oldText.Length,
                NewTextLength = newText.Length
            };

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: $"Successfully applied edit to {fullPath}",
                CollectedAtUtc: DateTime.UtcNow
            );

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence, sideEffects);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public async Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success)
            return VerificationResult.Failed("File edit execution failed.");

        string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
        string newText = request.GetParam<string>("new_text") ?? "";
        string currentContent = await File.ReadAllTextAsync(fullPath, ct);

        if (!currentContent.Contains(newText))
        {
            return VerificationResult.Failed($"Postcondition failed: File does not contain expected replacement text.");
        }

        return VerificationResult.Verified($"Targeted file edit verified: {fullPath}");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) != -1)
        {
            i += pattern.Length;
            count++;
        }
        return count;
    }
}

/// <summary>
/// Capability: filesystem.delete
/// Deletes a file or directory with verified removal.
/// </summary>
public sealed class FilesystemDeleteCapability : ICapability
{
    public string Id => "filesystem.delete";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Deletes a file or directory on the filesystem. Verifies removal postcondition.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Target file or directory path to delete.", true),
            new("recursive", "boolean", "If deleting a directory, delete recursively (default: false).", false)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            return Task.FromResult(PreconditionCheckResult.Failed($"Target path does not exist: '{fullPath}'"));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
            bool recursive = request.GetParam<bool>("recursive", false);
            bool isDirectory = Directory.Exists(fullPath);

            if (isDirectory)
            {
                Directory.Delete(fullPath, recursive);
            }
            else
            {
                File.Delete(fullPath);
            }

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(SideEffectKind.FileDeleted, fullPath, $"Deleted {(isDirectory ? "directory" : "file")}: {fullPath}")
            };

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: $"Deleted {fullPath}",
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Path = fullPath, Deleted = true }, sw.Elapsed, evidence, sideEffects));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success)
            return Task.FromResult(VerificationResult.Failed("Delete action failed."));

        string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return Task.FromResult(VerificationResult.Failed($"Postcondition failed: Target path '{fullPath}' still exists on disk."));
        }

        return Task.FromResult(VerificationResult.Verified($"Deletion verified: {fullPath} no longer exists."));
    }
}

/// <summary>
/// Capability: filesystem.copy
/// Copies files or directories to a destination path.
/// </summary>
public sealed class FilesystemCopyCapability : ICapability
{
    public string Id => "filesystem.copy";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Copies a file to a destination path.",
        Parameters: new List<CapabilityParameter>
        {
            new("source", "string", "Source file path.", true),
            new("destination", "string", "Destination file path.", true),
            new("overwrite", "boolean", "Overwrite destination if exists (default: false).", false)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? src = request.GetParam<string>("source");
        string? dst = request.GetParam<string>("destination");
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameters 'source' and 'destination' are required."));

        string fullSrc = Path.GetFullPath(src);
        if (!File.Exists(fullSrc))
            return Task.FromResult(PreconditionCheckResult.Failed($"Source file does not exist: '{fullSrc}'"));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string src = Path.GetFullPath(request.GetParam<string>("source")!);
            string dst = Path.GetFullPath(request.GetParam<string>("destination")!);
            bool overwrite = request.GetParam<bool>("overwrite", false);

            string? dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(src, dst, overwrite);

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(SideEffectKind.FileCreated, dst, $"Copied {src} -> {dst}")
            };

            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Source = src, Destination = dst }, sw.Elapsed, sideEffects: sideEffects));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Copy failed."));
        string dst = Path.GetFullPath(request.GetParam<string>("destination")!);
        if (!File.Exists(dst))
            return Task.FromResult(VerificationResult.Failed($"Postcondition failed: Destination '{dst}' does not exist."));

        return Task.FromResult(VerificationResult.Verified($"Copy verified: {dst} exists."));
    }
}

/// <summary>
/// Capability: filesystem.move
/// Renames or moves a file or directory.
/// </summary>
public sealed class FilesystemMoveCapability : ICapability
{
    public string Id => "filesystem.move";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Moves or renames a file or directory.",
        Parameters: new List<CapabilityParameter>
        {
            new("source", "string", "Source path.", true),
            new("destination", "string", "Destination path.", true)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? src = request.GetParam<string>("source");
        string? dst = request.GetParam<string>("destination");
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameters 'source' and 'destination' are required."));

        string fullSrc = Path.GetFullPath(src);
        if (!File.Exists(fullSrc) && !Directory.Exists(fullSrc))
            return Task.FromResult(PreconditionCheckResult.Failed($"Source does not exist: '{fullSrc}'"));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string src = Path.GetFullPath(request.GetParam<string>("source")!);
            string dst = Path.GetFullPath(request.GetParam<string>("destination")!);

            if (Directory.Exists(src))
            {
                Directory.Move(src, dst);
            }
            else
            {
                File.Move(src, dst);
            }

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(SideEffectKind.FileCreated, dst, $"Moved to {dst}"),
                new(SideEffectKind.FileDeleted, src, $"Moved from {src}")
            };

            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Source = src, Destination = dst }, sw.Elapsed, sideEffects: sideEffects));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Move failed."));
        string src = Path.GetFullPath(request.GetParam<string>("source")!);
        string dst = Path.GetFullPath(request.GetParam<string>("destination")!);

        if (!File.Exists(dst) && !Directory.Exists(dst))
            return Task.FromResult(VerificationResult.Failed($"Postcondition failed: Destination '{dst}' does not exist."));

        return Task.FromResult(VerificationResult.Verified($"Move verified: {dst} exists and {src} moved."));
    }
}

/// <summary>
/// Capability: filesystem.mkdir
/// Creates a directory recursively.
/// </summary>
public sealed class FilesystemMkdirCapability : ICapability
{
    public string Id => "filesystem.mkdir";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Creates a directory path recursively.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Directory path to create.", true)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string path = Path.GetFullPath(request.GetParam<string>("path")!);
            Directory.CreateDirectory(path);
            sw.Stop();
            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Path = path, Created = true }, sw.Elapsed));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Mkdir failed."));
        string path = Path.GetFullPath(request.GetParam<string>("path")!);
        if (!Directory.Exists(path))
            return Task.FromResult(VerificationResult.Failed($"Postcondition failed: Directory '{path}' does not exist."));
        return Task.FromResult(VerificationResult.Verified($"Directory verified: {path}"));
    }
}

/// <summary>
/// Capability: filesystem.list
/// Lists directory entries with file sizes and timestamps.
/// </summary>
public sealed class FilesystemListCapability : ICapability
{
    public string Id => "filesystem.list";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Lists files and subdirectories within a directory path with sizes, counts, and timestamps.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Directory path to list.", true),
            new("recursive", "boolean", "List subdirectories recursively (default: false).", false),
            new("pattern", "string", "Optional search pattern / glob (e.g. '*.cs', default: '*').", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));

        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(PreconditionCheckResult.Failed($"Directory not found: '{fullPath}'"));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
            bool recursive = request.GetParam<bool>("recursive", false);
            string pattern = request.GetParam<string>("pattern", "*") ?? "*";

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var dirInfo = new DirectoryInfo(fullPath);

            var items = new List<object>();
            foreach (var f in dirInfo.EnumerateFileSystemInfos(pattern, searchOption).Take(200))
            {
                bool isDir = (f.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                long size = isDir ? 0 : ((FileInfo)f).Length;
                items.Add(new
                {
                    Name = f.Name,
                    RelativePath = Path.GetRelativePath(fullPath, f.FullName),
                    IsDirectory = isDir,
                    SizeBytes = size,
                    LastModifiedUtc = f.LastWriteTimeUtc
                });
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, items, sw.Elapsed, evidence));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Directory list failed."));
        return Task.FromResult(VerificationResult.Verified("Directory contents enumerated."));
    }
}

/// <summary>
/// Capability: filesystem.search
/// Searches files by pattern or text content query.
/// </summary>
public sealed class FilesystemSearchCapability : ICapability
{
    public string Id => "filesystem.search";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Searches directory recursively for files matching a filename pattern or containing text content.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Root directory path to search.", true),
            new("pattern", "string", "Filename pattern (e.g. '*.json', '*.cs').", false),
            new("content_query", "string", "Optional text content to search inside files.", false),
            new("limit", "integer", "Maximum matches to return (default: 50).", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));

        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(PreconditionCheckResult.Failed($"Directory not found: '{fullPath}'"));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
            string pattern = request.GetParam<string>("pattern", "*") ?? "*";
            string? contentQuery = request.GetParam<string>("content_query");
            int limit = Math.Clamp(request.GetParam<int>("limit", 50), 1, 200);

            var matches = new List<object>();
            var files = Directory.EnumerateFiles(fullPath, pattern, SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                if (!string.IsNullOrEmpty(contentQuery))
                {
                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct);
                        if (text.Contains(contentQuery, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(new
                            {
                                Path = file,
                                RelativePath = Path.GetRelativePath(fullPath, file),
                                SizeBytes = new FileInfo(file).Length,
                                MatchedContent = true
                            });
                        }
                    }
                    catch { /* skip unreadable binary files */ }
                }
                else
                {
                    matches.Add(new
                    {
                        Path = file,
                        RelativePath = Path.GetRelativePath(fullPath, file),
                        SizeBytes = new FileInfo(file).Length
                    });
                }

                if (matches.Count >= limit) break;
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(matches, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return CapabilityResult.Succeeded(Id, matches, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Search failed."));
        return Task.FromResult(VerificationResult.Verified("Filesystem search completed."));
    }
}

/// <summary>
/// Capability: filesystem.metadata
/// Retrieves detailed file metadata, timestamps, attributes, and SHA-256 hash.
/// </summary>
public sealed class FilesystemMetadataCapability : ICapability
{
    public string Id => "filesystem.metadata";
    public CapabilityDomain Domain => CapabilityDomain.Filesystem;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Retrieves detailed file or directory metadata, sizes, creation/write timestamps, and SHA-256 hash.",
        Parameters: new List<CapabilityParameter>
        {
            new("path", "string", "Target file or directory path.", true)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? path = request.GetParam<string>("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'path' is required."));

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            return Task.FromResult(PreconditionCheckResult.Failed($"Path not found: '{fullPath}'"));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(request.GetParam<string>("path")!);
            bool isDir = Directory.Exists(fullPath);

            object data;
            if (isDir)
            {
                var di = new DirectoryInfo(fullPath);
                data = new
                {
                    Path = fullPath,
                    IsDirectory = true,
                    CreatedAtUtc = di.CreationTimeUtc,
                    LastModifiedUtc = di.LastWriteTimeUtc,
                    Attributes = di.Attributes.ToString()
                };
            }
            else
            {
                var fi = new FileInfo(fullPath);
                string sha256;
                using (var sha = SHA256.Create())
                await using (var fs = File.OpenRead(fullPath))
                {
                    var hash = await sha.ComputeHashAsync(fs, ct);
                    sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }

                data = new
                {
                    Path = fullPath,
                    IsDirectory = false,
                    SizeBytes = fi.Length,
                    CreatedAtUtc = fi.CreationTimeUtc,
                    LastModifiedUtc = fi.LastWriteTimeUtc,
                    Attributes = fi.Attributes.ToString(),
                    IsReadOnly = fi.IsReadOnly,
                    Sha256 = sha256
                };
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Metadata lookup failed."));
        return Task.FromResult(VerificationResult.Verified("File metadata verified."));
    }
}

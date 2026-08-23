using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Memory;
using Klydis.Core.Tracing;

namespace Klydis.Core.Workbench;

/// <summary>
/// Result of a workspace file mutation operation.
/// </summary>
public sealed record MutationResult(
    bool Success,
    string Path,
    string? BeforeContent,
    string? AfterContent,
    string? BeforeHash,
    string? AfterHash,
    DiffResult? Diff,
    FileChange? FileChange,
    string? ErrorMessage = null,
    bool FuzzyApplied = false);

/// <summary>
/// Authoritative mutation boundary for all workspace modifications.
/// Every file write, edit, patch, and line replacement passes through this pipeline.
/// </summary>
public sealed class WorkspaceMutationService
{
    private readonly MessageStore _messageStore;
    private readonly ArtifactDetector _artifactDetector;
    private readonly IExecutionEventStore? _eventStore;
    private readonly ILogger<WorkspaceMutationService>? _logger;

    // Sliding window of recent mutations for deduplication with WorkspaceChangeObserver
    private readonly ConcurrentDictionary<string, (string Hash, DateTime TimestampUtc)> _recentMutations = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceMutationService(
        MessageStore messageStore,
        ArtifactDetector artifactDetector,
        IExecutionEventStore? eventStore = null,
        ILogger<WorkspaceMutationService>? logger = null)
    {
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _artifactDetector = artifactDetector ?? throw new ArgumentNullException(nameof(artifactDetector));
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Checks if a file modification at the given path with the specified hash was recently
    /// handled by this mutation service (within the last threshold seconds).
    /// </summary>
    public bool IsRecentlyHandled(string path, string hash, TimeSpan threshold)
    {
        string canonical = NormalizePath(path);
        if (_recentMutations.TryGetValue(canonical, out var entry))
        {
            if (string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - entry.TimestampUtc <= threshold)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Executes a file write operation through the mutation pipeline.
    /// </summary>
    public async Task<MutationResult> WriteFileAsync(
        string path,
        string content,
        string sessionId,
        string? taskId,
        string? runId,
        string? actionId,
        string toolName = "write_file",
        CancellationToken ct = default)
    {
        return await MutateAsync(
            path,
            async targetPath =>
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(targetPath, content, ct);
                return (true, null);
            },
            sessionId,
            taskId,
            runId,
            actionId,
            toolName,
            ct);
    }

    /// <summary>
    /// Executes a targeted text replacement edit on an existing file.
    /// </summary>
    public async Task<MutationResult> EditFileAsync(
        string path,
        string oldText,
        string newText,
        string sessionId,
        string? taskId,
        string? runId,
        string? actionId,
        string toolName = "edit_file",
        CancellationToken ct = default)
    {
        bool isFuzzy = false;
        var res = await MutateAsync(
            path,
            async targetPath =>
            {
                if (!File.Exists(targetPath))
                {
                    return (false, $"File not found: {targetPath}");
                }

                string current = await File.ReadAllTextAsync(targetPath, ct);
                int idx = current.IndexOf(oldText, StringComparison.Ordinal);
                int replaceLen = oldText.Length;

                if (idx < 0)
                {
                    // Fall back to tolerant match
                    var fuzzy = FindTolerantMatch(current, oldText, out bool ambiguous);
                    if (fuzzy == null)
                    {
                        return (false, ambiguous
                            ? $"old_text appears more than once in {targetPath}."
                            : $"old_text not found in {targetPath}.");
                    }
                    isFuzzy = true;
                    idx = fuzzy.Value.Start;
                    replaceLen = fuzzy.Value.Length;
                }
                else if (current.IndexOf(oldText, idx + oldText.Length, StringComparison.Ordinal) >= 0)
                {
                    return (false, $"old_text appears more than once in {targetPath}.");
                }

                string modified = current.Substring(0, idx) + newText + current.Substring(idx + replaceLen);
                await File.WriteAllTextAsync(targetPath, modified, ct);
                return (true, null);
            },
            sessionId,
            taskId,
            runId,
            actionId,
            toolName,
            ct);

        return res with { FuzzyApplied = isFuzzy };
    }

    /// <summary>
    /// Applies a unified diff patch to an existing file.
    /// </summary>
    public async Task<MutationResult> ApplyPatchAsync(
        string path,
        string patch,
        string sessionId,
        string? taskId,
        string? runId,
        string? actionId,
        string toolName = "apply_patch",
        CancellationToken ct = default)
    {
        return await MutateAsync(
            path,
            async targetPath =>
            {
                if (!File.Exists(targetPath))
                {
                    return (false, $"File not found: {targetPath}");
                }

                string current = await File.ReadAllTextAsync(targetPath, ct);
                string? patched = UnifiedDiff.Apply(current, patch, out string? applyError);
                if (patched == null)
                {
                    return (false, $"Failed to apply patch: {applyError}");
                }
                if (patched == current)
                {
                    return (false, "Patch applied but produced no changes.");
                }

                await File.WriteAllTextAsync(targetPath, patched, ct);
                return (true, null);
            },
            sessionId,
            taskId,
            runId,
            actionId,
            toolName,
            ct);
    }

    /// <summary>
    /// Replaces a 1-indexed inclusive line range in an existing file.
    /// </summary>
    public async Task<MutationResult> ReplaceLinesAsync(
        string path,
        int startLine,
        int endLine,
        string newContent,
        string sessionId,
        string? taskId,
        string? runId,
        string? actionId,
        string toolName = "replace_lines",
        CancellationToken ct = default)
    {
        return await MutateAsync(
            path,
            async targetPath =>
            {
                if (!File.Exists(targetPath))
                {
                    return (false, $"File not found: {targetPath}");
                }

                var lines = await File.ReadAllLinesAsync(targetPath, ct);
                if (startLine < 1 || startLine > lines.Length + 1 || endLine < startLine - 1)
                {
                    return (false, $"Line range [{startLine}, {endLine}] out of bounds (file has {lines.Length} lines).");
                }

                var replacementLines = newContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                var newLines = new System.Collections.Generic.List<string>();

                for (int i = 0; i < startLine - 1 && i < lines.Length; i++)
                {
                    newLines.Add(lines[i]);
                }

                newLines.AddRange(replacementLines);

                for (int i = endLine; i < lines.Length; i++)
                {
                    newLines.Add(lines[i]);
                }

                await File.WriteAllLinesAsync(targetPath, newLines, ct);
                return (true, null);
            },
            sessionId,
            taskId,
            runId,
            actionId,
            toolName,
            ct);
    }

    /// <summary>
    /// Core pipeline: snapshots before state, executes mutation, snapshots after state,
    /// computes exact diff, records FileChange, emits execution events, registers artifact.
    /// </summary>
    public async Task<MutationResult> MutateAsync(
        string path,
        Func<string, Task<(bool Success, string? Error)>> mutationAction,
        string sessionId,
        string? taskId,
        string? runId,
        string? actionId,
        string toolName,
        CancellationToken ct = default)
    {
        string canonicalPath = NormalizePath(path);
        string? beforeContent = null;
        string? beforeHash = null;

        try
        {
            if (File.Exists(canonicalPath))
            {
                beforeContent = await File.ReadAllTextAsync(canonicalPath, ct);
                beforeHash = HashText(beforeContent);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not snapshot before-state for {Path}", canonicalPath);
        }

        var (success, error) = await mutationAction(canonicalPath);
        if (!success)
        {
            return new MutationResult(false, canonicalPath, beforeContent, null, beforeHash, null, null, null, error);
        }

        string? afterContent = null;
        string? afterHash = null;
        try
        {
            if (File.Exists(canonicalPath))
            {
                afterContent = await File.ReadAllTextAsync(canonicalPath, ct);
                afterHash = HashText(afterContent);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read after-state for {Path}", canonicalPath);
        }

        // Register in recent mutations cache for deduplication
        if (afterHash != null)
        {
            _recentMutations[canonicalPath] = (afterHash, DateTime.UtcNow);
            PruneRecentMutations();
        }

        // Compute diff
        var diff = DiffService.Diff(beforeContent, afterContent ?? string.Empty);
        bool isNewFile = beforeContent == null;

        var fileChange = new FileChange(
            ChangeId: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            TaskId: taskId,
            Path: canonicalPath,
            Tool: toolName,
            BeforeHash: beforeHash ?? "(new)",
            AfterHash: afterHash ?? "(deleted)",
            Diff: diff.Text,
            AddedLines: diff.AddedLines,
            DeletedLines: diff.DeletedLines,
            TimestampUtc: DateTime.UtcNow);

        // Durable persistence
        try
        {
            await _messageStore.AddFileChangeAsync(fileChange);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist FileChange for {Path}", canonicalPath);
        }

        // Emit lifecycle execution events
        try
        {
            _eventStore?.RecordEvent(new ExecutionEvent
            {
                SessionId = sessionId,
                TaskId = taskId,
                RunId = runId,
                ActionId = actionId,
                Category = isNewFile ? ExecutionEventCategory.FileCreated : ExecutionEventCategory.FileModified,
                ToolName = toolName,
                FilePath = canonicalPath,
                Title = $"{(isNewFile ? "Created" : "Modified")} {Path.GetFileName(canonicalPath)}",
                Summary = $"+{diff.AddedLines} -{diff.DeletedLines} lines",
                AddedLines = diff.AddedLines,
                DeletedLines = diff.DeletedLines,
                DiffText = diff.Text
            });

            _eventStore?.RecordEvent(new ExecutionEvent
            {
                SessionId = sessionId,
                TaskId = taskId,
                RunId = runId,
                ActionId = actionId,
                Category = ExecutionEventCategory.DiffCreated,
                ToolName = toolName,
                FilePath = canonicalPath,
                Title = $"Diff available: {Path.GetFileName(canonicalPath)}",
                Summary = $"+{diff.AddedLines} -{diff.DeletedLines}",
                DiffText = diff.Text,
                AddedLines = diff.AddedLines,
                DeletedLines = diff.DeletedLines
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to emit execution event for {Path}", canonicalPath);
        }

        // Durable execution event row
        try
        {
            await _messageStore.AddExecutionEventAsync(new ExecutionEventRow(
                EventId: Guid.NewGuid().ToString("N"),
                SessionId: sessionId,
                TaskId: taskId,
                RunId: runId,
                EventType: isNewFile ? "FileCreated" : "FileModified",
                TimestampUtc: DateTime.UtcNow,
                ToolName: toolName,
                Path: canonicalPath,
                PayloadJson: diff.Text));
        }
        catch { /* best effort */ }

        // Automatic artifact inspection and registration
        try
        {
            await _artifactDetector.InspectAndRegisterAsync(
                canonicalPath, sessionId, taskId, runId, actionId, afterContent, diff.Text, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Artifact detector failed on {Path}", canonicalPath);
        }

        return new MutationResult(
            true, canonicalPath, beforeContent, afterContent, beforeHash, afterHash, diff, fileChange, null);
    }

    private static (int Start, int Length)? FindTolerantMatch(string content, string needle, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(content)) return null;

        var origIndex = new System.Collections.Generic.List<int>(content.Length);
        var normChars = new System.Collections.Generic.List<char>(content.Length);
        bool lastWasLineBreak = false;
        bool lastWasSpace = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\n' || c == '\r')
            {
                if (!lastWasLineBreak) { normChars.Add('\n'); origIndex.Add(i); lastWasLineBreak = true; lastWasSpace = false; }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) { normChars.Add(' '); origIndex.Add(i); lastWasSpace = true; lastWasLineBreak = false; }
            }
            else
            {
                normChars.Add(c);
                origIndex.Add(i);
                lastWasLineBreak = false;
                lastWasSpace = false;
            }
        }

        var needleNorm = new System.Text.StringBuilder(needle.Length);
        lastWasLineBreak = false;
        lastWasSpace = false;
        for (int i = 0; i < needle.Length; i++)
        {
            char c = needle[i];
            if (c == '\n' || c == '\r')
            {
                if (!lastWasLineBreak) { needleNorm.Append('\n'); lastWasLineBreak = true; lastWasSpace = false; }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) { needleNorm.Append(' '); lastWasSpace = true; lastWasLineBreak = false; }
            }
            else
            {
                needleNorm.Append(c);
                lastWasLineBreak = false;
                lastWasSpace = false;
            }
        }

        string normContentStr = new string(normChars.ToArray());
        string normNeedleStr = needleNorm.ToString();
        if (string.IsNullOrWhiteSpace(normNeedleStr)) return null;

        int first = normContentStr.IndexOf(normNeedleStr, StringComparison.Ordinal);
        if (first < 0) return null;

        int second = normContentStr.IndexOf(normNeedleStr, first + normNeedleStr.Length, StringComparison.Ordinal);
        if (second >= 0)
        {
            ambiguous = true;
            return null;
        }

        int origStart = origIndex[first];
        int normEnd = first + normNeedleStr.Length - 1;
        int origEnd = origIndex[normEnd] + 1;
        return (origStart, origEnd - origStart);
    }

    private void PruneRecentMutations()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var kvp in _recentMutations)
        {
            if (kvp.Value.TimestampUtc < cutoff)
            {
                _recentMutations.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    private static string HashText(string text)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

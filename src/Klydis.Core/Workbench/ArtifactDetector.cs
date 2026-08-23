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
/// Result of artifact detection.
/// </summary>
public sealed record ArtifactDetectionResult(
    bool IsArtifact,
    string? ArtifactType,
    string? MimeType,
    bool IsPreviewable,
    ArtifactRecord? Record);

/// <summary>
/// Automatically inspects files after any mutation and registers them in the durable
/// artifact registry without requiring model tool cooperation.
/// </summary>
public sealed class ArtifactDetector
{
    private readonly MessageStore _messageStore;
    private readonly IExecutionEventStore? _eventStore;
    private readonly ILogger<ArtifactDetector>? _logger;
    private readonly ConcurrentDictionary<string, ArtifactRecord> _knownArtifacts = new(StringComparer.OrdinalIgnoreCase);

    public ArtifactDetector(
        MessageStore messageStore,
        IExecutionEventStore? eventStore = null,
        ILogger<ArtifactDetector>? logger = null)
    {
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Checks if a file path is a potential artifact.
    /// </summary>
    public static bool IsPotentialArtifact(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string fileName = Path.GetFileName(filePath).ToLowerInvariant();

        // Exclude temporary or internal files
        if (fileName.EndsWith(".tmp") || fileName.EndsWith(".temp") || fileName.StartsWith("~") ||
            fileName.EndsWith(".swp") || fileName.EndsWith(".lock"))
        {
            return false;
        }

        return ext switch
        {
            ".html" or ".htm" or ".md" or ".markdown" or ".svg" or
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or
            ".json" or ".xml" or ".xaml" or ".yaml" or ".yml" or ".toml" or ".csv" or ".sql" or
            ".cs" or ".ts" or ".js" or ".py" or ".rs" or ".go" or ".css" or ".txt" or ".log" or
            ".ps1" or ".bat" or ".sh" => true,
            _ => false
        };
    }

    /// <summary>
    /// Resolves the artifact category/type for rendering.
    /// </summary>
    public static string GetArtifactType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "html",
            ".md" or ".markdown" => "md",
            ".svg" => "svg",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => "image",
            _ => "text"
        };
    }

    /// <summary>
    /// Inspects and automatically registers an artifact in the store and emits the lifecycle event.
    /// </summary>
    public async Task<ArtifactDetectionResult> InspectAndRegisterAsync(
        string filePath,
        string sessionId,
        string? taskId,
        string? runId,
        string? actionId,
        string? content = null,
        string? diffText = null,
        CancellationToken ct = default)
    {
        if (!IsPotentialArtifact(filePath))
        {
            return new ArtifactDetectionResult(false, null, null, false, null);
        }

        try
        {
            string artifactType = GetArtifactType(filePath);
            string mimeType = ArtifactRecord.InferMimeType(filePath);
            bool isPreviewable = true;

            string contentHash = "(new)";
            if (content != null)
            {
                contentHash = HashText(content);
            }
            else if (File.Exists(filePath))
            {
                try
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
                    contentHash = HashBytes(fileBytes);
                }
                catch { /* best effort */ }
            }

            var record = new ArtifactRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                TaskId = taskId,
                ActionId = actionId,
                Path = filePath,
                MimeType = mimeType,
                DiffText = diffText,
                Previewable = isPreviewable,
                CreatedTimestamp = DateTime.UtcNow,
                UpdatedTimestamp = DateTime.UtcNow
            };

            // Save durable artifact row
            var artifactRow = new ArtifactRow(
                ArtifactId: record.Id,
                SessionId: sessionId,
                TaskId: taskId,
                Path: filePath,
                ArtifactType: artifactType,
                ContentHash: contentHash,
                CreatedAtUtc: record.CreatedTimestamp,
                UpdatedAtUtc: record.UpdatedTimestamp,
                Previewable: isPreviewable,
                IsCurrent: true);

            bool isExisting = _knownArtifacts.ContainsKey(filePath);
            if (!isExisting)
            {
                try
                {
                    var existing = await _messageStore.GetArtifactsBySessionAsync(sessionId, currentOnly: false);
                    if (existing.Any(a => string.Equals(a.Path, filePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        isExisting = true;
                    }
                }
                catch { /* best effort */ }
            }
            _knownArtifacts[filePath] = record;

            await _messageStore.MarkArtifactsStaleAsync(sessionId, filePath);
            await _messageStore.AddArtifactAsync(artifactRow);

            string eventType = isExisting ? "ArtifactUpdated" : "ArtifactCreated";

            try
            {
                await _messageStore.AddExecutionEventAsync(new ExecutionEventRow(
                    EventId: Guid.NewGuid().ToString("N"),
                    SessionId: sessionId,
                    TaskId: taskId,
                    RunId: runId,
                    EventType: eventType,
                    TimestampUtc: record.CreatedTimestamp,
                    ToolName: "artifact_detector",
                    Path: filePath,
                    PayloadJson: diffText));
            }
            catch { /* best effort */ }

            _logger?.LogDebug("Auto-registered artifact {Path} ({Type}) for session {SessionId}, task {TaskId}",
                filePath, artifactType, sessionId, taskId ?? "—");

            // Emit execution event
            _eventStore?.RecordEvent(new ExecutionEvent
            {
                SessionId = sessionId,
                TaskId = taskId,
                RunId = runId,
                ActionId = actionId,
                Category = isExisting ? ExecutionEventCategory.PreviewUpdated : ExecutionEventCategory.ArtifactCreated,
                Title = $"{(isExisting ? "Artifact updated" : "Artifact created")}: {Path.GetFileName(filePath)}",
                Summary = $"{(isExisting ? "Updated" : "Registered")} {artifactType} artifact",
                ArtifactId = record.Id,
                FilePath = filePath,
                Details = diffText
            });

            return new ArtifactDetectionResult(true, artifactType, mimeType, isPreviewable, record);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to register artifact for {Path}", filePath);
            return new ArtifactDetectionResult(false, null, null, false, null);
        }
    }

    private static string HashText(string text)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

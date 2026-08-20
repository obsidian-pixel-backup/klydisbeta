using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Klydis.Core.Tasks;

/// <summary>
/// A task-scoped artifact revision tracking content hashes, canonical path, and generating action.
/// </summary>
public sealed record TaskArtifact
{
    public required string ArtifactId { get; init; }
    public required string TaskId { get; init; }
    public required string RunId { get; init; }
    public string? TurnId { get; init; }
    public string? ActionId { get; init; }
    public required string CanonicalPath { get; init; }
    public int Revision { get; init; } = 1;
    public required string Sha256Hash { get; init; }
    public long ByteSize { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public bool IsCurrent { get; init; } = true;
}

/// <summary>
/// Thread-safe in-memory and durable catalog for task-scoped artifacts.
/// Prevents cross-task artifact leakage in multi-turn sessions.
/// </summary>
public sealed class TaskArtifactCatalog
{
    private readonly ConcurrentDictionary<string, List<TaskArtifact>> _artifactsByTask = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>
    /// Registers or updates an artifact for a specific task.
    /// Automatically increments revision and marks previous revisions of the same path as non-current.
    /// </summary>
    public TaskArtifact RegisterArtifact(
        string taskId,
        string runId,
        string filePath,
        string? turnId = null,
        string? actionId = null)
    {
        if (string.IsNullOrEmpty(taskId)) throw new ArgumentNullException(nameof(taskId));
        if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));

        string canonicalPath = Path.GetFullPath(filePath);
        string hash = ComputeFileHash(canonicalPath);
        long byteSize = File.Exists(canonicalPath) ? new FileInfo(canonicalPath).Length : 0;

        lock (_sync)
        {
            var list = _artifactsByTask.GetOrAdd(taskId, _ => new List<TaskArtifact>());

            // Mark previous entries for this exact path as not current
            int nextRev = 1;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (list[i].Revision >= nextRev)
                    {
                        nextRev = list[i].Revision + 1;
                    }
                    if (list[i].IsCurrent)
                    {
                        list[i] = list[i] with { IsCurrent = false };
                    }
                }
            }

            var artifact = new TaskArtifact
            {
                ArtifactId = Guid.NewGuid().ToString("N"),
                TaskId = taskId,
                RunId = runId,
                TurnId = turnId,
                ActionId = actionId,
                CanonicalPath = canonicalPath,
                Revision = nextRev,
                Sha256Hash = hash,
                ByteSize = byteSize,
                CreatedAtUtc = DateTime.UtcNow,
                IsCurrent = true
            };

            list.Add(artifact);
            return artifact;
        }
    }

    /// <summary>
    /// Retrieves all current artifacts belonging strictly to the specified task ID.
    /// </summary>
    public IReadOnlyList<TaskArtifact> GetCurrentArtifacts(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return Array.Empty<TaskArtifact>();

        lock (_sync)
        {
            if (_artifactsByTask.TryGetValue(taskId, out var list))
            {
                return list.Where(a => a.IsCurrent).ToList();
            }
            return Array.Empty<TaskArtifact>();
        }
    }

    private static string ComputeFileHash(string path)
    {
        if (!File.Exists(path)) return "missing";
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(path);
            byte[] bytes = sha256.ComputeHash(stream);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch
        {
            return "unreadable";
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Memory;

/// <summary>
/// Metadata for a saved session KV cache state snapshot.
/// </summary>
public record StateSnapshotRecord(
    string SessionId,
    string ModelPath,
    string FilePath,
    long FileSizeBytes,
    DateTime LastAccessed,
    int EvaluatedTokenCount
);

/// <summary>
/// Manages binary native KV cache state persistence on disk with LRU cache eviction.
/// </summary>
public class StatePersistenceManager
{
    private readonly string _stateDirectory;
    private readonly ILogger<StatePersistenceManager>? _logger;
    private readonly int _maxCachedSnapshots;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatePersistenceManager"/> class.
    /// </summary>
    /// <param name="maxCachedSnapshots">Maximum number of session state snapshots to retain on disk.</param>
    /// <param name="logger">Optional logger.</param>
    public StatePersistenceManager(int maxCachedSnapshots = 5, ILogger<StatePersistenceManager>? logger = null)
    {
        _maxCachedSnapshots = maxCachedSnapshots;
        _logger = logger;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _stateDirectory = Path.Combine(appData, ".klydis", "state");
        Directory.CreateDirectory(_stateDirectory);
    }

    /// <summary>
    /// Gets the target file path for a session's state snapshot.
    /// </summary>
    public string GetStateFilePath(string sessionId)
    {
        return Path.Combine(_stateDirectory, $"{sessionId}.kvstate");
    }

    /// <summary>
    /// Checks whether a state snapshot exists for the given session.
    /// </summary>
    public bool HasStateSnapshot(string sessionId)
    {
        string path = GetStateFilePath(sessionId);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>
    /// Registers a newly written snapshot and enforces LRU disk eviction.
    /// </summary>
    public void RegisterSnapshot(string sessionId, string modelPath, int evaluatedTokens)
    {
        string filePath = GetStateFilePath(sessionId);
        if (!File.Exists(filePath)) return;

        _logger?.LogInformation("Registered KV state snapshot for session {SessionId} ({FileSizeBytes} bytes).", sessionId, new FileInfo(filePath).Length);
        EnforceLruEviction();
    }

    /// <summary>
    /// Deletes a specific session's state snapshot if it exists.
    /// </summary>
    public bool DeleteSnapshot(string sessionId)
    {
        string filePath = GetStateFilePath(sessionId);
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
                _logger?.LogInformation("Deleted KV state snapshot for session {SessionId}.", sessionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete KV state snapshot for session {SessionId}.", sessionId);
            }
        }
        return false;
    }

    /// <summary>
    /// Enforces the LRU eviction policy on disk to prevent state snapshots from consuming excessive disk space.
    /// </summary>
    public void EnforceLruEviction()
    {
        try
        {
            var files = Directory.GetFiles(_stateDirectory, "*.kvstate")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();

            if (files.Count > _maxCachedSnapshots)
            {
                var filesToDelete = files.Skip(_maxCachedSnapshots);
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        file.Delete();
                        _logger?.LogInformation("Evicted LRU state snapshot file {FileName} from disk.", file.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to evict LRU snapshot file {FileName}.", file.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error enforcing LRU eviction for state snapshots.");
        }
    }
}

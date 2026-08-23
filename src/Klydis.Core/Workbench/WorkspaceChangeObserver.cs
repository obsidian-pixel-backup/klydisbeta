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
/// Secondary line of defense for workspace observation. Uses FileSystemWatcher to capture
/// file changes caused by external commands, scripts, or build processes (e.g. dotnet build,
/// git checkout, powershell output) that bypassed direct file-writing tools.
/// Automatically deduplicates against WorkspaceMutationService to prevent duplicate events.
/// </summary>
public sealed class WorkspaceChangeObserver : IDisposable
{
    private readonly WorkspaceMutationService _mutationService;
    private readonly MessageStore _messageStore;
    private readonly ArtifactDetector _artifactDetector;
    private readonly IExecutionEventStore? _eventStore;
    private readonly ILogger<WorkspaceChangeObserver>? _logger;

    private FileSystemWatcher? _watcher;
    private string? _currentWorkspaceRoot;
    private string? _activeSessionId;
    private string? _activeTaskId;
    private string? _activeRunId;

    // Debounce timer map
    private readonly ConcurrentDictionary<string, DateTime> _lastEventTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public WorkspaceChangeObserver(
        WorkspaceMutationService mutationService,
        MessageStore messageStore,
        ArtifactDetector artifactDetector,
        IExecutionEventStore? eventStore = null,
        ILogger<WorkspaceChangeObserver>? logger = null)
    {
        _mutationService = mutationService ?? throw new ArgumentNullException(nameof(mutationService));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _artifactDetector = artifactDetector ?? throw new ArgumentNullException(nameof(artifactDetector));
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Starts or points the watcher to the specified workspace directory and active execution context.
    /// </summary>
    public void StartObserving(string workspaceRoot, string sessionId, string? taskId = null, string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot)) return;

        lock (_lock)
        {
            _activeSessionId = sessionId;
            _activeTaskId = taskId;
            _activeRunId = runId;

            string canonical = Path.GetFullPath(workspaceRoot);
            if (string.Equals(_currentWorkspaceRoot, canonical, StringComparison.OrdinalIgnoreCase) && _watcher != null)
            {
                return;
            }

            StopObserving();

            _currentWorkspaceRoot = canonical;
            try
            {
                _watcher = new FileSystemWatcher(canonical)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true
                };

                _watcher.Created += OnFileSystemEvent;
                _watcher.Changed += OnFileSystemEvent;
                _watcher.Deleted += OnFileSystemEvent;
                _watcher.Renamed += OnRenamedEvent;

                _logger?.LogInformation("WorkspaceChangeObserver started watching {Path}", canonical);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to initialize FileSystemWatcher for {Path}", canonical);
            }
        }
    }

    /// <summary>
    /// Updates the active execution context for event attribution without restarting the watcher.
    /// </summary>
    public void UpdateContext(string sessionId, string? taskId = null, string? runId = null)
    {
        _activeSessionId = sessionId;
        _activeTaskId = taskId;
        _activeRunId = runId;
    }

    /// <summary>
    /// Stops the file system watcher.
    /// </summary>
    public void StopObserving()
    {
        lock (_lock)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileSystemEvent;
                _watcher.Changed -= OnFileSystemEvent;
                _watcher.Deleted -= OnFileSystemEvent;
                _watcher.Renamed -= OnRenamedEvent;
                _watcher.Dispose();
                _watcher = null;
            }
            _currentWorkspaceRoot = null;
        }
    }

    public void Dispose()
    {
        StopObserving();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        ProcessFileChangeAsync(e.FullPath, e.ChangeType.ToString()).ConfigureAwait(false);
    }

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        ProcessFileChangeAsync(e.FullPath, "Renamed").ConfigureAwait(false);
    }

    private async Task ProcessFileChangeAsync(string fullPath, string changeType)
    {
        // Debounce within 500ms
        string canonical = Path.GetFullPath(fullPath);
        var now = DateTime.UtcNow;
        if (_lastEventTimestamps.TryGetValue(canonical, out var last) && (now - last).TotalMilliseconds < 500)
        {
            return;
        }
        _lastEventTimestamps[canonical] = now;

        // Give the file a moment to be closed by writer
        await Task.Delay(100);

        if (!File.Exists(canonical) && changeType != "Deleted")
        {
            return;
        }

        string? content = null;
        string hash = "(deleted)";
        if (File.Exists(canonical))
        {
            try
            {
                content = await File.ReadAllTextAsync(canonical);
                hash = HashText(content);
            }
            catch
            {
                // File might be locked by another process
                return;
            }
        }

        // Deduplication: check if WorkspaceMutationService recently handled this exact file and hash
        if (_mutationService.IsRecentlyHandled(canonical, hash, TimeSpan.FromSeconds(3)))
        {
            _logger?.LogDebug("WorkspaceChangeObserver skipped {Path}: already handled by mutation service.", canonical);
            return;
        }

        string sessionId = _activeSessionId ?? "global";
        string? taskId = _activeTaskId;
        string? runId = _activeRunId;

        // Record FileChange
        var change = new FileChange(
            ChangeId: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            TaskId: taskId,
            Path: canonical,
            Tool: "filesystem_watcher",
            BeforeHash: "(untracked)",
            AfterHash: hash,
            Diff: content != null ? DiffService.Diff(null, content).Text : string.Empty,
            AddedLines: content != null ? DiffService.Diff(null, content).AddedLines : 0,
            DeletedLines: 0,
            TimestampUtc: DateTime.UtcNow);

        try
        {
            await _messageStore.AddFileChangeAsync(change);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to record watcher file change for {Path}", canonical);
        }

        // Emit execution event
        var category = changeType switch
        {
            "Created" => ExecutionEventCategory.FileCreated,
            "Deleted" => ExecutionEventCategory.FileDeleted,
            _ => ExecutionEventCategory.FileModified
        };

        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = sessionId,
            TaskId = taskId,
            RunId = runId,
            Category = category,
            ToolName = "filesystem_watcher",
            FilePath = canonical,
            Title = $"{changeType}: {Path.GetFileName(canonical)}",
            Summary = $"External modification detected by watcher ({changeType})",
            DiffText = change.Diff,
            AddedLines = change.AddedLines
        });

        // Inspect artifact
        if (content != null)
        {
            try
            {
                await _artifactDetector.InspectAndRegisterAsync(
                    canonical, sessionId, taskId, runId, null, content, change.Diff);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Artifact detector failed on watcher event for {Path}", canonical);
            }
        }
    }

    private static bool ShouldIgnore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string lower = path.ToLowerInvariant();

        return lower.Contains("\\.git\\") || lower.Contains("/.git/") ||
               lower.Contains("\\bin\\") || lower.Contains("/bin/") ||
               lower.Contains("\\obj\\") || lower.Contains("/obj/") ||
               lower.Contains("\\.vs\\") || lower.Contains("/.vs/") ||
               lower.Contains("\\.klydis\\") || lower.Contains("/.klydis/") ||
               lower.Contains("\\node_modules\\") || lower.Contains("/node_modules/") ||
               lower.EndsWith(".tmp") || lower.EndsWith(".temp") || lower.EndsWith(".lock");
    }

    private static string HashText(string text)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

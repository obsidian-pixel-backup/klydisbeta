using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Klydis.Core.Tasks;

/// <summary>
/// Manages workspace version increments on file mutations to drive cache/evidence invalidation (Phase 10).
/// </summary>
public interface IWorkspaceVersionManager
{
    /// <summary>Gets the current workspace version for a task.</summary>
    int GetVersion(string taskId);

    /// <summary>Increments the workspace version for a task following a file mutation.</summary>
    int IncrementVersion(string taskId, string path, string mutationKind);

    /// <summary>Raised when a task's workspace version increments.</summary>
    event Action<string, int, string>? VersionChanged;
}

/// <summary>
/// Concrete implementation of <see cref="IWorkspaceVersionManager"/>.
/// </summary>
public sealed class WorkspaceVersionManager : IWorkspaceVersionManager
{
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly Memory.MessageStore? _store;

    public event Action<string, int, string>? VersionChanged;

    public WorkspaceVersionManager(Memory.MessageStore? store = null)
    {
        _store = store;
    }

    /// <inheritdoc />
    public int GetVersion(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return 0;
        return _versions.GetOrAdd(taskId, 0);
    }

    /// <inheritdoc />
    public int IncrementVersion(string taskId, string path, string mutationKind)
    {
        if (string.IsNullOrEmpty(taskId)) return 0;
        int next = _versions.AddOrUpdate(taskId, 1, (_, current) => current + 1);
        VersionChanged?.Invoke(taskId, next, path);
        return next;
    }
}

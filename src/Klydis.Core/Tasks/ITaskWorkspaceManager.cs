using System;
using System.Collections.Concurrent;
using System.IO;

namespace Klydis.Core.Tasks;

/// <summary>
/// Manages per-task isolated workspace directories and boundary enforcement (Phase 13).
/// Prevents path traversal and ensures tools only operate within designated task workspace roots.
/// </summary>
public interface ITaskWorkspaceManager
{
    /// <summary>Gets the workspace root directory for the specified task.</summary>
    string GetWorkspaceRoot(string taskId);

    /// <summary>Sets an explicit workspace root directory for a task.</summary>
    void SetWorkspaceRoot(string taskId, string rootPath);

    /// <summary>Validates that a path is strictly contained within the task workspace root.</summary>
    bool IsPathWithinWorkspace(string taskId, string path, out string resolvedPath);

    /// <summary>Canonicalizes a relative or rooted path against the task workspace root.</summary>
    string CanonicalizePath(string taskId, string path);
}

/// <summary>
/// Concrete implementation of <see cref="ITaskWorkspaceManager"/>.
/// </summary>
public sealed class TaskWorkspaceManager : ITaskWorkspaceManager
{
    private readonly ConcurrentDictionary<string, string> _taskRoots = new(StringComparer.Ordinal);
    private readonly string _defaultWorkspaceRoot;

    public TaskWorkspaceManager(string? defaultWorkspaceRoot = null)
    {
        _defaultWorkspaceRoot = !string.IsNullOrWhiteSpace(defaultWorkspaceRoot)
            ? Path.GetFullPath(defaultWorkspaceRoot)
            : Directory.GetCurrentDirectory();
    }

    /// <inheritdoc />
    public string GetWorkspaceRoot(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return _defaultWorkspaceRoot;
        return _taskRoots.GetOrAdd(taskId, _defaultWorkspaceRoot);
    }

    /// <inheritdoc />
    public void SetWorkspaceRoot(string taskId, string rootPath)
    {
        if (string.IsNullOrEmpty(taskId) || string.IsNullOrWhiteSpace(rootPath)) return;
        _taskRoots[taskId] = Path.GetFullPath(rootPath);
    }

    /// <inheritdoc />
    public bool IsPathWithinWorkspace(string taskId, string path, out string resolvedPath)
    {
        string root = GetWorkspaceRoot(taskId);
        return WorkspaceBoundaryValidator.IsWithinWorkspace(path, root, out resolvedPath, out _);
    }

    /// <inheritdoc />
    public string CanonicalizePath(string taskId, string path)
    {
        string root = GetWorkspaceRoot(taskId);
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }
        return Path.GetFullPath(Path.Combine(root, path));
    }
}

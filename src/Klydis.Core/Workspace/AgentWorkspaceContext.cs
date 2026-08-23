using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Klydis.Core.Workspace;

/// <summary>
/// First-class runtime context defining the active workspace boundaries for a session or task.
/// </summary>
public sealed record AgentWorkspaceContext(
    string SessionId,
    string Root,
    string Scratch,
    string Artifacts,
    string Changes,
    string Exports,
    string Terminal,
    IReadOnlyList<string> AuthorizedExternalRoots,
    WorkspaceMode Mode = WorkspaceMode.Scratch)
{
    /// <summary>
    /// Returns the default working directory for model command execution and relative file operations.
    /// In Scratch mode, defaults to %WORKSPACE_ROOT%\scratch.
    /// In Project mode, defaults to %WORKSPACE_ROOT%.
    /// </summary>
    public string DefaultWorkingDirectory => Mode == WorkspaceMode.Scratch ? Scratch : Root;

    /// <summary>
    /// Checks if a normalized, canonical path is strictly contained within the workspace root or any authorized external roots.
    /// </summary>
    public bool ContainsPath(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath)) return false;
        if (IsSubPathOf(canonicalPath, Root)) return true;
        if (AuthorizedExternalRoots != null)
        {
            foreach (var ext in AuthorizedExternalRoots)
            {
                if (IsSubPathOf(canonicalPath, ext)) return true;
            }
        }
        return false;
    }

    private static bool IsSubPathOf(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent)) return false;
        try
        {
            string normParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normPath, normParent, StringComparison.OrdinalIgnoreCase) ||
                   normPath.StartsWith(normParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   normPath.StartsWith(normParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

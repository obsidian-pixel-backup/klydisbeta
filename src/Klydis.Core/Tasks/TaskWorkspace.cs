using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// The task's file workspace (review §12): the root every file-tool path must stay inside,
/// plus any explicitly-allowed additional roots. Established when a task is created (or by
/// the app when a project directory is known) and propagated through the action-validations
/// context so the <see cref="WorkspaceBoundaryValidator"/> is enforced for EVERY filesystem
/// action — read_file, write_file, edit_file, delete_file, list_directory, search_files —
/// not just the ones a caller remembers to check.
/// </summary>
public sealed record TaskWorkspace(
    string Root,
    IReadOnlyList<string>? AllowedRoots = null)
{
    /// <summary>True when the workspace is actually established (non-empty root).</summary>
    public bool IsEstablished => !string.IsNullOrWhiteSpace(Root);

    /// <summary>True when the resolved path is inside the root or one of the allowed roots.</summary>
    public bool Contains(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string resolved;
        try
        {
            resolved = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }
        if (IsWithin(resolved, Root)) return true;
        return AllowedRoots != null && AllowedRoots.Any(r => IsWithin(resolved, r));
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string normalized = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalized + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Deterministic workspace-boundary validation (review: file-tool paths must stay inside the
/// task workspace). Rejects absolute escapes (C:\Windows\System32\foo when the workspace is
/// C:\Projects\X) and relative traversal (../../secret.txt) BEFORE execution. Run_command is
/// deliberately exempt — the shell is the escape hatch for legitimate system work; the file
/// surface is not.
///
/// The gate accepts an optional workspace root: null = boundary enforcement off (permissive,
/// today's default — the desktop agent has no task workspace concept yet). When a task
/// workspace root exists (the TaskStep workspace milestone), callers supply it and every
/// file-tool path is contained. This validator is the deterministic rule; the caller decides
/// when to enable it.
/// </summary>
public static class WorkspaceBoundaryValidator
{
    /// <summary>Tools whose path arguments are subject to containment.</summary>
    private static readonly HashSet<string> PathTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "view_file", "write_file", "edit_file", "str_replace",
        "list_directory", "list_dir", "search_files", "index_folder_rag"
    };

    /// <summary>True when the tool's path arguments should be contained.</summary>
    public static bool IsPathTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && PathTools.Contains(toolName);

    /// <summary>
    /// Validates the tool's path argument against the workspace root. Returns null when the
    /// call is within bounds (or the tool/path is not path-scoped); returns a concrete reason
    /// when the resolved path escapes the root. Never throws.
    /// </summary>
    public static string? Validate(string? toolName, IDictionary<string, object>? args, string workspaceRoot)
    {
        if (!IsPathTool(toolName) || string.IsNullOrWhiteSpace(workspaceRoot)) return null;
        if (args == null) return null; // missing required path is the schema check's job

        string? path = FindPathArg(args);
        if (string.IsNullOrWhiteSpace(path)) return null;

        string resolved;
        try
        {
            resolved = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(workspaceRoot, path));
        }
        catch (Exception)
        {
            // Unresolvable paths are the executor's problem (it produces the real error);
            // containment only judges resolvable paths.
            return null;
        }

        string root = Path.GetFullPath(workspaceRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool contained = string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase) ||
                         resolved.StartsWith(
                             root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                         resolved.StartsWith(
                             root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (contained) return null;

        return $"path '{path}' resolves to '{resolved}', which is OUTSIDE the task workspace " +
               $"root '{root}'. File-tool paths must stay inside the workspace; use run_command " +
               "for anything outside it.";
    }

    private static string? FindPathArg(IDictionary<string, object> args)
    {
        foreach (var kvp in args)
        {
            if (string.Equals(kvp.Key, "path", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "folder_path", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "directory", StringComparison.OrdinalIgnoreCase))
            {
                return ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString();
            }
        }
        return null;
    }
}

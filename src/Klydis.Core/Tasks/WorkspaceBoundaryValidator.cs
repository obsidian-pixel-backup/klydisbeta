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
        "read_file", "view_file", "write_file", "edit_file", "replace_lines",
        "apply_patch", "structural_replace", "str_replace",
        "list_directory", "list_dir", "search_files", "index_folder_rag"
    };

    /// <summary>Tools whose working directory argument is subject to containment.</summary>
    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_command", "execute_command", "run_shell", "terminal_command", "manage_process"
    };

    private static readonly HashSet<string> ReservedDosDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>True when the tool's path arguments should be contained.</summary>
    public static bool IsPathTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && PathTools.Contains(toolName);

    /// <summary>True when the tool is a shell tool with a working directory.</summary>
    public static bool IsShellTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && ShellTools.Contains(toolName);

    /// <summary>
    /// Validates the tool's path argument against the workspace root. Returns null when the
    /// call is within bounds (or the tool/path is not path-scoped); returns a concrete reason
    /// when the resolved path escapes the root. Never throws.
    /// </summary>
    public static string? Validate(string? toolName, IDictionary<string, object>? args, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;
        if (args == null) return null; // missing required path is the schema check's job

        if (IsPathTool(toolName))
        {
            string? path = FindPathArg(args);
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (!IsWithinWorkspace(path, workspaceRoot, out string resolved, out string root))
            {
                return $"path '{path}' resolves to '{resolved}', which is OUTSIDE the task workspace " +
                       $"root '{root}'. File-tool paths must stay inside the workspace.";
            }
        }
        else if (IsShellTool(toolName))
        {
            string? workingDir = FindWorkingDirArg(args);
            if (!string.IsNullOrWhiteSpace(workingDir))
            {
                if (!IsWithinWorkspace(workingDir, workspaceRoot, out string resolved, out string root))
                {
                    return $"working directory '{workingDir}' resolves to '{resolved}', which is OUTSIDE the task workspace " +
                           $"root '{root}'. Shell commands must execute within the workspace.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether a given path is strictly within the workspace root.
    /// </summary>
    public static bool IsWithinWorkspace(string requestedPath, string workspaceRoot, out string resolvedPath, out string canonicalRoot)
    {
        resolvedPath = string.Empty;
        canonicalRoot = string.Empty;

        if (string.IsNullOrWhiteSpace(requestedPath) || string.IsNullOrWhiteSpace(workspaceRoot))
            return false;

        try
        {
            // Reject dangerous path schemes: UNC paths, device namespaces, NT namespaces, alternate data streams
            if (requestedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                requestedPath.StartsWith(@"//", StringComparison.Ordinal) ||
                requestedPath.StartsWith(@"\??\", StringComparison.Ordinal) ||
                requestedPath.StartsWith(@"/??/", StringComparison.Ordinal) ||
                requestedPath.Contains("::") ||
                (requestedPath.Length > 2 && requestedPath.IndexOf(':', 2) >= 0))
            {
                resolvedPath = requestedPath;
                canonicalRoot = workspaceRoot;
                return false;
            }

            // Check for DOS reserved device names
            string fileName = Path.GetFileNameWithoutExtension(requestedPath);
            if (ReservedDosDeviceNames.Contains(fileName))
            {
                resolvedPath = requestedPath;
                canonicalRoot = workspaceRoot;
                return false;
            }

            string fullRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.IsPathRooted(requestedPath)
                ? Path.GetFullPath(requestedPath)
                : Path.GetFullPath(Path.Combine(fullRoot, requestedPath));

            resolvedPath = fullPath;
            canonicalRoot = fullRoot;

            bool contained = string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                             fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                             fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            return contained;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindPathArg(IDictionary<string, object> args)
    {
        foreach (var kvp in args)
        {
            if (string.Equals(kvp.Key, "path", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "folder_path", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "directory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "target_file", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "file_path", StringComparison.OrdinalIgnoreCase))
            {
                return ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString();
            }
        }
        return null;
    }

    private static string? FindWorkingDirArg(IDictionary<string, object> args)
    {
        foreach (var kvp in args)
        {
            if (string.Equals(kvp.Key, "working_directory", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "working_dir", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "cwd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "work_dir", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "directory", StringComparison.OrdinalIgnoreCase))
            {
                return ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString();
            }
        }
        return null;
    }
}

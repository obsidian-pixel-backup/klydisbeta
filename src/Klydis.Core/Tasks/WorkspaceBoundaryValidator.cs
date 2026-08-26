using System;
using System.Collections.Generic;
using System.IO;
using Klydis.Core.Chat;
using Klydis.Core.Workspace;

namespace Klydis.Core.Tasks;

/// <summary>
/// Deterministic workspace-boundary validation (review: file-tool paths must stay inside the
/// task workspace). Rejects absolute escapes (C:\Windows\System32\foo when the workspace is
/// C:\Projects\X) and relative traversal (../../secret.txt) BEFORE execution. Run_command is
/// deliberately exempt — the shell is the escape hatch for legitimate system work; the file
/// surface is not.
///
/// The gate accepts an optional workspace root or <see cref="AgentWorkspaceContext"/>: null = boundary enforcement off (permissive).
/// When a task workspace exists, callers supply it and every file-tool path is contained.
/// </summary>
public static class WorkspaceBoundaryValidator
{
    /// <summary>Tools that mutate the filesystem — strictly contained within the workspace.</summary>
    private static readonly HashSet<string> MutationTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "replace_lines", "str_replace", "replace_text",
        "apply_patch", "structural_replace", "delete_file"
    };

    /// <summary>Tools that perform read-only inspection, search, or listing — allowed system-wide.</summary>
    private static readonly HashSet<string> ReadOnlyInspectionTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "view_file", "list_directory", "list_dir",
        "search_files", "search_dir", "find_files", "file_exists", "index_folder_rag"
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

    /// <summary>True when the tool mutates files and must be contained inside the workspace.</summary>
    public static bool IsMutationTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && MutationTools.Contains(toolName);

    /// <summary>True when the tool performs read-only inspection/search and is allowed across the system.</summary>
    public static bool IsReadOnlyInspectionTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && ReadOnlyInspectionTools.Contains(toolName);

    /// <summary>True when the tool's path arguments are handled by filesystem validation.</summary>
    public static bool IsPathTool(string? toolName)
        => IsMutationTool(toolName) || IsReadOnlyInspectionTool(toolName);

    /// <summary>True when the tool is a shell tool with a working directory.</summary>
    public static bool IsShellTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && ShellTools.Contains(toolName);

    /// <summary>
    /// Validates the tool's path argument against the workspace context or workspace root.
    /// Read-only inspection and search tools are permitted system-wide; mutation tools are contained to the workspace.
    /// Returns null when within bounds; returns a concrete reason when out of bounds or targeting restricted paths.
    /// </summary>
    public static string? Validate(string? toolName, IDictionary<string, object>? args, AgentWorkspaceContext? workspaceContext)
    {
        if (workspaceContext == null) return null;
        if (args == null) return null;

        if (IsMutationTool(toolName))
        {
            string? path = FindPathArg(args);
            if (string.IsNullOrWhiteSpace(path)) return null;

            var resolution = FilesystemPolicy.ResolveAndValidate(path, workspaceContext, isMutation: true);
            if (!resolution.IsAllowed)
            {
                return resolution.FailureReason ?? $"Path '{path}' is outside the active workspace '{workspaceContext.Root}'. Modifying or creating files is only permitted within the workspace.";
            }
        }
        else if (IsReadOnlyInspectionTool(toolName))
        {
            string? path = FindPathArg(args);
            if (string.IsNullOrWhiteSpace(path)) return null;

            var resolution = FilesystemPolicy.ResolveAndValidate(path, workspaceContext, isMutation: false);
            if (!resolution.IsAllowed && (resolution.Status == PathAuthorizationStatus.RestrictedApplicationPath || resolution.Status == PathAuthorizationStatus.TraversalAttackDetected))
            {
                return resolution.FailureReason ?? $"Path '{path}' is not permitted under the filesystem policy.";
            }
        }
        else if (IsShellTool(toolName))
        {
            string? workingDir = FindWorkingDirArg(args);
            if (!string.IsNullOrWhiteSpace(workingDir))
            {
                var resolution = FilesystemPolicy.ResolveAndValidate(workingDir, workspaceContext, isMutation: false);
                if (!resolution.IsAllowed && (resolution.Status == PathAuthorizationStatus.RestrictedApplicationPath || resolution.Status == PathAuthorizationStatus.TraversalAttackDetected))
                {
                    return resolution.FailureReason ?? $"Working directory '{workingDir}' is not permitted under the workspace policy.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates the tool's path argument against the workspace root. Returns null when the
    /// call is within bounds (or the tool/path is not path-scoped); returns a concrete reason
    /// when a path escapes the root or targets restricted system directories. Never throws.
    /// </summary>
    public static string? Validate(string? toolName, IDictionary<string, object>? args, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;
        if (args == null) return null; // missing required path is the schema check's job

        var wsContext = new AgentWorkspaceContext(
            SessionId: "temp",
            Root: workspaceRoot,
            Scratch: Path.Combine(workspaceRoot, "scratch"),
            Artifacts: Path.Combine(workspaceRoot, "artifacts"),
            Changes: Path.Combine(workspaceRoot, "changes"),
            Exports: Path.Combine(workspaceRoot, "exports"),
            Terminal: Path.Combine(workspaceRoot, "terminal"),
            AuthorizedExternalRoots: new List<string>());

        return Validate(toolName, args, wsContext);
    }

    /// <summary>
    /// Normalizes near-miss workspace prefixes (e.g. DEVELOPER Project(s) vs DEVELOPER PROJECTS).
    /// </summary>
    public static string NormalizePathForWorkspace(string requestedPath, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) || string.IsNullOrWhiteSpace(workspaceRoot))
            return requestedPath;

        string cleanRoot = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootName = Path.GetFileName(cleanRoot);
        if (string.IsNullOrEmpty(rootName)) return requestedPath;

        if (requestedPath.Contains("session-default", StringComparison.OrdinalIgnoreCase))
        {
            int idx = requestedPath.IndexOf("session-default", StringComparison.OrdinalIgnoreCase);
            string rel = requestedPath.Substring(idx + "session-default".Length).TrimStart('/', '\\');
            return Path.Combine(cleanRoot, rel);
        }

        if (requestedPath.Contains("DEVELOPER Project", StringComparison.OrdinalIgnoreCase))
        {
            int idx = requestedPath.IndexOf(rootName, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string rel = requestedPath.Substring(idx + rootName.Length).TrimStart('/', '\\');
                return Path.Combine(cleanRoot, rel);
            }
        }
        return requestedPath;
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

        requestedPath = NormalizePathForWorkspace(requestedPath, workspaceRoot);

        try
        {
            // Reject dangerous path schemes: UNC paths, device namespaces, NT namespaces, alternate data streams
            if (requestedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                requestedPath.StartsWith(@"//", StringComparison.Ordinal) ||
                requestedPath.StartsWith(@"\??\", StringComparison.Ordinal) ||
                requestedPath.StartsWith(@"/??/", StringComparison.Ordinal) ||
                requestedPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
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

    public static string? FindPathArg(IDictionary<string, object> args)
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

    public static string? FindWorkingDirArg(IDictionary<string, object> args)
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

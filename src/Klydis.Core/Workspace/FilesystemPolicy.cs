using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Klydis.Core.Workspace;

/// <summary>
/// Enforces the 3-tier deterministic filesystem access policy:
/// 1. Workspace (%USERPROFILE%\Klydis\Workspace\...) - Automatically permitted.
/// 2. User-approved external directories - Permitted only after explicit authorization.
/// 3. Restricted application & system directories (%USERPROFILE%\.klydis, Program Files, Windows, app bin) - Denied by default.
/// Also provides path normalization, directory traversal protection, and symlink/reparse point validation.
/// </summary>
public static class FilesystemPolicy
{
    private static readonly HashSet<string> ReservedDosDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Returns canonical list of restricted application and OS directories that the model cannot access directly.
    /// </summary>
    public static IReadOnlyList<string> GetRestrictedDirectories()
    {
        var restricted = new List<string>();

        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                restricted.Add(Path.Combine(userProfile, ".klydis"));
            }
        }
        catch { }

        try
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir)) restricted.Add(winDir);
        }
        catch { }

        try
        {
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(progFiles)) restricted.Add(progFiles);
        }
        catch { }

        try
        {
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(progFilesX86)) restricted.Add(progFilesX86);
        }
        catch { }

        try
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(sysDir)) restricted.Add(sysDir);
        }
        catch { }

        try
        {
            string appBase = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(appBase)) restricted.Add(appBase);
        }
        catch { }

        return restricted.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();
    }

    /// <summary>
    /// Resolves and validates a requested path against the workspace context and policy.
    /// Read-only inspection and search operations are permitted across the host system.
    /// File creations and edits (mutations) are strictly contained within the active workspace.
    /// </summary>
    public static WorkspacePathResolution ResolveAndValidate(
        string requestedPath,
        AgentWorkspaceContext workspaceContext,
        bool isMutation = false)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return new WorkspacePathResolution(
                requestedPath ?? string.Empty,
                string.Empty,
                PathAuthorizationStatus.Allowed,
                null);
        }

        // 1. Check for dangerous path patterns (UNC, device namespace, alternate data streams, DOS device names)
        if (IsDangerousSchemeOrDevice(requestedPath, out string dangerReason))
        {
            return new WorkspacePathResolution(
                requestedPath,
                requestedPath,
                PathAuthorizationStatus.TraversalAttackDetected,
                dangerReason);
        }

        // 2. Resolve relative path against workspace context subdirectories
        string candidateFullPath;
        try
        {
            if (Path.IsPathRooted(requestedPath))
            {
                candidateFullPath = Path.GetFullPath(requestedPath);
            }
            else
            {
                candidateFullPath = ResolveRelativePath(requestedPath, workspaceContext);
            }
        }
        catch (Exception ex)
        {
            return new WorkspacePathResolution(
                requestedPath,
                requestedPath,
                PathAuthorizationStatus.TraversalAttackDetected,
                $"Path resolution failed: {ex.Message}");
        }

        // 3. Symlink / Reparse point check (prevent escaping via symlinks or junctions into restricted paths)
        string targetCanonicalPath = ResolveReparsePoints(candidateFullPath);

        // Level 3 check: Restricted Application & System Directories
        var restricted = GetRestrictedDirectories();
        foreach (var restr in restricted)
        {
            if (IsSubPathOf(targetCanonicalPath, restr))
            {
                return new WorkspacePathResolution(
                    requestedPath,
                    targetCanonicalPath,
                    PathAuthorizationStatus.RestrictedApplicationPath,
                    $"Path '{targetCanonicalPath}' resolves to '{targetCanonicalPath}', which is OUTSIDE the task workspace and targets restricted application or system internal directory '{restr}'. Accessing application or system internals is prohibited.");
            }
        }

        // Check if within active workspace or authorized external roots
        if (workspaceContext.ContainsPath(targetCanonicalPath))
        {
            return new WorkspacePathResolution(
                requestedPath,
                targetCanonicalPath,
                PathAuthorizationStatus.Allowed);
        }

        // Relative path traversal check (e.g. "..\..\secret.txt")
        if (!Path.IsPathRooted(requestedPath) && !workspaceContext.ContainsPath(targetCanonicalPath))
        {
            return new WorkspacePathResolution(
                requestedPath,
                targetCanonicalPath,
                PathAuthorizationStatus.TraversalAttackDetected,
                $"Path '{requestedPath}' resolves to '{targetCanonicalPath}', which is OUTSIDE the task workspace '{workspaceContext.Root}'. Traversal escaping the workspace is prohibited.");
        }

        if (isMutation)
        {
            return new WorkspacePathResolution(
                requestedPath,
                targetCanonicalPath,
                PathAuthorizationStatus.OutsideWorkspace,
                $"Path '{targetCanonicalPath}' resolves to '{targetCanonicalPath}', which is outside the active workspace (OUTSIDE the task workspace root '{workspaceContext.Root}'). Modifying or creating files is only permitted within the workspace.");
        }

        // Read-only external path check: allowed system-wide
        return new WorkspacePathResolution(
            requestedPath,
            targetCanonicalPath,
            PathAuthorizationStatus.Allowed);
    }

    /// <summary>
    /// Resolves a relative path to the appropriate workspace subdirectory based on standard subfolder prefixes.
    /// </summary>
    public static string ResolveRelativePath(string relativePath, AgentWorkspaceContext context)
    {
        string norm = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

        if (norm.StartsWith("scratch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string sub = norm.Substring("scratch".Length + 1);
            return Path.GetFullPath(Path.Combine(context.Scratch, sub));
        }
        if (norm.StartsWith("artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string sub = norm.Substring("artifacts".Length + 1);
            return Path.GetFullPath(Path.Combine(context.Artifacts, sub));
        }
        if (norm.StartsWith("changes" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string sub = norm.Substring("changes".Length + 1);
            return Path.GetFullPath(Path.Combine(context.Changes, sub));
        }
        if (norm.StartsWith("exports" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string sub = norm.Substring("exports".Length + 1);
            return Path.GetFullPath(Path.Combine(context.Exports, sub));
        }
        if (norm.StartsWith("terminal" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string sub = norm.Substring("terminal".Length + 1);
            return Path.GetFullPath(Path.Combine(context.Terminal, sub));
        }

        // Default: relative paths resolve to the default working directory (scratch in Scratch mode, root in Project mode)
        return Path.GetFullPath(Path.Combine(context.DefaultWorkingDirectory, norm));
    }

    /// <summary>
    /// Checks for dangerous path schemes, namespaces, or device names.
    /// </summary>
    public static bool IsDangerousSchemeOrDevice(string path, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Path is empty.";
            return true;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"//", StringComparison.Ordinal))
        {
            reason = "UNC network share paths are not permitted.";
            return true;
        }

        if (path.StartsWith(@"\??\", StringComparison.Ordinal) ||
            path.StartsWith(@"/??/", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            reason = "NT/Device namespaces are not permitted.";
            return true;
        }

        // Alternate Data Streams or malformed colon
        if (path.Contains("::") || (path.Length > 2 && path.IndexOf(':', 2) >= 0))
        {
            reason = "NTFS alternate data streams (ADS) or invalid colon characters are not permitted.";
            return true;
        }

        // DOS Reserved Names (CON, PRN, AUX, NUL, COM1..9, LPT1..9)
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (ReservedDosDeviceNames.Contains(fileName))
        {
            reason = $"DOS reserved device name '{fileName}' is not permitted.";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Recursively resolves symlinks, junctions, or reparse points to verify canonical targets.
    /// </summary>
    public static string ResolveReparsePoints(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null) return Path.GetFullPath(target.FullName);
                }
            }
            else if (Directory.Exists(fullPath))
            {
                var di = new DirectoryInfo(fullPath);
                if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = di.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null) return Path.GetFullPath(target.FullName);
                }
            }
            return fullPath;
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static bool IsSubPathOf(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(path)) return false;
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

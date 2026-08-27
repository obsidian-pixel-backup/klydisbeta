using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Klydis.Core.Workspace;

/// <summary>
/// Thread-safe implementation of <see cref="IWorkspaceManager"/>.
/// Manages session workspace lifecycle, directory structure creation, and access policies.
/// </summary>
public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly string _baseWorkspaceDirectory;
    private readonly ConcurrentDictionary<string, AgentWorkspaceContext> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _authorizedExternalRoots = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the base directory where all session workspaces are created (%USERPROFILE%\Klydis\Workspace by default).
    /// </summary>
    public string BaseWorkspaceDirectory => _baseWorkspaceDirectory;

    public WorkspaceManager(string? baseWorkspaceDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(baseWorkspaceDirectory))
        {
            _baseWorkspaceDirectory = Path.GetFullPath(baseWorkspaceDirectory);
        }
        else
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _baseWorkspaceDirectory = Path.Combine(userProfile, "Klydis", "Workspace");
        }

        try
        {
            Directory.CreateDirectory(_baseWorkspaceDirectory);
            string defaultSessionDir = Path.Combine(_baseWorkspaceDirectory, "session-default");
            Directory.CreateDirectory(defaultSessionDir);
            Directory.CreateDirectory(Path.Combine(defaultSessionDir, "scratch"));
            Directory.CreateDirectory(Path.Combine(defaultSessionDir, "artifacts"));
            Directory.CreateDirectory(Path.Combine(defaultSessionDir, "changes"));
            Directory.CreateDirectory(Path.Combine(defaultSessionDir, "exports"));
            Directory.CreateDirectory(Path.Combine(defaultSessionDir, "terminal"));
        }
        catch { }
    }

    /// <inheritdoc />
    public AgentWorkspaceContext CreateSessionWorkspace(string sessionId, string? projectRoot = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = "default";
        }

        var authorized = GetAuthorizedRoots(sessionId);

        AgentWorkspaceContext context;
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            string fullProj = Path.GetFullPath(projectRoot);
            Directory.CreateDirectory(fullProj);

            // In project mode, create dedicated helper folders in session base or within project
            string sessionFolderName = SanitizeSessionFolderName(sessionId);
            string sessionSupportDir = Path.Combine(_baseWorkspaceDirectory, sessionFolderName);
            string scratchDir = Path.Combine(sessionSupportDir, "scratch");
            string artifactsDir = Path.Combine(sessionSupportDir, "artifacts");
            string changesDir = Path.Combine(sessionSupportDir, "changes");
            string exportsDir = Path.Combine(sessionSupportDir, "exports");
            string terminalDir = Path.Combine(sessionSupportDir, "terminal");

            Directory.CreateDirectory(scratchDir);
            Directory.CreateDirectory(artifactsDir);
            Directory.CreateDirectory(changesDir);
            Directory.CreateDirectory(exportsDir);
            Directory.CreateDirectory(terminalDir);

            var extList = new List<string>(authorized);
            if (!extList.Contains(fullProj, StringComparer.OrdinalIgnoreCase))
            {
                extList.Add(fullProj);
            }

            context = new AgentWorkspaceContext(
                SessionId: sessionId,
                Root: fullProj,
                Scratch: scratchDir,
                Artifacts: artifactsDir,
                Changes: changesDir,
                Exports: exportsDir,
                Terminal: terminalDir,
                AuthorizedExternalRoots: extList,
                Mode: WorkspaceMode.Project);
        }
        else
        {
            string sessionFolderName = SanitizeSessionFolderName(sessionId);
            string sessionDir = Path.Combine(_baseWorkspaceDirectory, sessionFolderName);
            string scratchDir = Path.Combine(sessionDir, "scratch");
            string artifactsDir = Path.Combine(sessionDir, "artifacts");
            string changesDir = Path.Combine(sessionDir, "changes");
            string exportsDir = Path.Combine(sessionDir, "exports");
            string terminalDir = Path.Combine(sessionDir, "terminal");

            Directory.CreateDirectory(sessionDir);
            Directory.CreateDirectory(scratchDir);
            Directory.CreateDirectory(artifactsDir);
            Directory.CreateDirectory(changesDir);
            Directory.CreateDirectory(exportsDir);
            Directory.CreateDirectory(terminalDir);

            context = new AgentWorkspaceContext(
                SessionId: sessionId,
                Root: sessionDir,
                Scratch: scratchDir,
                Artifacts: artifactsDir,
                Changes: changesDir,
                Exports: exportsDir,
                Terminal: terminalDir,
                AuthorizedExternalRoots: authorized.ToList(),
                Mode: WorkspaceMode.Scratch);
        }

        _sessions[sessionId] = context;
        return context;
    }

    /// <inheritdoc />
    public AgentWorkspaceContext GetWorkspaceContext(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = "default";
        }

        return _sessions.GetOrAdd(sessionId, id => CreateSessionWorkspace(id));
    }

    /// <inheritdoc />
    public string GetWorkspaceRoot(string sessionId)
        => GetWorkspaceContext(sessionId).Root;

    /// <inheritdoc />
    public string GetScratchDirectory(string sessionId)
        => GetWorkspaceContext(sessionId).Scratch;

    /// <inheritdoc />
    public string GetArtifactsDirectory(string sessionId)
        => GetWorkspaceContext(sessionId).Artifacts;

    /// <inheritdoc />
    public string GetChangesDirectory(string sessionId)
        => GetWorkspaceContext(sessionId).Changes;

    /// <inheritdoc />
    public string GetExportsDirectory(string sessionId)
        => GetWorkspaceContext(sessionId).Exports;

    /// <inheritdoc />
    public string GetTerminalDirectory(string sessionId)
        => GetWorkspaceContext(sessionId).Terminal;

    /// <inheritdoc />
    public bool IsPathAllowed(string sessionId, string path, bool isMutation = false)
    {
        var context = GetWorkspaceContext(sessionId);
        var resolution = FilesystemPolicy.ResolveAndValidate(path, context, isMutation);
        return resolution.IsAllowed;
    }

    /// <inheritdoc />
    public WorkspacePathResolution ResolvePath(string sessionId, string requestedPath, bool isMutation = false)
    {
        var context = GetWorkspaceContext(sessionId);
        return FilesystemPolicy.ResolveAndValidate(requestedPath, context, isMutation);
    }

    /// <inheritdoc />
    public void AuthorizeExternalPath(string sessionId, string externalPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(externalPath)) return;
        string canonical = Path.GetFullPath(externalPath);
        var roots = _authorizedExternalRoots.GetOrAdd(sessionId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (roots)
        {
            roots.Add(canonical);
        }

        // Refresh context if exists
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            var updatedRoots = new List<string>(existing.AuthorizedExternalRoots);
            if (!updatedRoots.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                updatedRoots.Add(canonical);
                _sessions[sessionId] = existing with { AuthorizedExternalRoots = updatedRoots };
            }
        }
    }

    /// <inheritdoc />
    public AgentWorkspaceContext GetOrCreateSessionWorkspace(string sessionId, string? projectRoot = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = "default";
        return _sessions.GetOrAdd(sessionId, sid => CreateSessionWorkspace(sid, projectRoot));
    }

    /// <inheritdoc />
    public AgentWorkspaceContext SetProjectWorkspace(string sessionId, string projectRoot)
    {
        return CreateSessionWorkspace(sessionId, projectRoot);
    }

    /// <inheritdoc />
    public async Task<string> SaveAttachmentArtifactAsync(string sessionId, string fileName, string? sourceFilePath = null, string? content = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = "default";
        var context = GetWorkspaceContext(sessionId);
        string safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"attachment_{Guid.NewGuid():N}.txt";

        string artifactsDir = context.Artifacts;
        Directory.CreateDirectory(artifactsDir);
        string targetArtifactPath = Path.Combine(artifactsDir, safeName);
        string targetRootPath = Path.Combine(context.Root, safeName);

        if (!string.IsNullOrWhiteSpace(sourceFilePath) && File.Exists(sourceFilePath))
        {
            try
            {
                File.Copy(sourceFilePath, targetArtifactPath, overwrite: true);
                if (!string.Equals(targetArtifactPath, targetRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceFilePath, targetRootPath, overwrite: true);
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(content))
                {
                    await File.WriteAllTextAsync(targetArtifactPath, content).ConfigureAwait(false);
                    if (!string.Equals(targetArtifactPath, targetRootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await File.WriteAllTextAsync(targetRootPath, content).ConfigureAwait(false);
                    }
                }
            }
        }
        else if (content != null)
        {
            await File.WriteAllTextAsync(targetArtifactPath, content).ConfigureAwait(false);
            if (!string.Equals(targetArtifactPath, targetRootPath, StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(targetRootPath, content).ConfigureAwait(false);
            }
        }

        AuthorizeExternalPath(sessionId, targetArtifactPath);
        AuthorizeExternalPath(sessionId, targetRootPath);
        return targetArtifactPath;
    }

    /// <inheritdoc />
    public void DeleteSessionWorkspace(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        string sessionFolderName = SanitizeSessionFolderName(sessionId);
        string sessionDir = Path.Combine(_baseWorkspaceDirectory, sessionFolderName);
        _sessions.TryRemove(sessionId, out _);
        _authorizedExternalRoots.TryRemove(sessionId, out _);

        try
        {
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
            }
        }
        catch { }
    }

    private HashSet<string> GetAuthorizedRoots(string sessionId)
    {
        return _authorizedExternalRoots.GetOrAdd(sessionId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string SanitizeSessionFolderName(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return "session-default";
        string clean = sessionId.Trim();
        if (clean.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
        {
            return clean;
        }
        return $"session-{clean}";
    }
}

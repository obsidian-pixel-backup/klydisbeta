using System;
using System.Collections.Generic;

namespace Klydis.Core.Workspace;

/// <summary>
/// Manages per-session workspaces, directory isolation, external authorizations, and filesystem boundary enforcement.
/// </summary>
public interface IWorkspaceManager
{
    /// <summary>
    /// Creates or retrieves the isolated workspace for a session.
    /// Creates all standard subdirectories (scratch, artifacts, changes, exports, terminal) on disk.
    /// </summary>
    AgentWorkspaceContext CreateSessionWorkspace(string sessionId, string? projectRoot = null);

    /// <summary>
    /// Gets or creates the session workspace context.
    /// </summary>
    AgentWorkspaceContext GetOrCreateSessionWorkspace(string sessionId, string? projectRoot = null);

    /// <summary>
    /// Gets the active workspace context for a session.
    /// </summary>
    AgentWorkspaceContext GetWorkspaceContext(string sessionId);

    /// <summary>
    /// Gets the root directory for a session workspace.
    /// </summary>
    string GetWorkspaceRoot(string sessionId);

    /// <summary>
    /// Gets the scratch directory for a session workspace (the default working directory).
    /// </summary>
    string GetScratchDirectory(string sessionId);

    /// <summary>
    /// Gets the artifacts directory for a session workspace.
    /// </summary>
    string GetArtifactsDirectory(string sessionId);

    /// <summary>
    /// Gets the changes directory for a session workspace.
    /// </summary>
    string GetChangesDirectory(string sessionId);

    /// <summary>
    /// Gets the exports directory for a session workspace.
    /// </summary>
    string GetExportsDirectory(string sessionId);

    /// <summary>
    /// Gets the terminal directory for a session workspace.
    /// </summary>
    string GetTerminalDirectory(string sessionId);

    /// <summary>
    /// Checks if a given path is permitted for access under the session's workspace policy.
    /// </summary>
    bool IsPathAllowed(string sessionId, string path, bool isMutation = false);

    /// <summary>
    /// Resolves and validates a requested relative or absolute path against the session workspace policy.
    /// </summary>
    WorkspacePathResolution ResolvePath(string sessionId, string requestedPath, bool isMutation = false);

    /// <summary>
    /// Authorizes an external directory path for a session.
    /// </summary>
    void AuthorizeExternalPath(string sessionId, string externalPath);

    /// <summary>
    /// Sets an explicit project directory for a session (Project mode).
    /// </summary>
    AgentWorkspaceContext SetProjectWorkspace(string sessionId, string projectRoot);

    /// <summary>
    /// Saves or copies an uploaded attachment into the session artifacts directory and workspace root.
    /// </summary>
    Task<string> SaveAttachmentArtifactAsync(string sessionId, string fileName, string? sourceFilePath = null, string? content = null);

    /// <summary>
    /// Deletes the session's workspace folder and all associated artifacts, scratch files, and logs from disk.
    /// </summary>
    void DeleteSessionWorkspace(string sessionId);
}

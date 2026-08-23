namespace Klydis.Core.Workspace;

/// <summary>
/// Defines the operational mode of the workspace.
/// </summary>
public enum WorkspaceMode
{
    /// <summary>
    /// Isolated temporary scratch workspace created per session under %USERPROFILE%\Klydis\Workspace\session-{id}\.
    /// </summary>
    Scratch,

    /// <summary>
    /// Explicit user-approved external project workspace directory.
    /// </summary>
    Project
}

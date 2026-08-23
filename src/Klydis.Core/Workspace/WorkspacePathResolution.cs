namespace Klydis.Core.Workspace;

/// <summary>
/// The authorization status of a resolved filesystem path.
/// </summary>
public enum PathAuthorizationStatus
{
    /// <summary>Path is strictly within the active workspace or authorized roots and permitted for operations.</summary>
    Allowed,

    /// <summary>Path targets an external directory that is not yet authorized by the user.</summary>
    RequiresAuthorization,

    /// <summary>Path targets restricted application internals (%USERPROFILE%\.klydis, Program Files, Windows, app directory) and is blocked.</summary>
    RestrictedApplicationPath,

    /// <summary>Path is outside the workspace root and violates containment.</summary>
    OutsideWorkspace,

    /// <summary>Path attempted traversal or invalid scheme (UNC, DOS devices, alternate data streams).</summary>
    TraversalAttackDetected
}

/// <summary>
/// Result of resolving and validating a requested path against the workspace policy.
/// </summary>
public sealed record WorkspacePathResolution(
    string RequestedPath,
    string ResolvedPath,
    PathAuthorizationStatus Status,
    string? FailureReason = null)
{
    /// <summary>True when the path is fully authorized for read/write execution.</summary>
    public bool IsAllowed => Status == PathAuthorizationStatus.Allowed;
}

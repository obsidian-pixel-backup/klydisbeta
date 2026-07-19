using System;

namespace Klydis.Core.Models;

/// <summary>
/// Represents an active or paused download that should be resumed.
/// </summary>
public record ActiveDownloadRecord(
    string RepoId,
    string FileName,
    string DestinationPath,
    DateTime StartedAt
);

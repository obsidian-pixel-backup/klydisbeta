using System;

namespace Klydis.Core.Workbench;

/// <summary>
/// Durable record of a file mutation with before/after hashes and unified diff text.
/// Every workspace mutation produces a FileDiff for the execution journal and right panel.
/// </summary>
public sealed record FileDiff
{
    public string Id { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string BeforeHash { get; init; } = string.Empty;
    public string AfterHash { get; init; } = string.Empty;
    public string UnifiedDiff { get; init; } = string.Empty;
    public int AddedLines { get; init; }
    public int DeletedLines { get; init; }
    public string? TaskId { get; init; }
    public string? RunId { get; init; }
    public string? TodoId { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

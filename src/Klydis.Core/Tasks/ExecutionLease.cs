using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// Execution lease protecting running operations across workers and surviving crashes.
/// Prevents concurrent execution of the same work item or tool call after crash recovery.
/// </summary>
public sealed record ExecutionLease
{
    public required string ExecutionId { get; init; }
    public required string GoalId { get; init; }
    public required string TurnId { get; init; }
    public required string WorkerId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset HeartbeatAt { get; set; } = DateTimeOffset.UtcNow;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public DateTimeOffset ExpiresAt => HeartbeatAt + LeaseDuration;

    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

    public void Renew()
    {
        HeartbeatAt = DateTimeOffset.UtcNow;
    }
}

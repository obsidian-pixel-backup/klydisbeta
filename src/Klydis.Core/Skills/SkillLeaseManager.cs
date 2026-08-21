using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Skills;

/// <summary>
/// Lifecycle state of a skill activation lease.
/// </summary>
public enum SkillLeaseState
{
    Inactive = 0,
    Candidate,
    Active,
    Dormant,
    Released
}

/// <summary>
/// Scoped activation lease binding a skill and its capabilities to a specific goal/task horizon.
/// </summary>
public sealed record SkillLease(
    string LeaseId,
    string SkillId,
    string? GoalId,
    string? TaskId,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ExposedTools,
    DateTime ActivatedAt,
    int ExpiresAfterIterations)
{
    public SkillLeaseState State { get; set; } = SkillLeaseState.Active;
    public int RemainingIterations { get; set; } = ExpiresAfterIterations;
    public int TotalInvocations { get; set; } = 0;
}

/// <summary>
/// Manages skill activation lifecycles, transactional lease acquisition, and scoped context retention.
/// </summary>
public sealed class SkillLeaseManager
{
    private readonly ConcurrentDictionary<string, SkillLease> _leases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Acquires or renews an activation lease for a skill.
    /// </summary>
    public SkillLease AcquireLease(
        string skillId,
        string? goalId,
        string? taskId,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> exposedTools,
        int durationIterations = 5)
    {
        ArgumentNullException.ThrowIfNull(skillId);

        string key = $"{goalId ?? "global"}_{skillId}";

        var lease = _leases.AddOrUpdate(
            key,
            _ => new SkillLease(
                LeaseId: Guid.NewGuid().ToString("N"),
                SkillId: skillId,
                GoalId: goalId,
                TaskId: taskId,
                Capabilities: capabilities,
                ExposedTools: exposedTools,
                ActivatedAt: DateTime.UtcNow,
                ExpiresAfterIterations: durationIterations),
            (_, existing) =>
            {
                existing.State = SkillLeaseState.Active;
                existing.RemainingIterations = durationIterations;
                return existing;
            });

        return lease;
    }

    /// <summary>
    /// Gets all currently active leases for a given goal horizon.
    /// </summary>
    public IReadOnlyList<SkillLease> GetActiveLeases(string? goalId = null)
    {
        return _leases.Values
            .Where(l => (goalId == null || l.GoalId == goalId) && l.State == SkillLeaseState.Active)
            .ToList();
    }

    /// <summary>
    /// Advances execution iteration clock and transitions unused leases to dormant or released state.
    /// </summary>
    public void TickTurn(string? goalId = null)
    {
        foreach (var lease in _leases.Values)
        {
            if (goalId != null && lease.GoalId != goalId) continue;

            if (lease.State == SkillLeaseState.Active)
            {
                lease.RemainingIterations--;
                if (lease.RemainingIterations <= 0)
                {
                    lease.State = SkillLeaseState.Dormant;
                }
            }
        }
    }

    /// <summary>
    /// Releases all leases for a goal.
    /// </summary>
    public void ReleaseAll(string? goalId = null)
    {
        foreach (var kvp in _leases)
        {
            if (goalId == null || kvp.Value.GoalId == goalId)
            {
                kvp.Value.State = SkillLeaseState.Released;
            }
        }
    }
}

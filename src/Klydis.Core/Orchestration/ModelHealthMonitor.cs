using System;
using System.Collections.Concurrent;

namespace Klydis.Core.Orchestration;

/// <summary>
/// Health state of a model's live execution within the current session.
/// </summary>
public enum ModelHealthState
{
    Healthy = 0,
    Degraded = 1,
    Critical = 2
}

/// <summary>
/// Recovery action recommended by the health monitor when stagnation is detected.
/// </summary>
public enum HealthRecoveryAction
{
    None = 0,
    ReduceToolSurface = 1,
    SwitchToGenericJson = 2,
    ForceSingleAction = 3,
    DecomposeObjective = 4,
    CapabilityIntentMode = 5,
    FallbackToWorkerModel = 6
}

/// <summary>
/// Model Health Monitor (P1).
/// Tracks online failure rates, stagnation, and representation errors during long-horizon runs.
/// Engages a 6-level automatic recovery cascade to prevent infinite error loops.
/// </summary>
public sealed class ModelHealthMonitor
{
    private sealed class SessionHealth
    {
        public int TotalAttempts;
        public int Successes;
        public int Failures;
        public int ConsecutiveFailures;
        public int UnknownToolCount;
        public int SchemaErrorCount;
        public int RecoveryLevel;
    }

    private readonly ConcurrentDictionary<string, SessionHealth> _sessions = new(StringComparer.Ordinal);

    public void RecordOutcome(string sessionId, bool success, bool isUnknownTool = false, bool isSchemaError = false)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        var health = _sessions.GetOrAdd(sessionId, _ => new SessionHealth());
        lock (health)
        {
            health.TotalAttempts++;
            if (success)
            {
                health.Successes++;
                health.ConsecutiveFailures = 0;
            }
            else
            {
                health.Failures++;
                health.ConsecutiveFailures++;
                if (isUnknownTool) health.UnknownToolCount++;
                if (isSchemaError) health.SchemaErrorCount++;
            }
        }
    }

    public ModelHealthState GetHealthState(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var health))
        {
            return ModelHealthState.Healthy;
        }

        lock (health)
        {
            if (health.ConsecutiveFailures >= 3 && health.Successes == 0)
            {
                return ModelHealthState.Critical;
            }

            if (health.ConsecutiveFailures >= 2 || health.UnknownToolCount >= 2 || health.SchemaErrorCount >= 3)
            {
                return ModelHealthState.Degraded;
            }

            return ModelHealthState.Healthy;
        }
    }

    public HealthRecoveryAction EvaluateRecoveryAction(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var health))
        {
            return HealthRecoveryAction.None;
        }

        lock (health)
        {
            if (health.ConsecutiveFailures < 2 && health.UnknownToolCount < 1)
            {
                return HealthRecoveryAction.None;
            }

            health.RecoveryLevel++;
            return health.RecoveryLevel switch
            {
                1 => HealthRecoveryAction.ReduceToolSurface,
                2 => HealthRecoveryAction.SwitchToGenericJson,
                3 => HealthRecoveryAction.ForceSingleAction,
                4 => HealthRecoveryAction.DecomposeObjective,
                5 => HealthRecoveryAction.CapabilityIntentMode,
                _ => HealthRecoveryAction.FallbackToWorkerModel
            };
        }
    }

    public void Reset(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }
}

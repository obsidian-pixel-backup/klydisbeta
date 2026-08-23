using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// The 7-level runtime recovery and supervisor escalation ladder (P1).
/// Guarantees that failures escalate deterministically through runtime intervention
/// rather than letting the model loop indefinitely.
/// </summary>
public enum SupervisorEscalationLevel
{
    /// <summary>Level 0: Normal autonomous model execution.</summary>
    Level0_NormalExecution = 0,

    /// <summary>Level 1: Deterministic syntax/schema repair injection.</summary>
    Level1_DeterministicRepair = 1,

    /// <summary>Level 2: Runtime deterministic tool selection and direct dispatch.</summary>
    Level2_DeterministicToolSelection = 2,

    /// <summary>Level 3: Step-level replanning and obligation reconstitution.</summary>
    Level3_ReplanStep = 3,

    /// <summary>Level 4: Execution policy throttling (tightest tool projection, 1-action limit).</summary>
    Level4_PolicyThrottling = 4,

    /// <summary>Level 5: Model failover to a stronger model profile with state snapshot.</summary>
    Level5_ModelFailover = 5,

    /// <summary>Level 6: User intervention required (task paused).</summary>
    Level6_UserIntervention = 6
}

/// <summary>
/// Evaluates and manages runtime supervisor escalation levels across a task run.
/// </summary>
public sealed class SupervisorEscalationLadder
{
    public SupervisorEscalationLevel CurrentLevel { get; private set; } = SupervisorEscalationLevel.Level0_NormalExecution;

    public int ConsecutiveFailures { get; private set; } = 0;
    public int ConsecutiveRefusals { get; private set; } = 0;

    /// <summary>
    /// Evaluates current execution signals and transitions to the appropriate escalation level.
    /// </summary>
    public SupervisorEscalationLevel Evaluate(
        ProgressEngine progressEngine,
        ProgressBudget budget,
        ResponseContractVerdict? contractVerdict,
        int failedActionCount)
    {
        // 1. Check for capability refusal
        if (contractVerdict?.Classification == ResponseContractClassification.CapabilityRefusal)
        {
            ConsecutiveRefusals++;
            if (ConsecutiveRefusals == 1)
            {
                CurrentLevel = SupervisorEscalationLevel.Level1_DeterministicRepair;
            }
            else if (ConsecutiveRefusals >= 2)
            {
                CurrentLevel = SupervisorEscalationLevel.Level2_DeterministicToolSelection;
            }
            return CurrentLevel;
        }
        else
        {
            ConsecutiveRefusals = 0;
        }

        // 2. Check for tool failure accumulation
        if (failedActionCount > 0)
        {
            ConsecutiveFailures += failedActionCount;
            if (ConsecutiveFailures == 1)
            {
                CurrentLevel = SupervisorEscalationLevel.Level1_DeterministicRepair;
            }
            else if (ConsecutiveFailures == 2)
            {
                CurrentLevel = SupervisorEscalationLevel.Level2_DeterministicToolSelection;
            }
            else if (ConsecutiveFailures == 3)
            {
                CurrentLevel = SupervisorEscalationLevel.Level3_ReplanStep;
            }
            else if (ConsecutiveFailures == 4)
            {
                CurrentLevel = SupervisorEscalationLevel.Level4_PolicyThrottling;
            }
            else if (ConsecutiveFailures == 5)
            {
                CurrentLevel = SupervisorEscalationLevel.Level5_ModelFailover;
            }
            else if (ConsecutiveFailures >= 6)
            {
                CurrentLevel = SupervisorEscalationLevel.Level6_UserIntervention;
            }
            return CurrentLevel;
        }

        // 3. Check for progress stagnation
        if (progressEngine.IsStagnant(threshold: 3))
        {
            if (CurrentLevel < SupervisorEscalationLevel.Level3_ReplanStep)
            {
                CurrentLevel = SupervisorEscalationLevel.Level3_ReplanStep;
            }
            else if (CurrentLevel == SupervisorEscalationLevel.Level3_ReplanStep && progressEngine.ConsecutiveStagnantCycles >= 5)
            {
                CurrentLevel = SupervisorEscalationLevel.Level4_PolicyThrottling;
            }
            return CurrentLevel;
        }

        // 4. If progress is positive, reset escalation towards normal
        if (progressEngine.History.Count > 0 && progressEngine.History[^1] > 0)
        {
            ConsecutiveFailures = 0;
            CurrentLevel = SupervisorEscalationLevel.Level0_NormalExecution;
        }

        return CurrentLevel;
    }

    /// <summary>Resets the escalation ladder to normal execution.</summary>
    public void Reset()
    {
        CurrentLevel = SupervisorEscalationLevel.Level0_NormalExecution;
        ConsecutiveFailures = 0;
        ConsecutiveRefusals = 0;
    }
}

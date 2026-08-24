using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// Determines whether a model-generated execution plan is required, optional, or unnecessary
/// for the current request. The model should not be forced to plan for trivial requests.
/// </summary>
public enum PlanningRequirement
{
    /// <summary>No plan needed — direct tool execution.</summary>
    None,
    /// <summary>Plan creation is optional — model decides.</summary>
    Optional,
    /// <summary>Plan creation is required — multi-step autonomous work.</summary>
    Required
}

/// <summary>
/// Evaluates whether an incoming request warrants a model-generated execution plan.
/// This replaces the legacy behavior where TaskDecomposer would derive plans from prose.
/// The decision now belongs to the model and runtime policy, not a regex parser.
/// </summary>
public static class PlanningPolicy
{
    /// <summary>
    /// Evaluates the planning requirement for a request based on interaction mode,
    /// message complexity, and explicit user intent.
    /// </summary>
    public static PlanningRequirement Evaluate(
        InteractionMode mode,
        string? userMessage,
        bool userExplicitlyRequestedPlan = false,
        int estimatedToolCalls = 0,
        int estimatedFileMutations = 0)
    {
        // Explicit user request always requires a plan
        if (userExplicitlyRequestedPlan)
            return PlanningRequirement.Required;

        // Conversation mode never needs a plan
        if (mode == InteractionMode.Conversation)
            return PlanningRequirement.None;

        // Autonomous mode always requires a plan for accountability
        if (mode == InteractionMode.Autonomous)
            return PlanningRequirement.Required;

        // Task mode: plan is optional, model decides based on complexity
        if (mode == InteractionMode.Task)
        {
            // High estimated complexity suggests planning
            if (estimatedToolCalls >= 5 || estimatedFileMutations >= 2)
                return PlanningRequirement.Required;

            return PlanningRequirement.Optional;
        }

        return PlanningRequirement.Optional;
    }
}

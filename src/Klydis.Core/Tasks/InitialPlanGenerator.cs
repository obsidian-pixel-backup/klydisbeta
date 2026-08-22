using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Initializes baseline execution plan schema representations for actionable requests.
/// If the user request contains explicit numbered/bulleted tasks, TaskDecomposer extracts them directly.
/// Otherwise, the plan starts with zero tasks for the model/planner to generate based on the objective.
/// </summary>
public static class InitialPlanGenerator
{
    /// <summary>
    /// Returns the initial task checklist for a new objective.
    /// If explicit decomposed tasks exist in the user message, returns them.
    /// Otherwise returns an empty list for model-driven planning.
    /// </summary>
    public static IReadOnlyList<string> Generate(string userMessage)
    {
        var decomposed = TaskDecomposer.Decompose(userMessage);
        if (decomposed.Count >= 2)
        {
            return decomposed;
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Creates a fresh <see cref="ExecutionPlan"/> schema initialized for the objective.
    /// Seeds tasks if explicit decomposed items exist.
    /// </summary>
    public static ExecutionPlan CreateInitialPlan(string objective)
    {
        var decomposed = TaskDecomposer.Decompose(objective);
        if (decomposed.Count >= 2)
        {
            var tasks = decomposed.Select((desc, idx) => new PlanTask(
                id: (idx + 1).ToString(),
                description: desc,
                status: TaskStepStatus.Pending
            )).ToList();

            return new ExecutionPlan(
                objective: objective ?? string.Empty,
                tasks: tasks,
                completion: new CompletionCriteria(decomposed));
        }

        return new ExecutionPlan(
            objective: objective ?? string.Empty,
            tasks: Array.Empty<PlanTask>(),
            completion: new CompletionCriteria(Array.Empty<string>()));
    }
}

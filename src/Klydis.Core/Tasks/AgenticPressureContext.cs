using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Maintains and formats the "Agentic Pressure Layer" that injects structured runtime truth
/// every turn so the model never forgets the objective, pending work, recent observations, or completion requirements.
/// </summary>
public static class AgenticPressureContext
{
    public static string Format(
        string goalObjective,
        GoalEntity? goal,
        IReadOnlyList<ToolExecutor.PlanEntry>? planEntries,
        IReadOnlyList<string>? recentObservations,
        string? lastAction,
        bool isVerifiedComplete,
        BudgetSnapshot? budget)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AGENT EXECUTION STATE ===");
        sb.AppendLine($"Goal Objective: {goalObjective}");

        int totalItems = 0;
        int completedItems = 0;
        var completedList = new List<string>();
        var pendingList = new List<string>();
        var blockedList = new List<string>();

        if (goal != null && goal.WorkItems.Count > 0)
        {
            var counts = goal.GetWorkItemCounts();
            totalItems = counts.Total;
            completedItems = counts.Completed;

            foreach (var w in goal.WorkItems)
            {
                if (w.State == WorkItemState.Completed)
                    completedList.Add($"[{w.Id}] {w.Objective}");
                else if (w.State == WorkItemState.Blocked)
                    blockedList.Add($"[{w.Id}] {w.Objective}");
                else
                    pendingList.Add($"[{w.Id}] {w.Objective}");
            }
        }
        else if (planEntries != null && planEntries.Count > 0)
        {
            totalItems = planEntries.Count;
            completedItems = planEntries.Count(p => p.Done);

            for (int i = 0; i < planEntries.Count; i++)
            {
                var entry = planEntries[i];
                if (entry.Done)
                    completedList.Add($"#{i + 1} {entry.Text}");
                else
                    pendingList.Add($"#{i + 1} {entry.Text}");
            }
        }

        if (totalItems > 0)
        {
            int pct = (int)Math.Round((double)completedItems * 100 / totalItems);
            sb.AppendLine($"Numeric Progress: {pct}% ({completedItems}/{totalItems} items completed)");
            if (completedList.Count > 0)
            {
                sb.AppendLine($"Completed Work ({completedList.Count}): {string.Join("; ", completedList)}");
            }
            if (pendingList.Count > 0)
            {
                sb.AppendLine($"Pending Work ({pendingList.Count}): {string.Join("; ", pendingList)}");
            }
            if (blockedList.Count > 0)
            {
                sb.AppendLine($"Blocked Work ({blockedList.Count}): {string.Join("; ", blockedList)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(lastAction))
        {
            sb.AppendLine($"Last Action Executed: {lastAction}");
        }

        if (recentObservations != null && recentObservations.Count > 0)
        {
            sb.AppendLine("Recent Observations:");
            foreach (var obs in recentObservations.TakeLast(3))
            {
                sb.AppendLine($"  - {obs}");
            }
        }

        if (budget != null)
        {
            sb.AppendLine($"Budget Status: {budget.HealthStatus} (turns: {budget.TurnsCount}, tools: {budget.ToolCallsCount})");
            if (!string.IsNullOrWhiteSpace(budget.GuidanceMessage))
            {
                sb.AppendLine($"  {budget.GuidanceMessage}");
            }
        }

        sb.AppendLine("Completion Criteria Status: " + (isVerifiedComplete ? "SATISFIED" : "NOT SATISFIED"));
        if (!isVerifiedComplete)
        {
            sb.AppendLine("REQUIRED: Choose the next action from available tools. Do NOT declare final answer or completion until all pending work is verified.");
        }
        sb.AppendLine("=============================");

        return sb.ToString();
    }
}

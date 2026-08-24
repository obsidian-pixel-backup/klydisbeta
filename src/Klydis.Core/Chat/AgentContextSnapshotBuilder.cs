using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Tasks;

namespace Klydis.Core.Chat;

/// <summary>
/// Builds the compact <see cref="AgentContextSnapshot"/> — the ONLY state projection the
/// model receives by default (full state stays in the persistent store and is retrieved on
/// demand). This is the deterministic bridge between the runtime's authoritative state
/// (plan checklist, evidence ledger, workspace) and the model's context window: a small,
/// stable block instead of re-serializing the entire execution history on every generation.
///
/// Mirrors the model-owned planning architecture: the plan/todo state is produced by the
/// model through structured tools; this builder only PROJECTS it compactly for the next
/// generation. It never invents tasks from prose.
/// </summary>
public static class AgentContextSnapshotBuilder
{
    /// <summary>
    /// Builds a snapshot from live runtime state. All inputs are optional — a snapshot is
    /// still produced (and formatted) with whatever is available, so callers can feed it
    /// partial state without special-casing.
    /// </summary>
    public static AgentContextSnapshot Build(
        string? objective,
        IReadOnlyList<ToolExecutor.PlanEntry>? planEntries,
        string? workspaceRoot,
        IReadOnlyList<string>? artifactPaths,
        IReadOnlyList<EvidenceLedgerEntry>? evidence,
        string? currentTaskId = null,
        string? currentStep = null)
    {
        var plan = planEntries ?? Array.Empty<ToolExecutor.PlanEntry>();
        int total = plan.Count;
        int completed = plan.Count(e => e.Done);
        var firstOpen = plan.FirstOrDefault(e => !e.Done);
        var nextTasks = plan.Where(e => !e.Done).Skip(1).Take(3).Select(e => e.Text).ToList();

        PlanSummary? planSummary = total > 0
            ? new PlanSummary(
                Objective: objective ?? string.Empty,
                TotalTasks: total,
                CompletedTasks: completed,
                CurrentTaskId: firstOpen?.Text,
                CurrentTaskTitle: firstOpen?.Text,
                NextTaskIds: nextTasks)
            : null;

        CurrentTaskSummary? currentTaskSummary = !string.IsNullOrWhiteSpace(currentStep)
            ? new CurrentTaskSummary(
                Id: currentTaskId ?? string.Empty,
                Title: currentStep,
                Status: firstOpen is null ? "complete" : "running",
                RecentToolCalls: Array.Empty<string>())
            : null;

        var evidenceList = (evidence ?? Array.Empty<EvidenceLedgerEntry>())
            .Select(e => new RecentEvidence(
                Kind: e.Evidence.Kind.ToString(),
                Subject: e.Evidence.Subject ?? e.Evidence.Description,
                Passed: e.Evidence.Kind != EvidenceKind.CommandFailed,
                Detail: e.Evidence.Description))
            .Take(8)
            .ToList();

        WorkspaceSummary? workspaceSummary = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? new WorkspaceSummary(
                Root: workspaceRoot,
                Scratch: null,
                Artifacts: null,
                ModifiedFileCount: artifactPaths?.Count ?? 0)
            : null;

        return new AgentContextSnapshot
        {
            Objective = objective ?? string.Empty,
            Plan = planSummary,
            Todos = null,
            Workspace = workspaceSummary,
            CurrentTask = currentTaskSummary,
            RecentEvidence = evidenceList,
            OpenIssues = Array.Empty<string>()
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Klydis.Core.Memory;

/// <summary>
/// Detailed token budget and segment breakdown for a compiled model prompt.
/// </summary>
public sealed record PromptBudgetReport
{
    public int TotalTokens { get; init; }
    public int SystemCoreTokens { get; init; }
    public int ModelProfileTokens { get; init; }
    public int CurrentStepTokens { get; init; }
    public int ActionContractTokens { get; init; }
    public int ToolIndexTokens { get; init; }
    public int ToolDetailsTokens { get; init; }
    public int PlanTokens { get; init; }
    public int WorldStateTokens { get; init; }
    public int HistoryTokens { get; init; }
    public int ToolResultTokens { get; init; }
    public int RagTokens { get; init; }
    public int AttachmentTokens { get; init; }
    public int StaticPrefixTokens { get; init; }
    public int DynamicSuffixTokens { get; init; }

    public bool ExceededSoftBudget { get; init; }
    public bool ExceededHardBudget { get; init; }

    /// <summary>Useful dynamic tokens divided by total prompt tokens.</summary>
    public double ContextEfficiency =>
        TotalTokens > 0 ? (double)(CurrentStepTokens + ToolDetailsTokens + ToolResultTokens + HistoryTokens) / TotalTokens : 0.0;

    /// <summary>Individual segments included in the compilation.</summary>
    public IReadOnlyList<PromptSegmentSummary> Segments { get; init; } = Array.Empty<PromptSegmentSummary>();

    /// <summary>
    /// Renders an exact prompt budget report matching blueprint format.
    /// </summary>
    public string FormattedReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROMPT BUDGET");
        sb.AppendLine("─────────────────────────────");
        sb.AppendLine($"System core        {SystemCoreTokens,6:N0}");
        sb.AppendLine($"Model profile      {ModelProfileTokens,6:N0}");
        sb.AppendLine($"Current step       {CurrentStepTokens,6:N0}");
        sb.AppendLine($"Action contract    {ActionContractTokens,6:N0}");
        sb.AppendLine($"Tool index         {ToolIndexTokens,6:N0}");
        sb.AppendLine($"Tool details       {ToolDetailsTokens,6:N0}");
        sb.AppendLine($"Plan               {PlanTokens,6:N0}");
        sb.AppendLine($"World state        {WorldStateTokens,6:N0}");
        sb.AppendLine($"History            {HistoryTokens,6:N0}");
        sb.AppendLine($"Tool results       {ToolResultTokens,6:N0}");
        sb.AppendLine($"RAG                {RagTokens,6:N0}");
        sb.AppendLine($"Attachments        {AttachmentTokens,6:N0}");
        sb.AppendLine("─────────────────────────────");
        sb.AppendLine($"TOTAL              {TotalTokens,6:N0}");
        sb.AppendLine($"Efficiency:        {ContextEfficiency * 100,5:F1}%");
        return sb.ToString();
    }
}

public sealed record PromptSegmentSummary(
    PromptSegmentKind Kind,
    int TokenCost,
    int Priority,
    string? Reason);

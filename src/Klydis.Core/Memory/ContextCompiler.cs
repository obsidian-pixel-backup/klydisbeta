using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;
using Klydis.Core.Protocol;
using Klydis.Core.Tasks;

namespace Klydis.Core.Memory;

/// <summary>
/// Compiled result produced by <see cref="ContextCompiler"/>.
/// </summary>
public sealed record CompiledContextResult(
    string SystemPrompt,
    string UserPrompt,
    string FullCombinedPrompt,
    PromptBudgetReport BudgetReport,
    IReadOnlyList<ChatMessage> SlicedMessages);

/// <summary>
/// The SINGLE component permitted to construct model context and prompts.
/// Everything else submits structured <see cref="PromptSegment"/> instances to it.
/// Enforces budget limits, current-step slicing, tool-index compaction,
/// think-block stripping, deduplication, and on-demand RAG/skills injection.
/// </summary>
public class ContextCompiler
{
    private readonly AgentModelProfile _profile;

    public ContextCompiler(AgentModelProfile? profile = null)
    {
        _profile = profile ?? AgentModelProfile.Default;
    }

    /// <summary>
    /// Compiles all segments into a tightly budgeted, deduplicated prompt.
    /// </summary>
    public CompiledContextResult Compile(
        IEnumerable<PromptSegment> segments,
        IReadOnlyList<ChatMessage>? history = null,
        int? overrideBudget = null)
    {
        int targetBudget = overrideBudget ?? _profile.RecommendedContextBudget;
        int hardLimit = _profile.HardContextBudget;

        var segmentList = segments.ToList();
        var summaries = new List<PromptSegmentSummary>();

        // 1. Deduplicate segments by kind and reason/content hash
        var uniqueSegments = new List<PromptSegment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seg in segmentList.OrderByDescending(s => s.Priority))
        {
            string key = $"{seg.Kind}:{seg.Content.Trim()}";
            if (seen.Add(key))
            {
                uniqueSegments.Add(seg);
            }
        }

        // 2. Estimate token costs if not present
        for (int i = 0; i < uniqueSegments.Count; i++)
        {
            var seg = uniqueSegments[i];
            if (seg.TokenCost <= 0)
            {
                int cost = EstimateTokens(seg.Content);
                uniqueSegments[i] = seg with { TokenCost = cost };
            }
        }

        // 3. Process conversation history (strip prior think blocks for compact Smeagle execution)
        var cleanedHistory = new List<ChatMessage>();
        if (history != null && history.Count > 0)
        {
            foreach (var msg in history)
            {
                if (msg.Role == ChatRole.Assistant)
                {
                    string cleaned = StripThinkBlocks(msg.Content);
                    cleanedHistory.Add(new ChatMessage(msg.Role, cleaned, msg.Name));
                }
                else
                {
                    cleanedHistory.Add(msg);
                }
            }
        }

        int historyTokens = cleanedHistory.Sum(m => EstimateTokens(m.Content));

        // 4. Budget budgeting & Eviction if over budget
        int totalStatic = uniqueSegments.Where(s => !s.Mutable).Sum(s => s.TokenCost);
        int totalDynamic = uniqueSegments.Where(s => s.Mutable).Sum(s => s.TokenCost) + historyTokens;
        int totalTokens = totalStatic + totalDynamic;

        if (totalTokens > targetBudget)
        {
            // Evict discretionary segments (priority < 80) according to their EvictionPolicy
            var retained = new List<PromptSegment>();
            int currentTotal = totalStatic + historyTokens;

            foreach (var seg in uniqueSegments.OrderByDescending(s => s.Priority))
            {
                if (seg.Required || seg.EvictionPolicy == EvictionPolicy.Never)
                {
                    retained.Add(seg);
                    continue;
                }

                if (currentTotal + seg.TokenCost <= hardLimit)
                {
                    retained.Add(seg);
                    currentTotal += seg.TokenCost;
                }
                // Otherwise dropped/evicted to maintain budget
            }
            uniqueSegments = retained;
        }

        // 5. Assemble system prompt sections
        var systemSb = new StringBuilder();
        var userSb = new StringBuilder();

        int systemCoreTokens = 0;
        int modelProfileTokens = 0;
        int currentStepTokens = 0;
        int actionContractTokens = 0;
        int toolIndexTokens = 0;
        int toolDetailsTokens = 0;
        int planTokens = 0;
        int worldStateTokens = 0;
        int toolResultTokens = 0;
        int ragTokens = 0;
        int attachmentTokens = 0;

        foreach (var seg in uniqueSegments.OrderByDescending(s => s.Priority))
        {
            summaries.Add(new PromptSegmentSummary(seg.Kind, seg.TokenCost, seg.Priority, seg.Reason));

            switch (seg.Kind)
            {
                case PromptSegmentKind.SystemCore:
                    systemCoreTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.ModelProfile:
                    modelProfileTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.ActionContract:
                    actionContractTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.ToolIndex:
                    toolIndexTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.ToolDetails:
                    toolDetailsTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.CurrentObjective:
                case PromptSegmentKind.CurrentStep:
                    currentStepTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.PlanSummary:
                    planTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.WorldState:
                    worldStateTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.RecentResults:
                    toolResultTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.RagContext:
                    ragTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                case PromptSegmentKind.AttachmentContext:
                    attachmentTokens += seg.TokenCost;
                    systemSb.AppendLine(seg.Content);
                    break;
                default:
                    systemSb.AppendLine(seg.Content);
                    break;
            }
        }

        string systemPrompt = systemSb.ToString().Trim();
        string userPrompt = userSb.ToString().Trim();

        var report = new PromptBudgetReport
        {
            TotalTokens = totalTokens,
            SystemCoreTokens = systemCoreTokens,
            ModelProfileTokens = modelProfileTokens,
            CurrentStepTokens = currentStepTokens,
            ActionContractTokens = actionContractTokens,
            ToolIndexTokens = toolIndexTokens,
            ToolDetailsTokens = toolDetailsTokens,
            PlanTokens = planTokens,
            WorldStateTokens = worldStateTokens,
            HistoryTokens = historyTokens,
            ToolResultTokens = toolResultTokens,
            RagTokens = ragTokens,
            AttachmentTokens = attachmentTokens,
            StaticPrefixTokens = systemCoreTokens + modelProfileTokens + toolIndexTokens,
            DynamicSuffixTokens = currentStepTokens + planTokens + toolDetailsTokens + historyTokens + toolResultTokens,
            ExceededSoftBudget = totalTokens > targetBudget,
            ExceededHardBudget = totalTokens > hardLimit,
            Segments = summaries
        };

        return new CompiledContextResult(
            SystemPrompt: systemPrompt,
            UserPrompt: userPrompt,
            FullCombinedPrompt: systemPrompt,
            BudgetReport: report,
            SlicedMessages: cleanedHistory);
    }

    /// <summary>
    /// Builds a compact tool index (~10 lines) and includes full schemas only for relevant tools.
    /// </summary>
    public static (PromptSegment ToolIndex, PromptSegment? ToolDetails) BuildCompactToolSegments(
        IReadOnlyList<ToolDefinition> registeredTools,
        IReadOnlySet<string>? activeToolNames = null)
    {
        var indexSb = new StringBuilder();
        indexSb.AppendLine("## AVAILABLE TOOLS");

        var detailsSb = new StringBuilder();
        detailsSb.AppendLine("## ACTIVE TOOL SCHEMAS");

        bool hasDetails = false;

        foreach (var tool in registeredTools)
        {
            indexSb.AppendLine($"- {tool.Name}: {tool.Description}");

            if (activeToolNames != null && activeToolNames.Contains(tool.Name))
            {
                hasDetails = true;
                detailsSb.AppendLine($"### Tool: {tool.Name}");
                detailsSb.AppendLine(tool.Description);
                if (tool.Parameters != null && tool.Parameters.Count > 0)
                {
                    detailsSb.AppendLine("Parameters:");
                    foreach (var p in tool.Parameters)
                    {
                        detailsSb.AppendLine($"  - {p.Name} ({p.Type}, {(p.Required ? "required" : "optional")}): {p.Description}");
                    }
                }
            }
        }

        var indexSeg = new PromptSegment
        {
            Kind = PromptSegmentKind.ToolIndex,
            Priority = 90,
            Content = indexSb.ToString().Trim(),
            TokenCost = EstimateTokens(indexSb.ToString()),
            Mutable = false,
            Required = true
        };

        PromptSegment? detailsSeg = hasDetails
            ? new PromptSegment
            {
                Kind = PromptSegmentKind.ToolDetails,
                Priority = 85,
                Content = detailsSb.ToString().Trim(),
                TokenCost = EstimateTokens(detailsSb.ToString()),
                Mutable = true,
                Required = false
            }
            : null;

        return (indexSeg, detailsSeg);
    }

    /// <summary>
    /// Slices an execution plan to inject only the completed summary, current active step, and next preview.
    /// </summary>
    public static PromptSegment BuildSlicedPlanSegment(ExecutionPlan plan, int currentStepIndex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## CURRENT PLAN SLICE");

        int total = plan.Tasks.Count;
        int completed = plan.Tasks.Count(t => t.Status == TaskStepStatus.Completed);

        sb.AppendLine($"Plan Status: {completed}/{total} tasks complete");

        if (currentStepIndex >= 0 && currentStepIndex < total)
        {
            sb.AppendLine($"CURRENT ACTIVE TASK ({currentStepIndex + 1}/{total}):");
            sb.AppendLine($"> {plan.Tasks[currentStepIndex].Description}");

            if (currentStepIndex + 1 < total)
            {
                sb.AppendLine($"NEXT UP:");
                sb.AppendLine($"> {plan.Tasks[currentStepIndex + 1].Description}");
            }
        }

        string content = sb.ToString().Trim();
        return new PromptSegment
        {
            Kind = PromptSegmentKind.PlanSummary,
            Priority = 80,
            Content = content,
            TokenCost = EstimateTokens(content),
            Mutable = true,
            Required = false,
            Reason = "Active plan slice"
        };
    }

    /// <summary>
    /// Slices a large multi-item test suite or attachment into exactly one active test item.
    /// </summary>
    public static PromptSegment BuildSlicedAttachmentSegment(string attachmentName, int itemNumber, int totalItems, string itemPrompt, string expectedCapability, string preferredTools)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## ATTACHMENT SUITE: {attachmentName}");
        sb.AppendLine($"CURRENT STEP {itemNumber}/{totalItems}:");
        sb.AppendLine(itemPrompt);
        sb.AppendLine();
        sb.AppendLine($"Expected capability: {expectedCapability}");
        sb.AppendLine($"Preferred tools: {preferredTools}");

        string content = sb.ToString().Trim();
        return new PromptSegment
        {
            Kind = PromptSegmentKind.AttachmentContext,
            Priority = 85,
            Content = content,
            TokenCost = EstimateTokens(content),
            Mutable = true,
            Required = true,
            Reason = $"Sliced item {itemNumber} of {attachmentName}"
        };
    }

    /// <summary>
    /// Removes raw &lt;think&gt;...&lt;/think&gt; tags from prior assistant messages to prevent context explosion.
    /// </summary>
    public static string StripThinkBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Remove <think>...</think> blocks
        string cleaned = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    /// <summary>
    /// Fast token estimation (~3.5 characters per token).
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 3.5);
    }
}

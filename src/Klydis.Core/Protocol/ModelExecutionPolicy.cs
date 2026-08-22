using System;

namespace Klydis.Core.Protocol;

/// <summary>
/// Model-specific execution policy governing action throttling, parallel tool execution,
/// tool projection, repair envelopes, and prompt contracts.
/// Normalizes model behavioral differences so smaller/weaker models remain reliable.
/// </summary>
public sealed record ModelExecutionPolicy
{
    /// <summary>Family name or identifier for telemetry.</summary>
    public required string FamilyName { get; init; }

    /// <summary>Maximum number of tool calls permitted in a single model turn/generation.</summary>
    public int MaxActionsPerGeneration { get; init; } = 2;

    /// <summary>Whether parallel/multi-tool calling within one generation is permitted.</summary>
    public bool AllowParallelTools { get; init; } = false;

    /// <summary>Whether task_complete may be emitted in a batch alongside other action calls.</summary>
    public bool AllowTaskCompleteInToolBatch { get; init; } = false;

    /// <summary>Whether the exposed tool surface is dynamically compressed to context-relevant tools.</summary>
    public bool ContextualToolProjection { get; init; } = true;

    /// <summary>Maximum number of tools exposed in prompt during contextual tool projection.</summary>
    public int MaxProjectedTools { get; init; } = 12;

    /// <summary>Whether the runtime should prefer Generic JSON protocol over native template tags.</summary>
    public bool PreferGenericJson { get; init; } = false;

    /// <summary>Whether shell commands wrapping registered tools (e.g. run_command("system_cpu_usage")) are auto-normalized.</summary>
    public bool NormalizeWrongWrappers { get; init; } = true;

    /// <summary>Whether the model is strictly forbidden from providing free-form answers before acquiring tool evidence.</summary>
    public bool RequireEvidenceBeforeAnswer { get; init; } = false;

    /// <summary>Whether internal orchestration meta-language is stripped from model prompts.</summary>
    public bool StripInternalSupervisorJargon { get; init; } = true;

    /// <summary>Maximum failed action attempts permitted on a single objective before triggering recovery.</summary>
    public int MaxFailuresPerObjective { get; init; } = 3;

    /// <summary>Maximum unknown/hallucinated tool attempts before triggering recovery.</summary>
    public int MaxUnknownTools { get; init; } = 2;

    /// <summary>
    /// Derives the execution policy from the loaded ModelProfile.
    /// </summary>
    public static ModelExecutionPolicy FromModelProfile(ModelProfile? profile)
    {
        if (profile == null)
        {
            return DefaultPolicy;
        }

        string id = (profile.ModelId + " " + profile.Architecture + " " + profile.ModelPath).ToLowerInvariant();

        // 1. Qwen 3.5 9B — Active Autonomous Worker
        if (id.Contains("9b") && id.Contains("qwen"))
        {
            return new ModelExecutionPolicy
            {
                FamilyName = "Qwen-9B-Worker",
                MaxActionsPerGeneration = 3,
                AllowParallelTools = true,
                AllowTaskCompleteInToolBatch = false,
                ContextualToolProjection = true,
                MaxProjectedTools = 12,
                PreferGenericJson = false,
                NormalizeWrongWrappers = true,
                RequireEvidenceBeforeAnswer = false,
                StripInternalSupervisorJargon = true,
                MaxFailuresPerObjective = 3,
                MaxUnknownTools = 2
            };
        }

        // 2. Qwen 3.6 12B — Reasoning / Analysis Model (Minimal Contract)
        if (id.Contains("12b") && id.Contains("qwen"))
        {
            return new ModelExecutionPolicy
            {
                FamilyName = "Qwen-12B-Reasoning",
                MaxActionsPerGeneration = 1,
                AllowParallelTools = false,
                AllowTaskCompleteInToolBatch = false,
                ContextualToolProjection = true,
                MaxProjectedTools = 8,
                PreferGenericJson = false,
                NormalizeWrongWrappers = true,
                RequireEvidenceBeforeAnswer = false,
                StripInternalSupervisorJargon = true,
                MaxFailuresPerObjective = 2,
                MaxUnknownTools = 1
            };
        }

        // 3. Qwen 3.6 14B MoE — Planner / Constrained Executor
        if (id.Contains("14b") || id.Contains("qwen35moe") || (id.Contains("moe") && id.Contains("qwen")))
        {
            return new ModelExecutionPolicy
            {
                FamilyName = "Qwen-14B-MoE-Planner",
                MaxActionsPerGeneration = 1,
                AllowParallelTools = false,
                AllowTaskCompleteInToolBatch = false,
                ContextualToolProjection = true,
                MaxProjectedTools = 8,
                PreferGenericJson = false,
                NormalizeWrongWrappers = true,
                RequireEvidenceBeforeAnswer = false,
                StripInternalSupervisorJargon = true,
                MaxFailuresPerObjective = 3,
                MaxUnknownTools = 2
            };
        }

        // 4. Mistral 7B Instruct — Constrained Tactical Model
        if (id.Contains("mistral") || id.Contains("mixtral"))
        {
            return new ModelExecutionPolicy
            {
                FamilyName = "Mistral-7B-Restricted",
                MaxActionsPerGeneration = 1,
                AllowParallelTools = false,
                AllowTaskCompleteInToolBatch = false,
                ContextualToolProjection = true,
                MaxProjectedTools = 8,
                PreferGenericJson = true,
                NormalizeWrongWrappers = true,
                RequireEvidenceBeforeAnswer = true,
                StripInternalSupervisorJargon = true,
                MaxFailuresPerObjective = 2,
                MaxUnknownTools = 1
            };
        }

        // 5. Llama 3.3 8B — Synthesizer (Evidence-First Gating)
        if (id.Contains("llama"))
        {
            return new ModelExecutionPolicy
            {
                FamilyName = "Llama-8B-Synthesizer",
                MaxActionsPerGeneration = 1,
                AllowParallelTools = false,
                AllowTaskCompleteInToolBatch = false,
                ContextualToolProjection = true,
                MaxProjectedTools = 8,
                PreferGenericJson = false,
                NormalizeWrongWrappers = true,
                RequireEvidenceBeforeAnswer = true,
                StripInternalSupervisorJargon = true,
                MaxFailuresPerObjective = 2,
                MaxUnknownTools = 1
            };
        }

        return DefaultPolicy;
    }

    public static readonly ModelExecutionPolicy DefaultPolicy = new()
    {
        FamilyName = "Generic-Balanced",
        MaxActionsPerGeneration = 2,
        AllowParallelTools = false,
        AllowTaskCompleteInToolBatch = false,
        ContextualToolProjection = true,
        MaxProjectedTools = 10,
        PreferGenericJson = false,
        NormalizeWrongWrappers = true,
        RequireEvidenceBeforeAnswer = false,
        StripInternalSupervisorJargon = true,
        MaxFailuresPerObjective = 3,
        MaxUnknownTools = 2
    };
}

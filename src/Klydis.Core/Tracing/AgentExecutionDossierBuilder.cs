using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Klydis.Core.Chat;
using Klydis.Core.Memory;
using Klydis.Core.Protocol;
using Klydis.Core.Tasks;

namespace Klydis.Core.Tracing;

/// <summary>
/// Formatter and exporter that transforms raw agent trace streams, database messages,
/// and execution state into a comprehensive Agent Execution Dossier (Markdown or JSONL stream).
/// Centralizes high-resolution monotonic and wall-clock performance metrics.
/// </summary>
public static class AgentExecutionDossierBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds a machine-readable JSONL trace export where each line is a serialized <see cref="AgentTraceEvent"/>.
    /// </summary>
    public static async Task<string> BuildJsonlTraceAsync(
        string sessionId,
        MessageStore messageStore,
        IAgentTrace? traceService = null)
    {
        var events = await LoadAllTraceEventsAsync(sessionId, messageStore, traceService).ConfigureAwait(false);
        var sb = new StringBuilder();

        foreach (var evt in events)
        {
            var sanitized = SanitizeEventForExport(evt);
            sb.AppendLine(JsonSerializer.Serialize(sanitized, JsonOptions));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the complete human-readable Markdown Agent Execution Dossier with full timing summaries.
    /// </summary>
    public static async Task<string> BuildMarkdownDossierAsync(
        string sessionId,
        MessageStore messageStore,
        IAgentTrace? traceService = null,
        ModelProfile? modelProfile = null,
        IModelProtocol? protocolAdapter = null,
        uint contextSize = 0,
        string? appVersion = null)
    {
        var session = await messageStore.GetSessionAsync(sessionId).ConfigureAwait(false);
        var messages = await messageStore.GetMessagesAsync(sessionId, null).ConfigureAwait(false);
        var events = await LoadAllTraceEventsAsync(sessionId, messageStore, traceService).ConfigureAwait(false);
        var tasks = await messageStore.GetTasksBySessionAsync(sessionId).ConfigureAwait(false);
        var currentTask = tasks.LastOrDefault();
        var toolActivities = currentTask != null ? await messageStore.GetToolActivityByTaskAsync(currentTask.TaskId).ConfigureAwait(false) : await messageStore.GetToolActivityBySessionAsync(sessionId).ConfigureAwait(false);
        var artifacts = await messageStore.GetArtifactsBySessionAsync(sessionId).ConfigureAwait(false);

        var sb = new StringBuilder();

        // 1. HEADER
        sb.AppendLine("============================================================");
        sb.AppendLine("KLYDIS AGENT EXECUTION LOG");
        sb.AppendLine("============================================================");
        sb.AppendLine($"App version: {appVersion ?? "1.0.0"}");
        sb.AppendLine($"Session: {session?.Title ?? "Untitled Session"}");
        sb.AppendLine($"SessionId: {sessionId}");
        sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string taskIdDisplay = currentTask?.TaskId ?? "(no active task)";
        string taskObjectiveDisplay = currentTask?.Objective ?? "(none)";
        string taskStatusDisplay = currentTask?.Status.ToString() ?? "(unknown)";

        sb.AppendLine();
        sb.AppendLine("--- TASK ---");
        sb.AppendLine($"TaskId: {taskIdDisplay}");
        sb.AppendLine($"Status: {taskStatusDisplay}");
        sb.AppendLine($"Objective: {taskObjectiveDisplay}");

        sb.AppendLine();
        sb.AppendLine("--- MODEL ---");
        if (modelProfile == null)
        {
            sb.AppendLine("(no model profile available — model not loaded)");
        }
        else
        {
            sb.AppendLine($"Model: {modelProfile.ModelId}");
            sb.AppendLine($"Path: {modelProfile.ModelPath}");
            sb.AppendLine($"Architecture: {modelProfile.Architecture}");
            sb.AppendLine($"ChatTemplate: {modelProfile.Template}");
            sb.AppendLine($"Reasoning: {modelProfile.Reasoning}");
            sb.AppendLine($"ToolProtocol: {modelProfile.ToolProtocol}");
            sb.AppendLine($"PreferredProtocol: {modelProfile.PreferredProtocol}");
            sb.AppendLine($"SupportedProtocols: {string.Join(", ", modelProfile.SupportedProtocols)}");
            sb.AppendLine($"NativeTools: {modelProfile.SupportsNativeTools} | StructuredOutput: {modelProfile.SupportsStructuredOutput} | Grammar: {modelProfile.SupportsGrammar} | Thinking: {modelProfile.SupportsThinking} | Continuation: {modelProfile.SupportsToolContinuation}");
            sb.AppendLine($"ProtocolConfidence: {modelProfile.ProtocolConfidence:0.00}");
            sb.AppendLine($"ProtocolKey: {ProtocolRegistry.ResolveProtocolKey(modelProfile) ?? "legacy-fallback"}");
            sb.AppendLine($"Adapter: {protocolAdapter?.GetType().Name ?? "legacy-fallback"}");
            sb.AppendLine($"Fingerprint: {modelProfile.Fingerprint}");
        }
        sb.AppendLine($"ContextSize: {contextSize} tokens");

        // 2. TIMING SUMMARY
        BuildTimingSummary(sb, events, messages);

        // 3. EXECUTION SUMMARY
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("EXECUTION SUMMARY");
        sb.AppendLine("============================================================");

        var distinctTurns = events.Where(e => !string.IsNullOrEmpty(e.TurnId)).Select(e => e.TurnId!).Distinct().Count();
        int totalTurns = distinctTurns > 0 ? distinctTurns : events.Count(e => e.Type == TraceEventType.TurnStarted);
        if (totalTurns == 0) totalTurns = messages.Count(m => m.Role == ChatRole.User);

        var distinctGenerations = events.Where(e => !string.IsNullOrEmpty(e.GenerationId)).Select(e => e.GenerationId!).Distinct().Count();
        int generations = distinctGenerations > 0 ? distinctGenerations : events.Count(e => e.Type is TraceEventType.GenerationStarted or TraceEventType.InferenceStarted);
        if (generations == 0) generations = messages.Count(m => m.Role == ChatRole.Assistant);

        int toolSuccesses = events.Count(e => e.Type == TraceEventType.ToolExecutionCompleted);
        int toolFailures = events.Count(e => e.Type == TraceEventType.ToolExecutionFailed);
        int toolRejections = events.Count(e => e.Type == TraceEventType.ToolCallRejected);

        if (toolSuccesses == 0 && toolFailures == 0)
        {
            toolSuccesses = events.Count(e => e.Type == TraceEventType.ToolResultDelivered && (e.Data?.TryGetValue("success", out var s) == true && true.Equals(s)));
            toolFailures = events.Count(e => e.Type == TraceEventType.ToolResultDelivered && (e.Data?.TryGetValue("success", out var s2) == true && false.Equals(s2)));
        }

        int totalPhysicalExecutions = toolSuccesses + toolFailures;
        var distinctTools = events.Where(e => !string.IsNullOrEmpty(e.ToolExecutionId)).Select(e => e.ToolExecutionId!).Distinct().Count();
        int toolCallsProposed = distinctTools > 0 ? distinctTools : (totalPhysicalExecutions + toolRejections);

        if (toolCallsProposed == 0 && toolActivities.Count > 0)
        {
            toolCallsProposed = toolActivities.Count;
            toolSuccesses = toolActivities.Count(a => a.Success);
            toolFailures = toolActivities.Count(a => !a.Success);
        }

        int retries = events.Count(e => e.Type == TraceEventType.RetryStarted);
        int repairs = events.Count(e => e.Type == TraceEventType.RepairStarted);
        int skills = events.Count(e => e.Type == TraceEventType.SkillInvoked || e.Type == TraceEventType.SkillInvocationStarted || e.Type == TraceEventType.SkillCompleted);
        int webOps = events.Count(e => e.Type is TraceEventType.WebSearchStarted or TraceEventType.PageOpened or TraceEventType.PageFetched or TraceEventType.ScrapeStarted);
        int artifactCount = artifacts.Count + events.Count(e => e.Type == TraceEventType.ArtifactCreated);
        int totalTokens = messages.Sum(m => m.TokenCount);

        sb.AppendLine($"Task status: {taskStatusDisplay}");
        sb.AppendLine($"Turns: {totalTurns}");
        sb.AppendLine($"Generations: {generations}");
        sb.AppendLine($"Tool calls: {toolCallsProposed}");
        sb.AppendLine($"Tool successes: {toolSuccesses}");
        sb.AppendLine($"Tool failures: {toolFailures}");
        sb.AppendLine($"Retries: {retries}");
        sb.AppendLine($"Repairs: {repairs}");
        sb.AppendLine($"Skills: {skills}");
        sb.AppendLine($"Web operations: {webOps}");
        sb.AppendLine($"Artifacts: {artifactCount}");
        sb.AppendLine($"Tokens: {totalTokens:N0}");

        // 4. TASK STATE
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("TASK STATE");
        sb.AppendLine("============================================================");
        if (currentTask != null)
        {
            sb.AppendLine($"Task ID: {currentTask.TaskId}");
            sb.AppendLine($"Objective: {currentTask.Objective}");
            sb.AppendLine($"Status: {currentTask.Status}");
            sb.AppendLine($"Created: {currentTask.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Updated: {currentTask.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(currentTask.Summary))
            {
                sb.AppendLine($"Summary: {currentTask.Summary}");
            }
        }
        else
        {
            sb.AppendLine("(no task record)");
        }

        // 5. PLAN
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("PLAN");
        sb.AppendLine("============================================================");
        string? planJson = currentTask?.PlanJson ?? session?.PlanJson;
        if (!string.IsNullOrWhiteSpace(planJson))
        {
            try
            {
                var planList = JsonSerializer.Deserialize<List<ToolExecutor.PlanEntry>>(planJson);
                if (planList != null && planList.Count > 0)
                {
                    int stepNum = 1;
                    foreach (var p in planList)
                    {
                        string check = p.Done ? "[x]" : "[ ]";
                        sb.AppendLine($"{check} Step {stepNum++}: {p.Text}");
                    }
                }
                else
                {
                    sb.AppendLine(planJson);
                }
            }
            catch
            {
                sb.AppendLine(planJson);
            }
        }
        else
        {
            sb.AppendLine("(no active plan)");
        }

        // 6. EXECUTION TREE
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("EXECUTION TREE");
        sb.AppendLine("============================================================");
        BuildExecutionTree(sb, events, messages, currentTask);

        // 7. EXECUTION TIMELINE
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("EXECUTION TIMELINE");
        sb.AppendLine("============================================================");
        BuildExecutionTimeline(sb, events, messages);

        // 8. ERRORS
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("ERRORS & EXCEPTIONS");
        sb.AppendLine("============================================================");
        var errorEvents = events.Where(e => e.Type is TraceEventType.Error or TraceEventType.Crash or TraceEventType.ToolExecutionFailed or TraceEventType.OutputParseFailed).ToList();
        if (errorEvents.Count == 0)
        {
            sb.AppendLine("(no recorded runtime errors)");
        }
        else
        {
            foreach (var err in errorEvents)
            {
                sb.AppendLine($"[{err.Timestamp:HH:mm:ss.fff}] {err.Type}");
                if (!string.IsNullOrEmpty(err.TaskId)) sb.AppendLine($"  task_id: {err.TaskId}");
                if (!string.IsNullOrEmpty(err.RunId)) sb.AppendLine($"  run_id: {err.RunId}");
                if (!string.IsNullOrEmpty(err.TurnId)) sb.AppendLine($"  turn_id: {err.TurnId}");
                if (!string.IsNullOrEmpty(err.ToolExecutionId)) sb.AppendLine($"  tool_execution_id: {err.ToolExecutionId}");
                if (err.Data != null)
                {
                    foreach (var (k, v) in err.Data)
                    {
                        sb.AppendLine($"  {k}: {FormatValue(v)}");
                    }
                }
                sb.AppendLine();
            }
        }

        // 9. ARTIFACTS
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("ARTIFACTS");
        sb.AppendLine("============================================================");
        if (artifacts.Count == 0)
        {
            sb.AppendLine("(no artifacts produced)");
        }
        else
        {
            foreach (var a in artifacts)
            {
                sb.AppendLine($"- ID: {a.ArtifactId} | Type: {a.ArtifactType} | Current: {a.IsCurrent}");
                sb.AppendLine($"  Path: {a.Path}");
                sb.AppendLine($"  Created: {a.CreatedAtUtc:yyyy-MM-dd HH:mm:ss UTC}");
            }
        }

        // 10. AGENT DIAGNOSTICS
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("AGENT DIAGNOSTICS");
        sb.AppendLine("============================================================");
        double toolSuccessRate = (toolSuccesses + toolFailures) > 0 ? ((double)toolSuccesses / (toolSuccesses + toolFailures)) * 100.0 : 100.0;
        int validActions = toolSuccesses + toolFailures;
        int invalidActions = toolRejections + events.Count(e => e.Type == TraceEventType.OutputParseFailed);
        int finalClaims = events.Count(e => e.Type == TraceEventType.VerificationCompleted || (e.Data?.ContainsKey("task_complete") == true));
        int noActionGenerations = events.Count(e => e.Type == TraceEventType.ModelOutput && (e.Data?.TryGetValue("action_count", out var ac) == true && Convert.ToInt32(ac) == 0));
        int continuedDecisions = events.Count(e => e.Type == TraceEventType.ContinuationDecision);
        int timedOutTools = events.Count(e => e.Type == TraceEventType.ToolExecutionFailed && e.Data?.TryGetValue("reason", out var r) == true && r?.ToString()?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true);

        sb.AppendLine($"Tool-call success rate: {toolSuccessRate:F1}%");
        sb.AppendLine();
        sb.AppendLine("Model outputs:");
        sb.AppendLine($"    valid actions: {validActions}");
        sb.AppendLine($"    invalid actions: {invalidActions}");
        sb.AppendLine($"    final claims: {finalClaims}");
        sb.AppendLine($"    no-action generations: {noActionGenerations}");
        sb.AppendLine();
        sb.AppendLine("Continuation:");
        sb.AppendLine($"    continued: {continuedDecisions}");
        sb.AppendLine($"    retried: {retries}");
        sb.AppendLine($"    replanned: {events.Count(e => e.Type == TraceEventType.PlanChanged)}");
        sb.AppendLine($"    premature completion attempts: {events.Count(e => e.Type == TraceEventType.OutputRejected)}");
        sb.AppendLine();
        sb.AppendLine("Execution:");
        sb.AppendLine($"    successful: {toolSuccesses}");
        sb.AppendLine($"    failed: {toolFailures}");
        sb.AppendLine($"    rejected: {toolRejections}");
        sb.AppendLine($"    timed out: {timedOutTools}");
        sb.AppendLine();
        sb.AppendLine("Task:");
        sb.AppendLine($"    status: {taskStatusDisplay}");
        sb.AppendLine($"    objective: {taskObjectiveDisplay}");
        sb.AppendLine();
        sb.AppendLine("Budget:");
        sb.AppendLine($"    turns: {totalTurns}");
        sb.AppendLine($"    tokens: {totalTokens:N0}");
        sb.AppendLine($"    tool calls: {toolCallsProposed}");

        // 11. FINAL RESULT
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("FINAL RESULT");
        sb.AppendLine("============================================================");
        var lastAssistantMsg = messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastAssistantMsg != null)
        {
            sb.AppendLine(lastAssistantMsg.Content);
        }
        else
        {
            sb.AppendLine("(no final assistant response)");
        }

        return sb.ToString();
    }

    private static void BuildTimingSummary(
        StringBuilder sb,
        List<AgentTraceEvent> events,
        List<MessageRecord> messages)
    {
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("TIMING SUMMARY");
        sb.AppendLine("============================================================");

        DateTimeOffset earliest = DateTimeOffset.MaxValue;
        DateTimeOffset latest = DateTimeOffset.MinValue;

        foreach (var evt in events)
        {
            if (evt.TimestampUtc < earliest) earliest = evt.TimestampUtc;
            if (evt.TimestampUtc > latest) latest = evt.TimestampUtc;
        }

        foreach (var msg in messages)
        {
            var dto = new DateTimeOffset(DateTime.SpecifyKind(msg.Timestamp, DateTimeKind.Utc));
            if (dto < earliest) earliest = dto;
            if (dto > latest) latest = dto;
        }

        if (earliest == DateTimeOffset.MaxValue) earliest = DateTimeOffset.UtcNow;
        if (latest == DateTimeOffset.MinValue) latest = earliest;

        double totalWallMs = (latest - earliest).TotalMilliseconds;
        if (totalWallMs < 0) totalWallMs = 0;

        // Categorized durations
        double modelInferenceMs = 0;
        double toolExecutionMs = 0;
        double skillExecutionMs = 0;
        double webOpsMs = 0;
        double planningMs = 0;
        double parsingMs = 0;
        double verificationMs = 0;
        double userWaitMs = 0;
        double queueWaitMs = 0;
        double contextBuildMs = 0;
        double promptConstructionMs = 0;
        double compactionMs = 0;
        double schedulingMs = 0;
        double persistenceMs = 0;
        double recoveryMs = 0;
        double claimValidationMs = 0;

        var genDurations = new List<double>();
        var ttftList = new List<double>();
        var toolDurations = new List<double>();
        int totalGeneratedTokens = 0;
        double totalStreamingSeconds = 0;

        var seenGenDurationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenGenTokenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTtftIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            double dur = evt.DurationMs ?? 0;

            if (evt.Category.HasValue)
            {
                switch (evt.Category.Value)
                {
                    case AgentTimingCategory.ModelInference:
                        modelInferenceMs += dur;
                        break;
                    case AgentTimingCategory.ToolExecution:
                        toolExecutionMs += dur;
                        break;
                    case AgentTimingCategory.SkillExecution:
                        skillExecutionMs += dur;
                        break;
                    case AgentTimingCategory.WebNetwork or AgentTimingCategory.WebParsing or AgentTimingCategory.WebExtraction:
                        webOpsMs += dur;
                        break;
                    case AgentTimingCategory.Planning:
                        planningMs += dur;
                        break;
                    case AgentTimingCategory.Parsing or AgentTimingCategory.Validation:
                        parsingMs += dur;
                        break;
                    case AgentTimingCategory.Verification or AgentTimingCategory.EvidenceProcessing:
                        verificationMs += dur;
                        break;
                    case AgentTimingCategory.UserWait:
                        userWaitMs += dur;
                        break;
                    case AgentTimingCategory.QueueWait or AgentTimingCategory.ModelQueueWait or AgentTimingCategory.ToolQueueWait:
                        queueWaitMs += dur;
                        break;
                    case AgentTimingCategory.ContextBuild:
                        contextBuildMs += dur;
                        break;
                    case AgentTimingCategory.PromptConstruction or AgentTimingCategory.TemplateRendering or AgentTimingCategory.TokenCounting:
                        promptConstructionMs += dur;
                        break;
                    case AgentTimingCategory.Compaction:
                        compactionMs += dur;
                        break;
                    case AgentTimingCategory.Scheduling:
                        schedulingMs += dur;
                        break;
                    case AgentTimingCategory.Persistence:
                        persistenceMs += dur;
                        break;
                    case AgentTimingCategory.Recovery:
                        recoveryMs += dur;
                        break;
                    case AgentTimingCategory.ClaimValidation:
                        claimValidationMs += dur;
                        break;
                }
            }
            else
            {
                if (evt.Type is TraceEventType.InferenceCompleted or TraceEventType.GenerationCompleted)
                    modelInferenceMs += dur;
                else if (evt.Type is TraceEventType.ToolExecutionCompleted or TraceEventType.ToolExecutionFailed)
                    toolExecutionMs += dur;
                else if (evt.Type is TraceEventType.SkillInvocationCompleted)
                    skillExecutionMs += dur;
                else if (evt.Type is TraceEventType.WebSearchCompleted or TraceEventType.ScrapeCompleted)
                    webOpsMs += dur;
                else if (evt.Type is TraceEventType.VerificationCompleted)
                    verificationMs += dur;
                else if (evt.Type is TraceEventType.CompactionCompleted)
                    compactionMs += dur;
                else if (evt.Type is TraceEventType.RepairCompleted)
                    recoveryMs += dur;
            }

            // Extract TTFT from FirstTokenReceived events
            if (evt.Type == TraceEventType.FirstTokenReceived)
            {
                string genKey = evt.GenerationId ?? evt.EventId;
                if (!seenTtftIds.Contains(genKey))
                {
                    seenTtftIds.Add(genKey);
                    if (dur > 0)
                    {
                        ttftList.Add(dur);
                    }
                    if (evt.Data != null && (evt.Data.TryGetValue("ttft_ms", out var ttVal) || evt.Data.TryGetValue("time_to_first_token_ms", out ttVal)))
                    {
                        if (TryGetDouble(ttVal, out double tt) && tt > 0)
                            ttftList.Add(tt);
                    }
                }
            }

            // Extract generation micro-metrics from InferenceCompleted or RawModelOutput
            if (evt.Type is TraceEventType.InferenceCompleted or TraceEventType.RawModelOutput or TraceEventType.GenerationCompleted)
            {
                string genKey = evt.GenerationId ?? evt.EventId;

                if (dur > 0 && !seenGenDurationIds.Contains(genKey))
                {
                    seenGenDurationIds.Add(genKey);
                    genDurations.Add(dur);
                }

                if (evt.Data != null)
                {
                    if (!seenTtftIds.Contains(genKey) && (evt.Data.TryGetValue("time_to_first_token_ms", out var ttftVal) || evt.Data.TryGetValue("ttft_ms", out ttftVal)))
                    {
                        if (TryGetDouble(ttftVal, out double ttft) && ttft > 0)
                        {
                            seenTtftIds.Add(genKey);
                            ttftList.Add(ttft);
                        }
                    }

                    if (!seenGenTokenIds.Contains(genKey))
                    {
                        int tokensFound = 0;
                        if (evt.Data.TryGetValue("output_tokens", out var otVal) && TryGetInt(otVal, out int ot) && ot > 0)
                        {
                            tokensFound = ot;
                        }
                        else if (evt.Data.TryGetValue("tokens", out var tVal) && TryGetInt(tVal, out int t) && t > 0)
                        {
                            tokensFound = t;
                        }

                        if (tokensFound > 0)
                        {
                            seenGenTokenIds.Add(genKey);
                            totalGeneratedTokens += tokensFound;
                        }
                    }

                    if (evt.Data.TryGetValue("streaming_duration_ms", out var sdVal) && TryGetDouble(sdVal, out double sd) && sd > 0)
                    {
                        totalStreamingSeconds += (sd / 1000.0);
                    }
                }
            }

            if (evt.Type is TraceEventType.ToolExecutionCompleted or TraceEventType.ToolExecutionFailed)
            {
                if (dur > 0) toolDurations.Add(dur);
            }
        }

        // Fallback for token count if trace events did not contain token counts
        if (totalGeneratedTokens == 0)
        {
            totalGeneratedTokens = messages.Where(m => m.Role == ChatRole.Assistant).Sum(m => m.TokenCount);
        }

        // Compute inter-turn user wait time if multiple user messages exist
        var userTurnStarts = events.Where(e => e.Type == TraceEventType.TurnStarted).OrderBy(e => e.TimestampUtc).ToList();
        var userTurnEnds = events.Where(e => e.Type == TraceEventType.TurnCompleted).OrderBy(e => e.TimestampUtc).ToList();
        if (userTurnStarts.Count > 1 && userTurnEnds.Count > 0)
        {
            for (int i = 0; i < userTurnStarts.Count - 1; i++)
            {
                var endOfPrev = userTurnEnds.ElementAtOrDefault(i)?.TimestampUtc ?? userTurnStarts[i].TimestampUtc;
                var startOfNext = userTurnStarts[i + 1].TimestampUtc;
                if (startOfNext > endOfPrev)
                {
                    userWaitMs += (startOfNext - endOfPrev).TotalMilliseconds;
                }
            }
        }

        double accountedActiveMs = modelInferenceMs + toolExecutionMs + skillExecutionMs + webOpsMs + planningMs + contextBuildMs + promptConstructionMs + parsingMs + verificationMs + compactionMs + schedulingMs + persistenceMs + recoveryMs + claimValidationMs;
        double waitingMs = userWaitMs + queueWaitMs;
        double unattributedMs = Math.Max(0, totalWallMs - accountedActiveMs - waitingMs);

        sb.AppendLine($"Started (UTC):                {earliest:yyyy-MM-dd HH:mm:ss.fff}Z");
        sb.AppendLine($"Ended (UTC):                  {latest:yyyy-MM-dd HH:mm:ss.fff}Z");
        sb.AppendLine($"Wall-clock duration:          {FormatDuration(totalWallMs)}");
        sb.AppendLine($"Accounted active time:        {FormatDuration(accountedActiveMs)}");
        sb.AppendLine($"Unattributed runtime:         {FormatDuration(unattributedMs)}");
        sb.AppendLine($"Waiting / idle time:          {FormatDuration(waitingMs)}");
        sb.AppendLine();
        sb.AppendLine("Subsystem Breakdown:");
        sb.AppendLine($"  Model inference:            {FormatDuration(modelInferenceMs)}");
        sb.AppendLine($"  Tool execution:             {FormatDuration(toolExecutionMs)}");
        sb.AppendLine($"  Skill execution:            {FormatDuration(skillExecutionMs)}");
        sb.AppendLine($"  Web operations:             {FormatDuration(webOpsMs)}");
        sb.AppendLine($"  Planning / state:           {FormatDuration(planningMs)}");
        sb.AppendLine($"  Context construction:       {FormatDuration(contextBuildMs + promptConstructionMs)}");
        sb.AppendLine($"  Parsing / validation:       {FormatDuration(parsingMs)}");
        sb.AppendLine($"  Verification / supervisor:  {FormatDuration(verificationMs + claimValidationMs)}");
        sb.AppendLine($"  Scheduling / dispatch:      {FormatDuration(schedulingMs)}");
        sb.AppendLine($"  Persistence / storage:      {FormatDuration(persistenceMs)}");
        sb.AppendLine($"  Recovery / repair:          {FormatDuration(recoveryMs)}");
        sb.AppendLine($"  Context compaction:         {FormatDuration(compactionMs)}");
        sb.AppendLine();

        int genCount = genDurations.Count;
        int toolCount = toolDurations.Count;
        double avgGenMs = genCount > 0 ? genDurations.Average() : 0;
        double avgToolMs = toolCount > 0 ? toolDurations.Average() : 0;
        double avgTtftMs = ttftList.Count > 0 ? ttftList.Average() : 0;
        double genSpeedTokPerSec = totalStreamingSeconds > 0 ? (totalGeneratedTokens / totalStreamingSeconds) : (modelInferenceMs > 0 ? (totalGeneratedTokens / (modelInferenceMs / 1000.0)) : 0);

        sb.AppendLine("Operational Velocity:");
        sb.AppendLine($"  Generations:                {genCount}");
        sb.AppendLine($"  Average generation:         {FormatDuration(avgGenMs)}");
        sb.AppendLine($"  Average Time To First Token:{FormatDuration(avgTtftMs)}");
        sb.AppendLine($"  Total generated tokens:     {totalGeneratedTokens:N0}");
        sb.AppendLine($"  Generation throughput:      {(genSpeedTokPerSec > 0 ? $"{genSpeedTokPerSec:F1} tok/s" : "n/a")}");
        sb.AppendLine($"  Tool executions:            {toolCount}");
        sb.AppendLine($"  Average tool execution:     {FormatDuration(avgToolMs)}");
    }

    public static string FormatDuration(double ms)
    {
        if (ms <= 0) return "0.0ms";
        if (ms < 1000) return $"{ms:F1}ms";
        if (ms < 60000) return $"{(ms / 1000.0):F3}s";
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}.{ts.Milliseconds:D3}s";
    }

    private static async Task<List<AgentTraceEvent>> LoadAllTraceEventsAsync(
        string sessionId,
        MessageStore messageStore,
        IAgentTrace? traceService)
    {
        var events = new List<AgentTraceEvent>();

        if (traceService != null)
        {
            var fromService = await traceService.GetEventsBySessionAsync(sessionId, 20000).ConfigureAwait(false);
            events.AddRange(fromService);
        }
        else
        {
            var fromDb = await messageStore.GetTraceEventsBySessionAsync(sessionId, 20000).ConfigureAwait(false);
            events.AddRange(fromDb);
        }

        return events.OrderBy(e => e.TimestampUtc).ToList();
    }

    private static AgentTraceEvent SanitizeEventForExport(AgentTraceEvent evt)
    {
        if (evt.Data == null) return evt;
        return evt with { Data = TraceSecretRedactor.RedactDictionary(evt.Data) };
    }

    private static void BuildExecutionTree(
        StringBuilder sb,
        List<AgentTraceEvent> events,
        List<MessageRecord> messages,
        AgentTask? currentTask)
    {
        string goalId = currentTask?.TaskId ?? "T-001";
        string goalObjective = currentTask?.Objective ?? "Chat Session";
        sb.AppendLine($"GOAL {goalId} ({goalObjective})");

        var runIds = events.Select(e => e.RunId).Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
        if (runIds.Count == 0) runIds.Add("R-001");

        for (int r = 0; r < runIds.Count; r++)
        {
            string runId = runIds[r]!;
            bool isLastRun = r == runIds.Count - 1;
            string runPrefix = isLastRun ? "└── " : "├── ";
            string runChildPrefix = isLastRun ? "    " : "│   ";
            sb.AppendLine($"{runPrefix}RUN {runId}");

            var runEvents = events.Where(e => e.RunId == runId || (string.IsNullOrEmpty(e.RunId) && runIds.Count == 1)).ToList();
            var turnIds = runEvents.Select(e => e.TurnId).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();

            if (turnIds.Count == 0)
            {
                // Group by User message turns
                int turnSeq = 1;
                foreach (var msg in messages.Where(m => m.Role == ChatRole.User))
                {
                    turnIds.Add($"TURN-{turnSeq++:D3}");
                }
            }

            for (int t = 0; t < turnIds.Count; t++)
            {
                string turnId = turnIds[t] ?? $"TURN-{t + 1:D3}";
                bool isLastTurn = t == turnIds.Count - 1;
                string turnPrefix = isLastTurn ? "└── " : "├── ";
                string turnChildPrefix = isLastTurn ? "    " : "│   ";
                sb.AppendLine($"{runChildPrefix}{turnPrefix}{turnId}");

                var turnEvents = runEvents.Where(e => e.TurnId == turnId).ToList();
                var generations = turnEvents.Where(e => e.Type == TraceEventType.GenerationStarted || e.Type == TraceEventType.RawModelOutput || e.Type == TraceEventType.InferenceCompleted).ToList();
                var toolCalls = turnEvents.Where(e => e.Type is TraceEventType.ToolCallProposed or TraceEventType.ToolExecutionStarted or TraceEventType.ToolResultDelivered).ToList();

                int genSeq = 1;
                foreach (var gen in generations)
                {
                    string genId = gen.GenerationId ?? $"G-{genSeq++:D3}";
                    string durStr = gen.DurationMs.HasValue ? $" ({FormatDuration(gen.DurationMs.Value)})" : "";
                    sb.AppendLine($"{runChildPrefix}{turnChildPrefix}├── Generation {genId}{durStr}");
                }

                int toolSeq = 1;
                foreach (var tc in toolCalls.Where(e => e.Type == TraceEventType.ToolExecutionStarted || e.Type == TraceEventType.ToolCallProposed))
                {
                    string toolName = tc.Data?.TryGetValue("tool", out var tname) == true ? tname?.ToString() ?? "tool" : "tool";
                    string execId = tc.ToolExecutionId ?? tc.ActionId ?? $"E-{toolSeq++:D3}";
                    string durStr = tc.DurationMs.HasValue ? $" ({FormatDuration(tc.DurationMs.Value)})" : "";
                    sb.AppendLine($"{runChildPrefix}{turnChildPrefix}├── Tool {execId} ({toolName}){durStr}");
                }

                if (turnEvents.Any(e => e.Type == TraceEventType.VerificationCompleted || e.Type == TraceEventType.RunCompleted))
                {
                    sb.AppendLine($"{runChildPrefix}{turnChildPrefix}└── Completion");
                }
            }
        }
    }

    private static void BuildExecutionTimeline(
        StringBuilder sb,
        List<AgentTraceEvent> events,
        List<MessageRecord> messages)
    {
        // Interleave database messages and structured trace events chronologically
        var timelineItems = new List<(DateTimeOffset Time, string Header, string Body)>();

        foreach (var msg in messages)
        {
            var dto = new DateTimeOffset(DateTime.SpecifyKind(msg.Timestamp, DateTimeKind.Utc));
            string role = msg.Role.ToString().ToUpperInvariant();
            string header = $"[{dto:HH:mm:ss.fff}] {role}";
            var body = new StringBuilder();
            body.AppendLine(msg.Content);
            if (!string.IsNullOrWhiteSpace(msg.ToolCallsJson))
            {
                body.AppendLine($"ToolCalls: {msg.ToolCallsJson}");
            }
            timelineItems.Add((dto, header, body.ToString().TrimEnd()));
        }

        foreach (var evt in events)
        {
            string durTag = evt.DurationMs.HasValue ? $" (duration={FormatDuration(evt.DurationMs.Value)})" : "";
            string catTag = evt.Category.HasValue ? $" [{evt.Category}]" : "";
            string header = $"[{evt.TimestampUtc:HH:mm:ss.fff}] {evt.Type}{catTag}{durTag}";
            var body = new StringBuilder();

            if (!string.IsNullOrEmpty(evt.TaskId)) body.AppendLine($"task_id={evt.TaskId}");
            if (!string.IsNullOrEmpty(evt.RunId)) body.AppendLine($"run_id={evt.RunId}");
            if (!string.IsNullOrEmpty(evt.TurnId)) body.AppendLine($"turn_id={evt.TurnId}");
            if (!string.IsNullOrEmpty(evt.GenerationId)) body.AppendLine($"generation_id={evt.GenerationId}");
            if (!string.IsNullOrEmpty(evt.ActionId)) body.AppendLine($"action_id={evt.ActionId}");
            if (!string.IsNullOrEmpty(evt.ToolExecutionId)) body.AppendLine($"tool_execution_id={evt.ToolExecutionId}");

            if (evt.Data != null)
            {
                foreach (var (k, v) in evt.Data)
                {
                    body.AppendLine($"{k}={FormatValue(v)}");
                }
            }

            timelineItems.Add((evt.TimestampUtc, header, body.ToString().TrimEnd()));
        }

        // Sort by timestamp
        foreach (var item in timelineItems.OrderBy(t => t.Time))
        {
            sb.AppendLine(item.Header);
            if (!string.IsNullOrWhiteSpace(item.Body))
            {
                sb.AppendLine(item.Body);
            }
            sb.AppendLine();
        }
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        if (value is bool b) return b ? "true" : "false";
        if (value is IDictionary<string, object?> dict)
        {
            return JsonSerializer.Serialize(dict, JsonOptions);
        }
        if (value is IDictionary<string, object> dictObj)
        {
            return JsonSerializer.Serialize(dictObj, JsonOptions);
        }
        if (value is JsonElement elem)
        {
            return elem.GetRawText();
        }
        return value.ToString() ?? "";
    }

    public static bool TryGetDouble(object? value, out double result)
    {
        result = 0;
        if (value == null) return false;
        if (value is double d) { result = d; return true; }
        if (value is float f) { result = f; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetDouble(out var dVal))
            {
                result = dVal;
                return true;
            }
            if (elem.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(elem.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
            }
            return false;
        }
        return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    public static bool TryGetInt(object? value, out int result)
    {
        result = 0;
        if (value == null) return false;
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = (int)l; return true; }
        if (value is JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var iVal))
            {
                result = iVal;
                return true;
            }
            if (elem.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(elem.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
            }
            return false;
        }
        return int.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}

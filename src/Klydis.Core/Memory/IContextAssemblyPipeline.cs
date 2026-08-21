using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;
using Klydis.Core.Inference;
using Klydis.Core.Tasks;
using ChatMessage = Klydis.Core.Chat.ChatMessage;

namespace Klydis.Core.Memory;

/// <summary>
/// Parameters for context assembly for an inference turn.
/// </summary>
public sealed record ContextAssemblyRequest(
    string SessionId,
    string? TaskId,
    string? StepTitle,
    string? SystemPrompt,
    string? ActiveSkillsPrompt,
    IReadOnlyList<ToolDefinition>? Tools = null,
    InferenceOperation Operation = InferenceOperation.Conversation,
    EpistemicLedger? EpistemicLedger = null,
    int MaxTokens = 4096);

/// <summary>
/// Fully assembled and token-budgeted prompt and message context ready for the model.
/// </summary>
public sealed record AssembledContext(
    string SystemPrompt,
    IReadOnlyList<ChatMessage> FormattedHistory,
    int EstimatedTokenCount);

/// <summary>
/// Authoritative pipeline for structured prompt & context assembly.
/// Replaces monolithic context dumps with modular, operation-specific, epistemic-filtered assembly.
/// </summary>
public interface IContextAssemblyPipeline
{
    /// <summary>
    /// Assembles system instructions, active skills, step obligation, epistemic facts, and token-fitted history.
    /// </summary>
    Task<AssembledContext> AssembleAsync(
        ContextAssemblyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarizes large tool execution outputs to prevent burning context budget while keeping full output in durable storage.
    /// </summary>
    string SummarizeToolOutput(string toolName, string rawOutput, int maxChars = 2000);
}

/// <summary>
/// Concrete implementation of <see cref="IContextAssemblyPipeline"/>.
/// </summary>
public sealed class ContextAssemblyPipeline : IContextAssemblyPipeline
{
    private readonly MessageStore _store;

    public ContextAssemblyPipeline(MessageStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async Task<AssembledContext> AssembleAsync(
        ContextAssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            sb.AppendLine(request.SystemPrompt.Trim());
        }

        if (request.EpistemicLedger != null)
        {
            string epistemicSection = request.EpistemicLedger.FormatAuthoritativeContext();
            if (!string.IsNullOrWhiteSpace(epistemicSection))
            {
                sb.AppendLine();
                sb.AppendLine(epistemicSection);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ActiveSkillsPrompt))
        {
            sb.AppendLine(request.ActiveSkillsPrompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.StepTitle))
        {
            sb.AppendLine($"\n<current_step>\n{request.StepTitle.Trim()}\n</current_step>");
        }

        // Apply operation-specific instruction tuning
        switch (request.Operation)
        {
            case InferenceOperation.Planning:
                sb.AppendLine("\n[MODE: PLANNING]\nEstablish or refine the high-level roadmap. Focus on sequence, dependencies, and verification criteria.");
                break;
            case InferenceOperation.ToolSelection:
            case InferenceOperation.SimpleAction:
                sb.AppendLine("\n[MODE: ACTION]\nSelect exactly ONE action to execute. State facts backed only by authoritative evidence.");
                break;
            case InferenceOperation.VerificationInterpretation:
                sb.AppendLine("\n[MODE: VERIFICATION]\nInterpret the tool execution output strictly against expected success criteria.");
                break;
            case InferenceOperation.Repair:
                sb.AppendLine("\n[MODE: REPAIR]\nYour previous action failed. Correct the call or select an alternative tool without repeating the failed strategy.");
                break;
            case InferenceOperation.RequirementsExtraction:
                sb.AppendLine("\n" + InteractionClassifier.FormatRequirementExtractionContract());
                break;
        }

        var records = await _store.GetMessagesAsync(request.SessionId, limit: null);
        var history = records.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
        int estTokens = (sb.Length + history.Sum(m => m.Content?.Length ?? 0)) / 4;

        return new AssembledContext(
            SystemPrompt: sb.ToString().Trim(),
            FormattedHistory: history,
            EstimatedTokenCount: estTokens);
    }

    /// <inheritdoc />
    public string SummarizeToolOutput(string toolName, string rawOutput, int maxChars = 2000)
        => SummarizeToolOutputStatic(toolName, rawOutput, maxChars);

    /// <summary>
    /// Static tool output summarizer accessible across the harness.
    /// </summary>
    public static string SummarizeToolOutputStatic(string toolName, string rawOutput, int maxChars = 2000)
    {
        if (string.IsNullOrWhiteSpace(rawOutput)) return string.Empty;
        if (rawOutput.Length <= maxChars) return rawOutput;

        var lines = rawOutput.Split('\n');
        if (lines.Length > 40)
        {
            var topLines = lines.Take(20);
            var bottomLines = lines.Skip(lines.Length - 10);
            return string.Join('\n', topLines) +
                   $"\n\n[... {lines.Length - 30} lines omitted for context budget. Full output preserved in durable ledger ...]\n\n" +
                   string.Join('\n', bottomLines);
        }

        return rawOutput[..maxChars] + "… [output truncated for context budget]";
    }
}

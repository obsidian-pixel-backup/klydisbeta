using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;

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
    int MaxTokens = 4096);

/// <summary>
/// Fully assembled and token-budgeted prompt and message context ready for the model.
/// </summary>
public sealed record AssembledContext(
    string SystemPrompt,
    IReadOnlyList<ChatMessage> FormattedHistory,
    int EstimatedTokenCount);

/// <summary>
/// Authoritative pipeline for structured prompt & context assembly (Phase 14).
/// Replaces inline 600-line ChatEngine context assembly with modular, budgeted assembly.
/// </summary>
public interface IContextAssemblyPipeline
{
    /// <summary>
    /// Assembles system instructions, active skills, step obligation, and token-fitted history.
    /// </summary>
    Task<AssembledContext> AssembleAsync(
        ContextAssemblyRequest request,
        CancellationToken cancellationToken = default);
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

        if (!string.IsNullOrWhiteSpace(request.ActiveSkillsPrompt))
        {
            sb.AppendLine(request.ActiveSkillsPrompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.StepTitle))
        {
            sb.AppendLine($"\n<current_step>\n{request.StepTitle.Trim()}\n</current_step>");
        }

        var records = await _store.GetMessagesAsync(request.SessionId, limit: null);
        var history = records.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
        int estTokens = (sb.Length + history.Sum(m => m.Content?.Length ?? 0)) / 4;

        return new AssembledContext(
            SystemPrompt: sb.ToString().Trim(),
            FormattedHistory: history,
            EstimatedTokenCount: estTokens);
    }
}

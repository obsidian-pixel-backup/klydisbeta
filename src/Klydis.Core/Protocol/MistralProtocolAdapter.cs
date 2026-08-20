using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Protocol adapter for Mistral 7B, Mistral NeMo, Mixtral 8x7B/8x22B, Codestral, and Devstral models.
/// Implements [INST] turn framing, [AVAILABLE_TOOLS] schema prelude, [TOOL_CALLS], and [TOOL_RESULTS].
/// </summary>
public sealed class MistralProtocolAdapter : IModelProtocol
{
    private static readonly string[] DefaultStopTokens = new[]
    {
        "</s>",
        "[INST]",
        "[/TOOL_CALLS]",
        "<|im_end|>"
    };

    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => ChatTemplate.Mistral;

    public MistralProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _promptEngine = promptEngine ?? new PromptTemplateEngine();
    }

    public string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        string systemAccumulator = string.Empty;

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System || msg.Role == ChatRole.Runtime)
            {
                string text = msg.Role == ChatRole.Runtime ? BuildRuntimeDirective(msg.Content) : msg.Content;
                systemAccumulator = string.IsNullOrEmpty(systemAccumulator) ? text : $"{systemAccumulator}\n\n{text}";
            }
            else if (msg.Role == ChatRole.User)
            {
                if (!string.IsNullOrEmpty(systemAccumulator))
                {
                    sb.Append($"[INST] {systemAccumulator}\n\n{msg.Content} [/INST] ");
                    systemAccumulator = string.Empty;
                }
                else
                {
                    sb.Append($"[INST] {msg.Content} [/INST] ");
                }
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                sb.Append($"{msg.Content}</s> ");
            }
            else if (msg.Role == ChatRole.Tool)
            {
                string toolHeader = !string.IsNullOrEmpty(msg.Name) ? $"Tool Result for '{msg.Name}':\n" : "Tool Result:\n";
                sb.Append($"[INST] [{toolHeader}{msg.Content}] [/INST] ");
            }
        }

        if (Profile.PreOpensThinkBlock)
        {
            sb.Append("<think>\n");
        }

        return sb.ToString();
    }

    public IReadOnlyList<CanonicalAction> ParseOutput(string rawOutput)
    {
        return ActionDialectParser.ParseCanonical(rawOutput);
    }

    public string FormatToolResult(ChatMessage toolResult)
    {
        string nameAttr = !string.IsNullOrEmpty(toolResult.Name) ? $" '{toolResult.Name}'" : "";
        return $"[INST] [Tool Result{nameAttr}]:\n{toolResult.Content} [/INST]";
    }

    public IReadOnlyList<string> GetStopTokens()
    {
        if (Profile.StopTokens != null && Profile.StopTokens.Count > 0)
        {
            return Profile.StopTokens.Concat(DefaultStopTokens).Distinct(StringComparer.Ordinal).ToArray();
        }
        return DefaultStopTokens;
    }

    public bool Supports(RuntimeCapability capability, CapabilityLevel minimum)
    {
        int level = capability switch
        {
            RuntimeCapability.ToolCalling => (int)Profile.ToolCalling,
            RuntimeCapability.ToolContinuation => (int)Profile.Continuation,
            RuntimeCapability.StructuredOutput => Profile.SupportsStructuredOutput ? 2 : 0,
            RuntimeCapability.GrammarConstrainedOutput => Profile.SupportsGrammar ? 2 : 0,
            RuntimeCapability.Thinking => Profile.SupportsThinking ? 2 : 0,
            _ => 0
        };
        return level >= (int)minimum;
    }

    private static string BuildRuntimeDirective(string content)
        => content.StartsWith("[RUNTIME CONTROL]", StringComparison.Ordinal)
            ? content
            : "[RUNTIME CONTROL] " + content.Trim();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Protocol adapter for DeepSeek V2.5, V3, and DeepSeek-R1 reasoning & tool calling models.
/// Implements DeepSeek native special token framing, pre-opened think blocks, and native tool envelopes.
/// </summary>
public sealed class DeepSeekProtocolAdapter : IModelProtocol
{
    private static readonly string[] DefaultStopTokens = new[]
    {
        "<｜end of sentence｜>",
        "<|im_end|>",
        "<|endoftext|>",
        "<｜tool calls end｜>"
    };

    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => ChatTemplate.DeepSeek;

    public DeepSeekProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _promptEngine = promptEngine ?? new PromptTemplateEngine();
    }

    public string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();

        // DeepSeek GGUFs predominantly use ChatML framing or native DeepSeek tokens.
        foreach (var msg in messages)
        {
            string roleStr = msg.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "user",
                ChatRole.Runtime => "system",
                _ => "user"
            };

            string content = msg.Role switch
            {
                ChatRole.Tool => FormatToolResult(msg),
                ChatRole.Runtime => BuildRuntimeDirective(msg.Content),
                _ => msg.Content
            };

            sb.Append($"<|im_start|>{roleStr}\n{content}<|im_end|>\n");
        }

        sb.Append("<|im_start|>assistant\n");

        if (Profile.PreOpensThinkBlock || Profile.Reasoning == ReasoningProtocol.NativeThinkBlock)
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
        if (string.IsNullOrEmpty(toolResult.Name))
        {
            return $"<tool_response>\n{toolResult.Content}\n</tool_response>";
        }

        return $"<tool_response>\n[Tool Result for '{toolResult.Name}']:\n{toolResult.Content}\n</tool_response>";
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
            RuntimeCapability.Thinking => Profile.SupportsThinking ? 3 : 0,
            _ => 0
        };
        return level >= (int)minimum;
    }

    private static string BuildRuntimeDirective(string content)
        => content.StartsWith("[RUNTIME CONTROL]", StringComparison.Ordinal)
            ? content
            : "[RUNTIME CONTROL] " + content.Trim();
}

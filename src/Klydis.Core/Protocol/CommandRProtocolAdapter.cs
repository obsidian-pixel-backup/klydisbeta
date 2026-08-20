using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Klydis.Core.Chat;
using ChatTemplate = Klydis.Core.Chat.ChatTemplate;

namespace Klydis.Core.Protocol;

/// <summary>
/// Protocol adapter for Cohere Command-R and Command-R+ models.
/// Implements START_OF_TURN/END_OF_TURN tokens, START_OF_ACTION_TOKEN, and START_OF_RESULT_TOKEN.
/// </summary>
public sealed class CommandRProtocolAdapter : IModelProtocol
{
    private static readonly string[] DefaultStopTokens = new[]
    {
        "<|END_OF_TURN_TOKEN|>",
        "<|START_OF_TURN_TOKEN|>",
        "<|END_OF_ACTION_TOKEN|>"
    };

    private readonly PromptTemplateEngine _promptEngine;

    public ModelProfile Profile { get; }
    public ChatTemplate Template => ChatTemplate.CommandR;

    public CommandRProtocolAdapter(ModelProfile profile, PromptTemplateEngine? promptEngine = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _promptEngine = promptEngine ?? new PromptTemplateEngine();
    }

    public string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();

        foreach (var msg in messages)
        {
            string roleToken = msg.Role switch
            {
                ChatRole.System => "SYSTEM",
                ChatRole.User => "USER",
                ChatRole.Assistant => "CHATBOT",
                ChatRole.Tool => "SYSTEM",
                ChatRole.Runtime => "SYSTEM",
                _ => "USER"
            };

            string content = msg.Role switch
            {
                ChatRole.Tool => FormatToolResult(msg),
                ChatRole.Runtime => BuildRuntimeDirective(msg.Content),
                _ => msg.Content
            };

            sb.Append($"<|START_OF_TURN_TOKEN|><|{roleToken}_TOKEN|>{content}<|END_OF_TURN_TOKEN|>");
        }

        sb.Append("<|START_OF_TURN_TOKEN|><|CHATBOT_TOKEN|>");

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
        string toolName = toolResult.Name ?? "tool";
        var payload = new[]
        {
            new
            {
                call = new { tool_name = toolName },
                outputs = new[] { new { output = toolResult.Content } }
            }
        };
        string json = JsonSerializer.Serialize(payload);
        return $"<|START_OF_RESULT_TOKEN|>{json}<|END_OF_RESULT_TOKEN|>";
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

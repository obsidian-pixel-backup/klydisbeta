using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fluid;
using Fluid.Values;
using Klydis.Core.Chat;

namespace Klydis.Core.Protocol;

/// <summary>
/// Dynamic chat template engine powered by Fluid (Liquid/Jinja2 compatible).
/// Compiles and evaluates embedded GGUF `tokenizer.chat_template` strings with standard variables:
/// `messages`, `tools`, `add_generation_prompt`, `eos_token`, `bos_token`.
/// </summary>
public sealed class FluidChatTemplateEngine
{
    private static readonly FluidParser _parser = new();
    private static readonly ConcurrentDictionary<string, IFluidTemplate> _templateCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Renders a Jinja2/Liquid chat template against a message history and tool definitions.
    /// </summary>
    public static string Render(
        string jinjaTemplate,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        bool addGenerationPrompt = true,
        string? eosToken = "<|im_end|>",
        string? bosToken = "")
    {
        if (string.IsNullOrWhiteSpace(jinjaTemplate))
        {
            throw new ArgumentException("Template string cannot be empty", nameof(jinjaTemplate));
        }

        // 1. Get or compile the template from cache
        var template = _templateCache.GetOrAdd(jinjaTemplate, key =>
        {
            // Normalize Hugging Face Jinja2 filters to Fluid-compatible filters if needed
            string normalized = NormalizeJinjaSyntax(key);
            if (_parser.TryParse(normalized, out var compiled, out var error))
            {
                return compiled;
            }
            throw new InvalidOperationException($"Failed to parse chat template: {error}");
        });

        // 2. Prepare Fluid TemplateContext
        var options = new TemplateOptions();
        options.MemberAccessStrategy.MemberNameStrategy = MemberNameStrategies.SnakeCase;
        var context = new TemplateContext(options);

        // Format messages array
        var messageObjects = messages.Select(m => new Dictionary<string, object?>
        {
            ["role"] = m.Role.ToString().ToLowerInvariant(),
            ["content"] = m.Content,
            ["name"] = m.Name
        }).ToList();

        context.SetValue("messages", messageObjects);

        // Format tools array
        if (tools != null && tools.Count > 0)
        {
            var toolObjects = tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = FormatToolParameters(t.Parameters)
                }
            }).ToList();

            context.SetValue("tools", toolObjects);
        }
        else
        {
            context.SetValue("tools", Array.Empty<object>());
        }

        context.SetValue("add_generation_prompt", addGenerationPrompt);
        context.SetValue("eos_token", eosToken ?? string.Empty);
        context.SetValue("bos_token", bosToken ?? string.Empty);
        context.SetValue("strftime_now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

        // 3. Render
        return template.Render(context).Trim();
    }

    /// <summary>
    /// Attempts to render the template; returns false and fallback output if parsing or rendering fails.
    /// </summary>
    public static bool TryRender(
        string jinjaTemplate,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        bool addGenerationPrompt,
        out string rendered,
        string? eosToken = "<|im_end|>",
        string? bosToken = "")
    {
        try
        {
            rendered = Render(jinjaTemplate, messages, tools, addGenerationPrompt, eosToken, bosToken);
            return true;
        }
        catch
        {
            rendered = string.Empty;
            return false;
        }
    }

    private static Dictionary<string, object> FormatToolParameters(IList<ToolParameter> parameters)
    {
        var properties = new Dictionary<string, object>();
        var requiredList = new List<string>();

        foreach (var param in parameters)
        {
            var paramDict = new Dictionary<string, object>
            {
                ["type"] = param.Type,
                ["description"] = param.Description
            };

            if (param.Enum != null && param.Enum.Length > 0)
            {
                paramDict["enum"] = param.Enum;
            }

            properties[param.Name] = paramDict;

            if (param.Required)
            {
                requiredList.Add(param.Name);
            }
        }

        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = requiredList
        };
    }

    /// <summary>
    /// Normalizes Python-specific Jinja2 syntax (e.g. `.items()`, `is defined`, `tojson`) into Fluid-compatible constructs.
    /// </summary>
    private static string NormalizeJinjaSyntax(string jinja)
    {
        if (string.IsNullOrEmpty(jinja)) return jinja;

        // Huggingface templates frequently use `tojson` filter
        string normalized = jinja.Replace("|tojson", "");
        return normalized;
    }
}

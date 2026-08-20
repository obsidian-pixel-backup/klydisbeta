using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;
using Klydis.Core.Protocol;

namespace Klydis.Core.Inference;

/// <summary>
/// Dynamic GBNF (GGML BNF) Grammar synthesizer for constrained LLM sampling.
/// Converts arbitrary C# ToolDefinitions into strictly validated GBNF grammars for JSON, Qwen XML, and Anthropic XML formats.
/// Rule names are strictly sanitized to [a-zA-Z0-9-] to prevent llama.cpp native crashes.
/// </summary>
public static class DynamicGbnfGenerator
{
    private static readonly Regex UnsafeRuleCharRegex = new(@"[^a-zA-Z0-9-]", RegexOptions.Compiled);

    /// <summary>
    /// Generates a GBNF grammar for the specified tool definitions and protocol format.
    /// </summary>
    public static string GenerateToolGrammar(
        IReadOnlyList<ToolDefinition> tools,
        ToolProtocol protocol = ToolProtocol.GenericJson)
    {
        if (tools == null || tools.Count == 0)
        {
            return "root ::= [\\s\\S]*\n";
        }

        return protocol switch
        {
            ToolProtocol.QwenNative => GenerateQwenXmlGrammar(tools),
            ToolProtocol.Antml => GenerateAnthropicXmlGrammar(tools),
            _ => GenerateJsonActionGrammar(tools)
        };
    }

    /// <summary>
    /// Generates a GBNF grammar for JSON action envelope: {"action": "tool_call", "name": "tool_name", "arguments": {...}}
    /// </summary>
    public static string GenerateJsonActionGrammar(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# GBNF Grammar for JSON Action Envelope");
        sb.AppendLine("root ::= ws ( json-action-call | free-text ) ws");
        sb.AppendLine("free-text ::= [^\\{\\<]+");
        sb.AppendLine("json-action-call ::= \"{\" ws \"\\\"action\\\"\" ws \":\" ws \"\\\"tool_call\\\"\" ws \",\" ws \"\\\"name\\\"\" ws \":\" ws tool-name-choice ws \",\" ws \"\\\"arguments\\\"\" ws \":\" ws tool-arguments-choice ws \"}\"");

        var toolNameRules = new List<string>();
        foreach (var tool in tools)
        {
            string safeName = SanitizeRuleName(tool.Name);
            toolNameRules.Add($"\"\\\"{tool.Name}\\\"\"");
        }

        sb.AppendLine($"tool-name-choice ::= {string.Join(" | ", toolNameRules)}");

        var toolArgRules = new List<string>();
        foreach (var tool in tools)
        {
            string safeName = SanitizeRuleName(tool.Name);
            string argRuleName = $"args-{safeName}";
            toolArgRules.Add(argRuleName);

            sb.AppendLine($"{argRuleName} ::= \"{{\" ws {GenerateToolParamRules(tool, sb)} ws \"}}\"");
        }

        sb.AppendLine($"tool-arguments-choice ::= {string.Join(" | ", toolArgRules)}");

        AppendJsonPrimitives(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Generates a GBNF grammar for Qwen native format: <tool_call>\n<function=tool_name>\n<parameter=key>val</parameter>\n</function>\n</tool_call>
    /// </summary>
    public static string GenerateQwenXmlGrammar(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GBNF Grammar for Qwen XML Tool Calling");
        sb.AppendLine("root ::= tool-call-block | [\\s\\S]*");
        sb.AppendLine("tool-call-block ::= \"<tool_call>\" \"\\n\" function-choice \"\\n\" \"</tool_call>\"");

        var funcChoices = new List<string>();
        foreach (var tool in tools)
        {
            string safeName = SanitizeRuleName(tool.Name);
            string ruleName = $"func-{safeName}";
            funcChoices.Add(ruleName);

            sb.AppendLine($"{ruleName} ::= \"<function=\" \"{tool.Name}\" \">\" \"\\n\" {GenerateXmlParamRules(tool)} \"</function>\"");
        }

        sb.AppendLine($"function-choice ::= {string.Join(" | ", funcChoices)}");
        sb.AppendLine("param-value ::= [^<\\n]+");
        return sb.ToString();
    }

    /// <summary>
    /// Generates a GBNF grammar for Anthropic format: <antml:invoke name="tool_name"><antml:parameter name="key">val</antml:parameter></antml:invoke>
    /// </summary>
    public static string GenerateAnthropicXmlGrammar(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GBNF Grammar for Anthropic XML Tool Calling");
        sb.AppendLine("root ::= invoke-block | [\\s\\S]*");

        var invokeChoices = new List<string>();
        foreach (var tool in tools)
        {
            string safeName = SanitizeRuleName(tool.Name);
            string ruleName = $"invoke-{safeName}";
            invokeChoices.Add(ruleName);

            sb.AppendLine($"{ruleName} ::= \"<antml:invoke name=\\\"\" \"{tool.Name}\" \"\\\">\" \"\\n\" {GenerateAntmlParamRules(tool)} \"</antml:invoke>\"");
        }

        sb.AppendLine($"invoke-block ::= {string.Join(" | ", invokeChoices)}");
        sb.AppendLine("param-value ::= [^<\\n]+");
        return sb.ToString();
    }

    private static string GenerateToolParamRules(ToolDefinition tool, StringBuilder sb)
    {
        if (tool.Parameters == null || tool.Parameters.Count == 0)
        {
            return "\"\"";
        }

        var paramRules = new List<string>();
        for (int i = 0; i < tool.Parameters.Count; i++)
        {
            var param = tool.Parameters[i];
            string safeParamName = SanitizeRuleName(param.Name);
            string ruleName = $"p-{SanitizeRuleName(tool.Name)}-{safeParamName}";

            string valueType = param.Type?.ToLowerInvariant() switch
            {
                "integer" or "int" => "json-int",
                "number" or "float" => "json-number",
                "boolean" or "bool" => "json-bool",
                "array" => "json-array",
                "object" => "json-object",
                _ => param.Enum != null && param.Enum.Length > 0
                    ? "(" + string.Join(" | ", param.Enum.Select(e => $"\"\\\"{e}\\\"\"")) + ")"
                    : "json-string"
            };

            sb.AppendLine($"{ruleName} ::= \"\\\"{param.Name}\\\"\" ws \":\" ws {valueType}");

            if (param.Required)
            {
                paramRules.Add(ruleName);
            }
            else
            {
                paramRules.Add($"({ruleName})?");
            }
        }

        return string.Join(" ( ws \",\" ws )? ", paramRules);
    }

    private static string GenerateXmlParamRules(ToolDefinition tool)
    {
        if (tool.Parameters == null || tool.Parameters.Count == 0)
        {
            return "\"\"";
        }

        var rules = tool.Parameters.Select(p => $"(\"<parameter=\" \"{p.Name}\" \">\" param-value \"</parameter>\" \"\\n\")").ToList();
        return string.Join(" ", rules) + " ";
    }

    private static string GenerateAntmlParamRules(ToolDefinition tool)
    {
        if (tool.Parameters == null || tool.Parameters.Count == 0)
        {
            return "\"\"";
        }

        var rules = tool.Parameters.Select(p => $"(\"<antml:parameter name=\\\"\" \"{p.Name}\" \"\\\">\" param-value \"</antml:parameter>\" \"\\n\")").ToList();
        return string.Join(" ", rules) + " ";
    }

    private static void AppendJsonPrimitives(StringBuilder sb)
    {
        sb.AppendLine("ws ::= [ \\t\\n\\r]*");
        sb.AppendLine("json-string ::= \"\\\"\" ([^\"\\\\\\n] | \"\\\\\" [\"\\\\/bfnrt])* \"\\\"\"");
        sb.AppendLine("json-number ::= \"-\"? [0-9]+ (\".\" [0-9]+)? ([eE] [+-]? [0-9]+)?");
        sb.AppendLine("json-int ::= \"-\"? [0-9]+");
        sb.AppendLine("json-bool ::= \"true\" | \"false\"");
        sb.AppendLine("json-array ::= \"[\" ws (json-value (ws \",\" ws json-value)*)? ws \"]\"");
        sb.AppendLine("json-object ::= \"{\" ws (json-pair (ws \",\" ws json-pair)*)? ws \"}\"");
        sb.AppendLine("json-pair ::= json-string ws \":\" ws json-value");
        sb.AppendLine("json-value ::= json-string | json-number | json-bool | json-array | json-object | \"null\"");
    }

    /// <summary>
    /// Sanitizes identifiers to strict [a-zA-Z0-9-] format. Replaces underscores and other characters with hyphens.
    /// </summary>
    public static string SanitizeRuleName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "rule";
        string sanitized = Regex.Replace(name, @"[^a-zA-Z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(sanitized) ? "rule" : sanitized.ToLowerInvariant();
    }
}

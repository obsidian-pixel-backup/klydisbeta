using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klydis.Core.Tracing;

/// <summary>
/// Redacts sensitive credentials, API keys, tokens, passwords, authorization headers,
/// and cookies from trace events, arguments, and log exports before persistence or download.
/// </summary>
public static class TraceSecretRedactor
{
    private static readonly HashSet<string> SensitiveKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd",
        "secret", "client_secret",
        "api_key", "apikey", "api-key",
        "token", "access_token", "auth_token", "refresh_token", "id_token",
        "authorization", "auth", "proxy-authorization",
        "cookie", "set-cookie",
        "private_key", "privatekey", "ssh_key",
        "credential", "credentials"
    };

    private static readonly Regex BearerTokenRegex = new(
        @"(?i)\bBearer\s+([a-zA-Z0-9_\-\.]{12,})\b",
        RegexOptions.Compiled);

    private static readonly Regex ApiKeyPatternRegex = new(
        @"\b(?:sk-[a-zA-Z0-9T3-]{16,}|ghp_[a-zA-Z0-9]{20,}|hf_[a-zA-Z0-9]{20,}|xox[baprs]-[a-zA-Z0-9\-]{20,}|key-[a-zA-Z0-9]{20,})\b",
        RegexOptions.Compiled);

    private static readonly Regex JwtPatternRegex = new(
        @"\beyJ[a-zA-Z0-9_\-]{10,}\.eyJ[a-zA-Z0-9_\-]{10,}\.[a-zA-Z0-9_\-]{10,}\b",
        RegexOptions.Compiled);

    private static readonly Regex UrlPasswordRegex = new(
        @"(?i)(https?://[^:\s/]+):([^@\s/]+)@",
        RegexOptions.Compiled);

    private static readonly Regex JsonKeyPatternRegex = new(
        @"(?i)""(password|secret|client_secret|api_key|apikey|token|access_token|auth_token|authorization|private_key)""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex AssignmentPatternRegex = new(
        @"(?i)\b(password|secret|client_secret|api_key|apikey|token|access_token|auth_token|authorization|private_key)\s*=\s*([^\s,;'""]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Redacts known secrets and credential patterns from arbitrary text strings.
    /// </summary>
    public static string RedactText(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var result = input;

        // 1. Redact Bearer tokens
        result = BearerTokenRegex.Replace(result, "Bearer [REDACTED]");

        // 2. Redact Known API Key prefixes
        result = ApiKeyPatternRegex.Replace(result, "[REDACTED_API_KEY]");

        // 3. Redact JWT patterns
        result = JwtPatternRegex.Replace(result, "[REDACTED_JWT]");

        // 4. Redact URL embedded basic auth
        result = UrlPasswordRegex.Replace(result, "$1:[REDACTED]@");

        // 5. Redact JSON key values
        result = JsonKeyPatternRegex.Replace(result, "\"$1\": \"[REDACTED]\"");

        // 6. Redact key=value pairs
        result = AssignmentPatternRegex.Replace(result, "$1=[REDACTED]");

        return result;
    }

    /// <summary>
    /// Recursively scrubs a dictionary of trace data or tool arguments, redacting sensitive keys and values.
    /// </summary>
    public static Dictionary<string, object?>? RedactDictionary(IDictionary<string, object?>? dictionary)
    {
        if (dictionary == null) return null;

        var result = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);

        foreach (var (key, value) in dictionary)
        {
            if (IsSensitiveKey(key))
            {
                result[key] = "[REDACTED]";
            }
            else
            {
                result[key] = RedactValue(value);
            }
        }

        return result;
    }

    /// <summary>
    /// Redacts dictionary with object values.
    /// </summary>
    public static Dictionary<string, object> RedactArguments(IDictionary<string, object>? args)
    {
        if (args == null) return new Dictionary<string, object>();

        var result = new Dictionary<string, object>(args.Count, StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            if (IsSensitiveKey(key))
            {
                result[key] = "[REDACTED]";
            }
            else
            {
                result[key] = RedactValue(value) ?? string.Empty;
            }
        }
        return result;
    }

    private static bool IsSensitiveKey(string key)
    {
        if (SensitiveKeyNames.Contains(key)) return true;
        foreach (var sensitive in SensitiveKeyNames)
        {
            if (key.Contains(sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static object? RedactValue(object? value)
    {
        if (value == null) return null;

        if (value is string strVal)
        {
            return RedactText(strVal);
        }

        if (value is IDictionary<string, object?> dictNullable)
        {
            return RedactDictionary(dictNullable);
        }

        if (value is IDictionary<string, object> dictObj)
        {
            return RedactArguments(dictObj);
        }

        if (value is IEnumerable<object?> listVal && !(value is string))
        {
            var newList = new List<object?>();
            foreach (var item in listVal)
            {
                newList.Add(RedactValue(item));
            }
            return newList;
        }

        if (value is JsonElement jsonElem)
        {
            return RedactJsonElement(jsonElem);
        }

        return value;
    }

    private static object? RedactJsonElement(JsonElement elem)
    {
        switch (elem.ValueKind)
        {
            case JsonValueKind.String:
                return RedactText(elem.GetString());
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in elem.EnumerateObject())
                {
                    if (IsSensitiveKey(prop.Name))
                    {
                        dict[prop.Name] = "[REDACTED]";
                    }
                    else
                    {
                        dict[prop.Name] = RedactJsonElement(prop.Value);
                    }
                }
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in elem.EnumerateArray())
                {
                    list.Add(RedactJsonElement(item));
                }
                return list;
            case JsonValueKind.Number:
                if (elem.TryGetInt64(out var l)) return l;
                if (elem.TryGetDouble(out var d)) return d;
                return elem.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            default:
                return null;
        }
    }
}

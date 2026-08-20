using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Klydis.Core.Diagnostics;

/// <summary>
/// A single structured compiler / test diagnostic extracted from tool output.
/// </summary>
public sealed record Diagnostic(
    string File,
    int? Line,
    int? Column,
    string? Code,
    string Severity,
    string Message,
    string Tool);

/// <summary>
/// Parses build / test / lint output (dotnet, tsc, cargo, pytest, go) into structured
/// <see cref="Diagnostic"/> records so compiler errors can be fed back to the agent as
/// actionable file:line:code items instead of raw log walls (blueprint TODO 091).
/// Pure and deterministic — no I/O — so it is trivially testable.
/// </summary>
public static class DiagnosticsParser
{
    public const string Dotnet = "dotnet";
    public const string TypeScript = "tsc";
    public const string Rust = "cargo";
    public const string Python = "pytest";
    public const string Go = "go";

    // dotnet build/test and tsc share the same shape: path\file.ext(12,5): error CS0103: msg
    private static readonly Regex DotnetTscRegex = new(
        @"(?<file>.+?\.(?:cs|ts|tsx|js|jsx|mjs|cjs))\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>error|warning)\s+(?<code>[A-Z]{2,}\d+):\s*(?<msg>.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // rustc/cargo: "error[E0308]: mismatched types" followed by "  --> src/main.rs:12:5"
    private static readonly Regex RustErrorRegex = new(
        @"^\s*(?<sev>error|warning)(?<code>\[E\d+\])?:\s*(?<msg>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RustLocationRegex = new(
        @"-->\s+(?<file>.+):(?<line>\d+):(?<col>\d+)",
        RegexOptions.Compiled);

    // pytest: "FAILED tests/test_x.py::test_y - AssertionError: ..." and traceback frames
    private static readonly Regex PytestFailedRegex = new(
        @"FAILED\s+(?<file>.+?)::[^\s]+\s+-\s+(?<msg>.+)",
        RegexOptions.Compiled);

    private static readonly Regex PytestTracebackRegex = new(
        @"(?<file>.+?\.py):(?<line>\d+):\s+in\s+\w+",
        RegexOptions.Compiled);

    // go build/vet: "./main.go:12:5: undefined: foo" or "./main.go:12: undefined: foo"
    private static readonly Regex GoRegex = new(
        @"(?<file>[\w./\\-]+\.go):(?<line>\d+)(?::(?<col>\d+))?:\s*(?<sev>error|warning)?\s*(?<msg>.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts structured diagnostics from tool output. Auto-detects the tool from the output
    /// unless <paramref name="toolHint"/> is supplied. Returns an empty list when the output is
    /// clean or unrecognized.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Parse(string output, string? toolHint = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<Diagnostic>();
        string tool = toolHint ?? DetectTool(output);
        return tool switch
        {
            Dotnet => ParseDotnetOrTsc(output, Dotnet),
            TypeScript => ParseDotnetOrTsc(output, TypeScript),
            Rust => ParseRust(output),
            Python => ParsePytest(output),
            Go => ParseGo(output),
            _ => Array.Empty<Diagnostic>()
        };
    }

    /// <summary>
    /// Detects the compiler/test tool from raw output via distinctive markers.
    /// </summary>
    public static string DetectTool(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        if (output.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("warning CS", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return Dotnet;
        }
        if (output.Contains("error TS", StringComparison.OrdinalIgnoreCase))
        {
            return TypeScript;
        }
        if (output.Contains("error[E", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains("error:", StringComparison.OrdinalIgnoreCase) && output.Contains("-->", StringComparison.Ordinal)))
        {
            return Rust;
        }
        if ((output.Contains("FAILED ", StringComparison.OrdinalIgnoreCase) && output.Contains("::", StringComparison.Ordinal)) ||
            (output.Contains(".py:", StringComparison.OrdinalIgnoreCase) && output.Contains(" in ", StringComparison.OrdinalIgnoreCase)))
        {
            return Python;
        }
        if (output.Contains(".go:", StringComparison.OrdinalIgnoreCase))
        {
            return Go;
        }
        return string.Empty;
    }

    /// <summary>
    /// Renders diagnostics as a compact, agent-actionable block (file:line:col + code + message),
    /// deduplicated and capped so a huge log never floods the context. Returns empty for a clean
    /// build.
    /// </summary>
    public static string FormatForContext(IReadOnlyList<Diagnostic> diagnostics, int max = 20)
    {
        if (diagnostics == null || diagnostics.Count == 0) return string.Empty;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("COMPILER DIAGNOSTICS (structured):");
        int shown = 0;
        foreach (var d in diagnostics)
        {
            string key = $"{d.File}:{d.Line}:{d.Column}:{d.Code}";
            if (!seen.Add(key)) continue;
            if (shown >= max) break;

            string loc = d.File;
            if (d.Line.HasValue)
            {
                loc += $":{d.Line}";
                if (d.Column.HasValue) loc += $":{d.Column}";
            }
            sb.AppendLine($"  - [{d.Severity}] {loc}{(d.Code != null ? $" ({d.Code})" : string.Empty)}: {d.Message}");
            shown++;
        }
        if (shown < diagnostics.Count)
        {
            sb.AppendLine($"  … and {diagnostics.Count - shown} more");
        }
        return sb.ToString();
    }

    private static IReadOnlyList<Diagnostic> ParseDotnetOrTsc(string output, string tool)
    {
        var result = new List<Diagnostic>();
        foreach (Match m in DotnetTscRegex.Matches(output))
        {
            result.Add(new Diagnostic(
                m.Groups["file"].Value.Trim(),
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["code"].Value,
                m.Groups["sev"].Value.ToLowerInvariant(),
                m.Groups["msg"].Value.Trim(),
                tool));
        }
        return result;
    }

    private static IReadOnlyList<Diagnostic> ParseRust(string output)
    {
        var result = new List<Diagnostic>();
        string? currentMessage = null;
        string currentSeverity = "error";
        string? currentCode = null;
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            var err = RustErrorRegex.Match(line);
            if (err.Success)
            {
                currentMessage = err.Groups["msg"].Value.Trim();
                currentSeverity = err.Groups["sev"].Value.ToLowerInvariant();
                currentCode = err.Groups["code"].Success ? err.Groups["code"].Value : null;
                continue;
            }
            var loc = RustLocationRegex.Match(line);
            if (loc.Success)
            {
                result.Add(new Diagnostic(
                    loc.Groups["file"].Value.Trim(),
                    int.Parse(loc.Groups["line"].Value),
                    int.Parse(loc.Groups["col"].Value),
                    currentCode,
                    currentSeverity,
                    currentMessage ?? string.Empty,
                    Rust));
            }
        }
        return result;
    }

    private static IReadOnlyList<Diagnostic> ParsePytest(string output)
    {
        var result = new List<Diagnostic>();
        foreach (Match m in PytestFailedRegex.Matches(output))
        {
            result.Add(new Diagnostic(
                m.Groups["file"].Value.Trim(),
                null, null, null, "error",
                m.Groups["msg"].Value.Trim(),
                Python));
        }
        foreach (Match m in PytestTracebackRegex.Matches(output))
        {
            result.Add(new Diagnostic(
                m.Groups["file"].Value.Trim(),
                int.Parse(m.Groups["line"].Value),
                null, null, "error",
                "failure in test",
                Python));
        }
        return result;
    }

    private static IReadOnlyList<Diagnostic> ParseGo(string output)
    {
        var result = new List<Diagnostic>();
        foreach (Match m in GoRegex.Matches(output))
        {
            result.Add(new Diagnostic(
                m.Groups["file"].Value.Trim(),
                int.Parse(m.Groups["line"].Value),
                m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : (int?)null,
                null,
                m.Groups["sev"].Success ? m.Groups["sev"].Value.ToLowerInvariant() : "error",
                m.Groups["msg"].Value.Trim(),
                Go));
        }
        return result;
    }
}

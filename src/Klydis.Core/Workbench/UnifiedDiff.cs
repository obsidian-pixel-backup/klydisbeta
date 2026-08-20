using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Klydis.Core.Workbench;

/// <summary>
/// A parsed hunk from a unified diff (diff -u format).
/// <see cref="OldLines"/> is the sequence of context + removed lines (what must exist in the
/// file to apply the hunk), tagged by <see cref="OldIsRemoved"/>; <see cref="NewLines"/> is the
/// sequence of context + added lines (what replaces it), tagged by <see cref="NewIsAdded"/>.
/// </summary>
public sealed record UnifiedDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<string> OldLines,
    IReadOnlyList<string> NewLines,
    IReadOnlyList<bool> OldIsRemoved,
    IReadOnlyList<bool> NewIsAdded);

/// <summary>
/// Pure unified-diff (diff -u / unidiff) parsing and application (blueprint TODO 082).
/// Applies hunks in order with trailing-whitespace / CRLF tolerance and a line-number hint to
/// disambiguate repeated blocks. Context lines are taken from the FILE (so existing line endings
/// and trailing whitespace are preserved); only removed lines are dropped and added lines
/// inserted. No I/O — fully unit-testable.
/// </summary>
public static class UnifiedDiff
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a unified diff into its hunks. Returns null (with <paramref name="error"/>) when
    /// the diff is empty, malformed, or contains no hunks.
    /// </summary>
    public static IReadOnlyList<UnifiedDiffHunk>? ParseHunks(string diff, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(diff))
        {
            error = "Diff is empty.";
            return null;
        }

        string[] lines = diff.Replace("\r\n", "\n").Split('\n');
        var hunks = new List<UnifiedDiffHunk>();
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            var header = ParseHunkHeader(line, out var headerError);
            if (header == null)
            {
                error = headerError;
                return null;
            }

            i++;
            var oldLines = new List<string>();
            var newLines = new List<string>();
            var oldIsRemoved = new List<bool>();
            var newIsAdded = new List<bool>();
            while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                string l = lines[i];
                if (l.Length == 0)
                {
                    i++;
                    continue;
                }
                char prefix = l[0];
                string content = l.Length > 1 ? l.Substring(1) : string.Empty;
                switch (prefix)
                {
                    case ' ':
                        oldLines.Add(content);
                        newLines.Add(content);
                        oldIsRemoved.Add(false);
                        newIsAdded.Add(false);
                        break;
                    case '-':
                        oldLines.Add(content);
                        oldIsRemoved.Add(true);
                        break;
                    case '+':
                        newLines.Add(content);
                        newIsAdded.Add(true);
                        break;
                    case '\\':
                        break; // "\ No newline at end of file" marker
                    default:
                        break;
                }
                i++;
            }

            hunks.Add(new UnifiedDiffHunk(
                header.Value.OldStart, header.Value.OldCount,
                header.Value.NewStart, header.Value.NewCount,
                oldLines, newLines, oldIsRemoved, newIsAdded));
        }

        if (hunks.Count == 0)
        {
            error = "No hunks found in the diff.";
            return null;
        }
        return hunks;
    }

    /// <summary>
    /// Applies a unified diff to the original file content. Returns the patched content, or null
    /// (with <paramref name="error"/>) when the diff is malformed or a hunk does not match the
    /// file. Context lines are preserved from the file (line endings / trailing whitespace kept);
    /// removed lines are dropped and added lines inserted.
    /// </summary>
    public static string? Apply(string original, string diff, out string? error)
    {
        error = null;
        var hunks = ParseHunks(diff, out var parseError);
        if (hunks == null)
        {
            error = parseError;
            return null;
        }

        string[] fileLines = original.Split('\n');
        var result = new List<string>(fileLines);
        int offset = 0; // cumulative new-count - old-count of applied hunks

        foreach (var hunk in hunks)
        {
            int preferred = Math.Max(0, hunk.OldStart - 1 + offset);

            if (hunk.OldLines.Count == 0)
            {
                // Pure addition: insert the new lines at the hunk's position.
                int insertAt = Math.Clamp(hunk.OldStart - 1 + offset, 0, result.Count);
                result.InsertRange(insertAt, hunk.NewLines);
                offset += hunk.NewLines.Count;
                continue;
            }

            int found = FindBlock(result, hunk.OldLines, preferred);
            if (found < 0)
            {
                error = $"Hunk near original line {hunk.OldStart} was not found in the file (even with trailing-whitespace/line-ending tolerance).";
                return null;
            }

            // Build the replacement from the FILE's context lines (preserving their line endings
            // and trailing whitespace), dropping removed lines and inserting added lines.
            var replacement = new List<string>(hunk.NewLines.Count);
            int i = 0; // index into OldLines (advances past removed lines to the next context)
            for (int j = 0; j < hunk.NewLines.Count; j++)
            {
                if (hunk.NewIsAdded[j])
                {
                    replacement.Add(hunk.NewLines[j]);
                }
                else
                {
                    while (i < hunk.OldLines.Count && hunk.OldIsRemoved[i]) i++;
                    replacement.Add(result[found + i]);
                    i++;
                }
            }

            result.RemoveRange(found, hunk.OldLines.Count);
            result.InsertRange(found, replacement);
            offset += replacement.Count - hunk.OldLines.Count;
        }

        return string.Join("\n", result);
    }

    private static (int OldStart, int OldCount, int NewStart, int NewCount)? ParseHunkHeader(string line, out string? error)
    {
        error = null;
        var m = HunkHeaderRegex.Match(line);
        if (!m.Success)
        {
            error = $"Malformed hunk header: {line}";
            return null;
        }
        int oldStart = int.Parse(m.Groups[1].Value);
        int oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
        int newStart = int.Parse(m.Groups[3].Value);
        int newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
        return (oldStart, oldCount, newStart, newCount);
    }

    /// <summary>
    /// Locates the old block (context + removed lines) in the file. Comparison is
    /// trailing-whitespace / CRLF tolerant. When the block appears more than once, prefers the
    /// occurrence nearest the hunk's line-number hint.
    /// </summary>
    private static int FindBlock(List<string> lines, IReadOnlyList<string> block, int preferred)
    {
        if (block.Count == 0) return -1;
        var normBlock = block.Select(NormalizeForCompare).ToArray();
        int maxStart = lines.Count - block.Count;
        if (maxStart < 0) return -1;

        var matches = new List<int>();
        for (int i = 0; i <= maxStart; i++)
        {
            bool ok = true;
            for (int j = 0; j < block.Count; j++)
            {
                if (NormalizeForCompare(lines[i + j]) != normBlock[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok) matches.Add(i);
        }

        if (matches.Count == 0) return -1;
        return matches.OrderBy(m => Math.Abs(m - preferred)).First();
    }

    private static string NormalizeForCompare(string line)
        => line.TrimEnd('\r', ' ', '\t');
}

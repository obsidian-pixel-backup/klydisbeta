using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Klydis.Core.Workbench;

/// <summary>
/// Line-based diff over two texts, producing a compact unified-style diff plus added/deleted
/// line counts. Used by the change-capture path around file-mutating tools so the Changes tab
/// shows REAL filesystem diffs (workbench spec §7–§8). Pure, deterministic, testable.
/// </summary>
public static class DiffService
{
    /// <summary>Computes the diff of <paramref name="after"/> relative to <paramref name="before"/>.</summary>
    public static DiffResult Diff(string? before, string after)
    {
        string[] beforeLines = SplitLines(before);
        string[] afterLines = SplitLines(after);

        // LCS table. Bounded by the sizes involved — a single write_file diff is typically
        // small; pathological inputs (e.g. diffing against a 100k-line file) are capped below.
        int n = beforeLines.Length;
        int m = afterLines.Length;
        const int MaxLcsCells = 2_500_000; // ~2500x1000 — beyond that, fall back to whole-file replace
        if ((long)n * m > MaxLcsCells)
        {
            return WholeFileDiff(beforeLines, afterLines);
        }

        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(beforeLines[i], afterLines[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var sb = new StringBuilder();
        int added = 0;
        int deleted = 0;
        int i0 = 0;
        int j0 = 0;
        while (i0 < n && j0 < m)
        {
            if (string.Equals(beforeLines[i0], afterLines[j0], StringComparison.Ordinal))
            {
                i0++;
                j0++;
            }
            else if (lcs[i0 + 1, j0] >= lcs[i0, j0 + 1])
            {
                sb.Append('-').AppendLine(beforeLines[i0]);
                deleted++;
                i0++;
            }
            else
            {
                sb.Append('+').AppendLine(afterLines[j0]);
                added++;
                j0++;
            }
        }
        while (i0 < n)
        {
            sb.Append('-').AppendLine(beforeLines[i0]);
            deleted++;
            i0++;
        }
        while (j0 < m)
        {
            sb.Append('+').AppendLine(afterLines[j0]);
            added++;
            j0++;
        }

        return new DiffResult(sb.ToString(), added, deleted);
    }

    private static DiffResult WholeFileDiff(string[] beforeLines, string[] afterLines)
    {
        var sb = new StringBuilder();
        foreach (var line in beforeLines)
        {
            sb.Append('-').AppendLine(line);
        }
        foreach (var line in afterLines)
        {
            sb.Append('+').AppendLine(line);
        }
        return new DiffResult(sb.ToString(), afterLines.Length, beforeLines.Length);
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        // Split on \r\n and \n.
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Split('\n')
                   .ToArray();
    }
}

/// <summary>The unified-style diff text plus added/deleted line counts.</summary>
public sealed record DiffResult(string Text, int AddedLines, int DeletedLines);

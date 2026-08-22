using System.Text;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Extracts HTML tables into structured <see cref="WebTable"/> models and formats them as standard Markdown.
/// </summary>
public static class TableExtractor
{
    public static IReadOnlyList<WebTable> Extract(HtmlDocument doc, int maxTables = 10)
    {
        var tables = new List<WebTable>();
        var tableNodes = doc.DocumentNode.SelectNodes("//table");
        if (tableNodes == null) return tables;

        foreach (var tableNode in tableNodes.Take(maxTables))
        {
            var caption = tableNode.SelectSingleNode(".//caption")?.InnerText?.Trim();
            caption = !string.IsNullOrEmpty(caption) ? HtmlEntity.DeEntitize(caption) : null;

            var rows = new List<List<string>>();
            var headerCols = new List<string>();

            // 1. Look for <th> headers
            var thNodes = tableNode.SelectNodes(".//tr/th");
            if (thNodes != null && thNodes.Count > 0)
            {
                foreach (var th in thNodes)
                {
                    headerCols.Add(CleanCell(th.InnerText));
                }
            }

            // 2. Look for rows
            var trNodes = tableNode.SelectNodes(".//tr");
            if (trNodes != null)
            {
                foreach (var tr in trNodes)
                {
                    var tdNodes = tr.SelectNodes("./td");
                    if (tdNodes == null || tdNodes.Count == 0)
                    {
                        // Check if this row only had th elements and we already parsed them
                        continue;
                    }

                    var rowCells = tdNodes.Select(td => CleanCell(td.InnerText)).ToList();
                    rows.Add(rowCells);
                }
            }

            // If header was missing, synthesize from first row if rows exist
            if (headerCols.Count == 0 && rows.Count > 0)
            {
                headerCols = Enumerable.Range(1, rows[0].Count).Select(i => $"Col {i}").ToList();
            }

            if (headerCols.Count > 0 || rows.Count > 0)
            {
                tables.Add(new WebTable(caption, headerCols, rows));
            }
        }

        return tables;
    }

    public static string FormatAsMarkdown(WebTable table)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(table.Caption))
        {
            sb.AppendLine($"**Table: {table.Caption}**\n");
        }

        if (table.Columns.Count == 0 && table.Rows.Count == 0) return string.Empty;

        int colCount = Math.Max(table.Columns.Count, table.Rows.Count > 0 ? table.Rows.Max(r => r.Count) : 0);
        var cols = table.Columns.Count > 0 ? table.Columns : Enumerable.Range(1, colCount).Select(i => $"Col {i}").ToList();

        sb.Append("| ");
        for (int i = 0; i < colCount; i++)
        {
            var colName = i < cols.Count ? cols[i] : $"Col {i + 1}";
            sb.Append(colName.Replace("|", "\\|")).Append(" | ");
        }
        sb.AppendLine();

        sb.Append("| ");
        for (int i = 0; i < colCount; i++)
        {
            sb.Append("--- | ");
        }
        sb.AppendLine();

        foreach (var row in table.Rows)
        {
            sb.Append("| ");
            for (int i = 0; i < colCount; i++)
            {
                var cell = i < row.Count ? row[i] : "";
                sb.Append(cell.Replace("|", "\\|")).Append(" | ");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string CleanCell(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = HtmlEntity.DeEntitize(text).Trim();
        return System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ");
    }
}

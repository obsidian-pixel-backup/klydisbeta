namespace Klydis.Core.Web.Models;

/// <summary>
/// A structured tabular data model extracted from HTML table elements.
/// </summary>
public sealed record WebTable(
    string? Caption,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows);

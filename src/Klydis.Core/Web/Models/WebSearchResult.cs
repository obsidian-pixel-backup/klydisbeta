namespace Klydis.Core.Web.Models;

/// <summary>
/// One search hit. Search returns IDs, titles, URLs and snippets — not giant prose — so the
/// agent can pick a result and open it without re-parsing a blob.
/// </summary>
public sealed record WebSearchResult(
    string Id,
    string Title,
    string Url,
    string Snippet,
    string Domain,
    int Rank);

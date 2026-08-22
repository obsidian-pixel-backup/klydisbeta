using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Strategy contract for external search engine providers.
/// </summary>
public interface ISearchProvider
{
    string Name { get; }
    int Priority { get; }
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct);
}

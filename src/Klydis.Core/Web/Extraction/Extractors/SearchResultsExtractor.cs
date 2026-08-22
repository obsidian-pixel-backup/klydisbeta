using System.Text;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction.Extractors;

/// <summary>
/// Specialized extractor for search engine results pages.
/// </summary>
public sealed class SearchResultsExtractor : IPageExtractor
{
    private readonly GenericExtractor _generic = new();

    public PageType SupportedType => PageType.SearchResults;

    public ExtractedPage Extract(string url, string html, int maxChars)
    {
        return _generic.Extract(url, html, maxChars);
    }
}

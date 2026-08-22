using System.Collections.Concurrent;
using Klydis.Core.Web.Extraction.Extractors;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Registry resolving specialized <see cref="IPageExtractor"/> strategies for classified page types.
/// </summary>
public sealed class ExtractorRegistry
{
    private readonly ConcurrentDictionary<PageType, IPageExtractor> _extractors = new();
    private readonly GenericExtractor _genericExtractor = new();

    public static readonly ExtractorRegistry Default = CreateDefaultRegistry();

    public ExtractorRegistry()
    {
        Register(_genericExtractor);
    }

    public void Register(IPageExtractor extractor)
    {
        _extractors[extractor.SupportedType] = extractor;
    }

    public IPageExtractor Resolve(PageType pageType)
    {
        if (_extractors.TryGetValue(pageType, out var extractor))
        {
            return extractor;
        }
        return _genericExtractor;
    }

    public static ExtractorRegistry CreateDefaultRegistry()
    {
        var registry = new ExtractorRegistry();
        registry.Register(new ArticleExtractor());
        registry.Register(new DocumentationExtractor());
        registry.Register(new GitHubExtractor());
        registry.Register(new WikipediaExtractor());
        registry.Register(new SearchResultsExtractor());
        return registry;
    }
}

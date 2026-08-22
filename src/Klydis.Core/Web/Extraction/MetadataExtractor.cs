using System.Globalization;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Extracts semantic metadata, OpenGraph attributes, and JSON-LD schema definitions from HTML.
/// </summary>
public static class MetadataExtractor
{
    public static WebMetadata Extract(HtmlDocument doc, string? requestedUrl = null)
    {
        var root = doc.DocumentNode;

        var title = root.SelectSingleNode("//title")?.InnerText?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = root.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "")?.Trim();
        }

        var description = root.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "")?.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            description = root.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", "")?.Trim();
        }

        var canonical = root.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", "")?.Trim();
        var author = root.SelectSingleNode("//meta[@name='author']")?.GetAttributeValue("content", "")?.Trim();
        var siteName = root.SelectSingleNode("//meta[@property='og:site_name']")?.GetAttributeValue("content", "")?.Trim();
        var language = root.SelectSingleNode("//html")?.GetAttributeValue("lang", "")?.Trim();

        // Published / Modified Dates
        DateTimeOffset? publishedAt = null;
        DateTimeOffset? modifiedAt = null;

        var pubString = root.SelectSingleNode("//meta[@property='article:published_time']")?.GetAttributeValue("content", "")?.Trim();
        if (!string.IsNullOrEmpty(pubString) && DateTimeOffset.TryParse(pubString, CultureInfo.InvariantCulture, out var pubDate))
        {
            publishedAt = pubDate;
        }

        var modString = root.SelectSingleNode("//meta[@property='article:modified_time']")?.GetAttributeValue("content", "")?.Trim();
        if (!string.IsNullOrEmpty(modString) && DateTimeOffset.TryParse(modString, CultureInfo.InvariantCulture, out var modDate))
        {
            modifiedAt = modDate;
        }

        // OpenGraph Properties
        var ogDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ogNodes = root.SelectNodes("//meta[starts-with(@property, 'og:')]");
        if (ogNodes != null)
        {
            foreach (var node in ogNodes)
            {
                var prop = node.GetAttributeValue("property", "");
                var content = node.GetAttributeValue("content", "");
                if (!string.IsNullOrEmpty(prop) && !string.IsNullOrEmpty(content) && !ogDict.ContainsKey(prop))
                {
                    ogDict[prop] = content.Trim();
                }
            }
        }

        // JSON-LD
        string? jsonLd = null;
        var jsonLdNode = root.SelectSingleNode("//script[@type='application/ld+json']");
        if (jsonLdNode != null && !string.IsNullOrWhiteSpace(jsonLdNode.InnerText))
        {
            jsonLd = jsonLdNode.InnerText.Trim();
        }

        return new WebMetadata(
            Title: string.IsNullOrWhiteSpace(title) ? null : HtmlEntity.DeEntitize(title),
            Description: string.IsNullOrWhiteSpace(description) ? null : HtmlEntity.DeEntitize(description),
            CanonicalUrl: string.IsNullOrWhiteSpace(canonical) ? requestedUrl : canonical,
            Author: string.IsNullOrWhiteSpace(author) ? null : HtmlEntity.DeEntitize(author),
            PublishedAt: publishedAt,
            ModifiedAt: modifiedAt,
            SiteName: string.IsNullOrWhiteSpace(siteName) ? null : HtmlEntity.DeEntitize(siteName),
            Language: string.IsNullOrWhiteSpace(language) ? null : language,
            ContentType: "text/html",
            WordCount: null,
            JsonLd: jsonLd,
            OpenGraph: ogDict.Count > 0 ? ogDict : null);
    }
}

using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Extracts and normalizes structured hyperlinks from an HTML document.
/// </summary>
public static class LinkExtractor
{
    public static IReadOnlyList<WebLink> Extract(HtmlDocument doc, string baseUrl, int maxLinks = 100)
    {
        var links = new List<WebLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var anchorNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchorNodes == null) return links;

        Uri? baseUri = null;
        if (!string.IsNullOrEmpty(baseUrl))
        {
            Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);
        }

        foreach (var node in anchorNodes)
        {
            var rawHref = node.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrWhiteSpace(rawHref) ||
                rawHref.StartsWith("#") ||
                rawHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                rawHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                rawHref.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string absoluteUrl = rawHref;
            bool isExternal = false;

            if (Uri.TryCreate(rawHref, UriKind.Absolute, out var absUri))
            {
                absoluteUrl = absUri.ToString();
                if (baseUri != null && !string.Equals(absUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    isExternal = true;
                }
            }
            else if (baseUri != null && Uri.TryCreate(baseUri, rawHref, out var combinedUri))
            {
                absoluteUrl = combinedUri.ToString();
            }
            else
            {
                continue;
            }

            if (!seenUrls.Add(absoluteUrl))
            {
                continue;
            }

            var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                var titleAttr = node.GetAttributeValue("title", "").Trim();
                text = !string.IsNullOrEmpty(titleAttr) ? titleAttr : absoluteUrl;
            }

            links.Add(new WebLink(text, absoluteUrl, null, isExternal));
            if (links.Count >= maxLinks) break;
        }

        return links;
    }
}

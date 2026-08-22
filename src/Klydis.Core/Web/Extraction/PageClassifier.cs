using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Deterministic multi-signal page classifier. Inspects URL domain/path, meta tags,
/// schema.org JSON-LD definitions, and DOM structure.
/// </summary>
public sealed class PageClassifier : IPageClassifier
{
    public PageClassification Classify(string url, string html, string? contentType = null)
    {
        var signals = new List<string>();

        // 1. Bot Challenge Check
        if (BotChallengeDetector.IsBotChallenge(html, out var challengeSignal))
        {
            signals.Add($"bot_challenge:{challengeSignal}");
            return new PageClassification(PageType.BotChallenge, 0.95, signals);
        }

        // 2. URL Domain & Path Patterns
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (host.Contains("wikipedia.org"))
            {
                signals.Add("domain:wikipedia");
                return new PageClassification(PageType.Wikipedia, 0.98, signals);
            }

            if (host.Contains("github.com"))
            {
                signals.Add("domain:github");
                return new PageClassification(PageType.GitHub, 0.98, signals);
            }

            if (host.Contains("reddit.com") || host.Contains("stackoverflow.com") || host.Contains("stackexchange.com") || host.Contains("discourse."))
            {
                signals.Add("domain:forum");
                return new PageClassification(PageType.Forum, 0.92, signals);
            }

            if (host.StartsWith("docs.") || path.StartsWith("/docs") || path.StartsWith("/documentation") ||
                path.StartsWith("/api") || host.Contains("readthedocs.io") || host.Contains("learn.microsoft.com"))
            {
                signals.Add("url_pattern:documentation");
                return new PageClassification(PageType.Documentation, 0.90, signals);
            }

            if (host.Contains("bing.com") && path.Contains("search") ||
                host.Contains("duckduckgo.com") ||
                host.Contains("google.com") && path.Contains("search"))
            {
                signals.Add("domain:search_results");
                return new PageClassification(PageType.SearchResults, 0.95, signals);
            }
        }

        // 3. HTML DOM / Meta / Schema Inspection
        if (!string.IsNullOrWhiteSpace(html))
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Check Error Page signals
                var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? "";
                if (title.Contains("404") || title.Contains("Not Found", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("500 Internal Server Error", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Access Denied", StringComparison.OrdinalIgnoreCase))
                {
                    signals.Add("title:error_page");
                    return new PageClassification(PageType.ErrorPage, 0.85, signals);
                }

                // Check OpenGraph / Schema.org
                var ogType = doc.DocumentNode.SelectSingleNode("//meta[@property='og:type']")?.GetAttributeValue("content", "")?.ToLowerInvariant();
                if (ogType == "article")
                {
                    signals.Add("og_type:article");
                    return new PageClassification(PageType.Article, 0.88, signals);
                }

                var jsonLdNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
                if (jsonLdNodes != null)
                {
                    foreach (var node in jsonLdNodes)
                    {
                        var json = node.InnerText;
                        if (json.Contains("\"Article\"") || json.Contains("\"NewsArticle\""))
                        {
                            signals.Add("json_ld:article");
                            return new PageClassification(PageType.Article, 0.90, signals);
                        }
                        if (json.Contains("\"Product\""))
                        {
                            signals.Add("json_ld:product");
                            return new PageClassification(PageType.Product, 0.90, signals);
                        }
                        if (json.Contains("\"TechArticle\""))
                        {
                            signals.Add("json_ld:tech_article");
                            return new PageClassification(PageType.Documentation, 0.90, signals);
                        }
                    }
                }

                // Check DOM structures
                var articleNode = doc.DocumentNode.SelectSingleNode("//article");
                if (articleNode != null)
                {
                    signals.Add("dom:article_tag");
                    return new PageClassification(PageType.Article, 0.80, signals);
                }

                var tables = doc.DocumentNode.SelectNodes("//table");
                if (tables != null && tables.Count >= 2)
                {
                    signals.Add("dom:multiple_tables");
                    return new PageClassification(PageType.Table, 0.75, signals);
                }
            }
            catch
            {
                // Fall through to Generic
            }
        }

        signals.Add("fallback:generic");
        return new PageClassification(PageType.Generic, 0.50, signals);
    }
}

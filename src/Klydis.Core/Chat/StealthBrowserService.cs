using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Web.Browser;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Klydis.Core.Chat;

/// <summary>
/// Result of a structured browser fetch. The web subsystem gets classified outcomes, not
/// exceptions, so the fetch router and the agent can reason about what failed.
/// </summary>
public sealed record BrowserFetchDocument(
    string? Title,
    string? FinalUrl,
    string Markdown,
    int? HttpStatus,
    bool ContentWasTruncated,
    WebFailure? Failure,
    string? RawHtml = null);

/// <summary>
/// Service managing stealth browser execution, context isolation, request interception,
/// anti-detection spoofing, and resilient web crawling.
/// </summary>
public class StealthBrowserService : IAsyncDisposable
{
    private readonly ILogger<StealthBrowserService> _logger;
    private readonly CamoufoxManager _camoufoxManager;
    private readonly SsrfGuard _ssrfGuard;
    private readonly BrowserPool _browserPool;
    private bool _isDisposed;

    public BrowserPool Pool => _browserPool;

    public StealthBrowserService(
        ILogger<StealthBrowserService> logger,
        CamoufoxManager camoufoxManager,
        SsrfGuard? ssrfGuard = null,
        BrowserPool? browserPool = null)
    {
        _logger = logger;
        _camoufoxManager = camoufoxManager;
        _ssrfGuard = ssrfGuard ?? new SsrfGuard(logger);
        _browserPool = browserPool ?? new BrowserPool(camoufoxManager, maxConcurrentCrawls: 4, logger: logger);
    }

    /// <summary>
    /// Structured browser fetch: SSRF policy is enforced BEFORE navigation, request interception
    /// inspects every sub-resource, final URL is revalidated after navigation, and accurate HTTP status
    /// is captured from the main resource response.
    /// </summary>
    public async Task<BrowserFetchDocument> FetchDocumentAsync(string url, int maxChars, CancellationToken ct = default)
    {
        // 1. Initial URL Policy Gate
        var policy = await _ssrfGuard.ValidateAsync(url, ct).ConfigureAwait(false);
        if (policy != null)
        {
            _logger.LogWarning("Browser navigation blocked by policy: {Message}", policy.Message);
            return new BrowserFetchDocument(null, null, string.Empty, null, false, policy with { Stage = "browser" });
        }

        try
        {
            await using var session = await _browserPool.CreateSessionAsync(_ssrfGuard, BrowserResourceBudget.Default, ct).ConfigureAwait(false);
            var page = session.Page;

            IResponse? response = null;
            try
            {
                response = await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 25000
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _browserPool.HealthMonitor.RecordNavigationFailure(ex);
                return new BrowserFetchDocument(null, null, string.Empty, null, false, ClassifyNavigationFailure(ex));
            }

            _browserPool.HealthMonitor.RecordNavigationSuccess();
            int? httpStatus = response?.Status;

            string? finalUrl = null;
            try { finalUrl = page.Url; } catch { }

            // 2. Revalidate final redirected URL before accepting document
            if (!string.IsNullOrEmpty(finalUrl) && !string.Equals(finalUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                var finalPolicy = await _ssrfGuard.ValidateAsync(finalUrl, ct).ConfigureAwait(false);
                if (finalPolicy != null)
                {
                    _logger.LogWarning("Browser redirected to blocked destination {FinalUrl}: {Message}", finalUrl, finalPolicy.Message);
                    return new BrowserFetchDocument(null, finalUrl, string.Empty, httpStatus, false,
                        finalPolicy with { Stage = "browser", Message = $"Browser navigated to blocked destination: {finalPolicy.Message}" });
                }
            }

            // If main response returned HTTP 4xx or 5xx error, report classified failure
            if (httpStatus.HasValue && httpStatus.Value >= 400)
            {
                return new BrowserFetchDocument(null, finalUrl, string.Empty, httpStatus, false,
                    WebFailure.FromHttpStatus(httpStatus.Value) with { Stage = "browser" });
            }

            // 3. Content-candidate wait: poll for meaningful content and hydration
            await WaitForContentCandidateAsync(page, ct).ConfigureAwait(false);

            string title = string.Empty;
            try { title = await page.TitleAsync().ConfigureAwait(false); } catch { }

            string? rawHtml = null;
            try { rawHtml = await page.ContentAsync().ConfigureAwait(false); } catch { }

            string markdown;
            try
            {
                markdown = await ExtractMarkdownAsync(page).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Browser content extraction failed for {Url}", url);
                return new BrowserFetchDocument(null, finalUrl, string.Empty, httpStatus, false,
                    new WebFailure(WebFailureCode.ExtractionFailure, false, false,
                        $"Browser content extraction failed: {ex.Message}", Stage: "browser"));
            }

            if (string.IsNullOrWhiteSpace(markdown))
            {
                return new BrowserFetchDocument(null, finalUrl, string.Empty, httpStatus, false,
                    new WebFailure(WebFailureCode.EmptyContent, false, false,
                        "The browser loaded the page but no meaningful content was found.", Stage: "browser"));
            }

            bool truncated = markdown.Length > maxChars;
            if (truncated)
            {
                markdown = markdown[..maxChars] + "\n\n… [truncated]";
            }

            return new BrowserFetchDocument(title, finalUrl, markdown, httpStatus ?? 200, truncated, null, rawHtml);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stealth browser session could not be completed for {Url}", url);
            return new BrowserFetchDocument(null, null, string.Empty, null, false,
                new WebFailure(WebFailureCode.BrowserUnavailable, false, false,
                    $"Browser session failed: {ex.Message}", Stage: "browser"));
        }
    }

    /// <summary>
    /// Executes a web crawl on the target URL with anti-detection, stealth patching, and
    /// Markdown conversion. Compatibility wrapper over <see cref="FetchDocumentAsync"/>.
    /// </summary>
    public async Task<string> CrawlUrlAsync(string url, CancellationToken ct = default)
    {
        var result = await FetchDocumentAsync(url, 20000, ct).ConfigureAwait(false);
        if (result.Failure != null)
        {
            return $"WEB_FAILED\ncode={result.Failure.Tag}\nmessage: {result.Failure.Message}";
        }

        var header = $"# Page Title: {result.Title ?? "(none)"}\nSource URL: {url}\n\n---\n\n";
        return header + result.Markdown;
    }

    /// <summary>
    /// Executes a search query using stealth browser rendering for engines like Bing.
    /// </summary>
    public async Task<string?> RenderPageHtmlAsync(string url, CancellationToken ct = default)
    {
        var policy = await _ssrfGuard.ValidateAsync(url, ct).ConfigureAwait(false);
        if (policy != null)
        {
            _logger.LogWarning("Browser render blocked by policy: {Message}", policy.Message);
            return null;
        }

        try
        {
            await using var session = await _browserPool.CreateSessionAsync(_ssrfGuard, BrowserResourceBudget.Default, ct).ConfigureAwait(false);
            var page = session.Page;

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000
            }).ConfigureAwait(false);

            await Task.Delay(800, ct).ConfigureAwait(false);
            return await page.ContentAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stealth browser page HTML render failed for URL: {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Waits for content candidate elements (main, article, body text) and checks DOM mutation stability.
    /// </summary>
    private static async Task WaitForContentCandidateAsync(IPage page, CancellationToken ct)
    {
        for (int i = 0; i < 8; i++)
        {
            bool hasContent;
            try
            {
                hasContent = await page.EvaluateAsync<bool>(@"() => {
                    const main = document.querySelector('main, article, [role=""main""]');
                    if (main && main.innerText.trim().length > 100) return true;
                    const body = document.body;
                    return body !== null && body.innerText.trim().length > 200;
                }").ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (hasContent) return;
            await Task.Delay(350, ct).ConfigureAwait(false);
        }

        await Task.Delay(400, ct).ConfigureAwait(false);
    }

    private static async Task<string> ExtractMarkdownAsync(IPage page)
    {
        // Purge DOM noise elements
        await page.EvaluateAsync(@"() => {
            const noisySelectors = [
                'nav', 'footer', 'header', 'aside',
                '[role=""navigation""]', '[role=""banner""]',
                '.cookie-banner', '.ad-container', 'iframe',
                '#cookie-notice', '#gdpr-banner', '.social-share'
            ];
            noisySelectors.forEach(s => document.querySelectorAll(s).forEach(el => el.remove()));
        }").ConfigureAwait(false);

        string html;
        var mainHandle = await page.QuerySelectorAsync("main, article, [role=\"main\"]").ConfigureAwait(false);
        if (mainHandle != null)
        {
            html = await mainHandle.InnerHTMLAsync().ConfigureAwait(false);
        }
        else
        {
            html = await page.InnerHTMLAsync("body").ConfigureAwait(false);
        }

#pragma warning disable CS0618
        var config = new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true
        };
#pragma warning restore CS0618
        var converter = new ReverseMarkdown.Converter(config);
        var markdown = converter.Convert(html);

        return Regex.Replace(markdown, @"\n{3,}", "\n\n").Trim();
    }

    private static WebFailure ClassifyNavigationFailure(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        bool timedOut = ex is TimeoutException ||
                        message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("timed out", StringComparison.OrdinalIgnoreCase);

        if (timedOut)
        {
            return new WebFailure(WebFailureCode.Timeout, Retryable: true, BrowserFallbackRecommended: false,
                "Browser navigation timed out.", Stage: "browser");
        }

        return new WebFailure(WebFailureCode.BrowserNavigationFailure, Retryable: false, BrowserFallbackRecommended: false,
            $"Browser navigation failed: {message}", Stage: "browser");
    }

    private static string? GetRequiredChromiumRevision(string driverBaseDir) =>
        BrowserPool.GetRequiredChromiumRevision(driverBaseDir);

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _browserPool.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

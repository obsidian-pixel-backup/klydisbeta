using System.Text;
using System.Security.Cryptography;
using Klydis.Core.Chat;
using Klydis.Core.Web.Extraction;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Fetch;

/// <summary>
/// Browser-backed fetch. The stealth browser renders JavaScript and passes anti-bot checks,
/// but it is NOT a policy bypass: the same SSRF guard runs before navigation (browsers can
/// happily navigate to file:// and 169.254.169.254 — the guard is what stops it), and every
/// browser failure is classified into a structured <see cref="WebFailure"/>.
/// </summary>
public sealed class BrowserFetcher : IWebFetcher
{
    private readonly StealthBrowserService? _browser;
    private readonly SsrfGuard _guard;
    private readonly ILogger? _logger;

    public string Name => "browser";

    public BrowserFetcher(StealthBrowserService? browser, SsrfGuard guard, ILogger? logger = null)
    {
        _browser = browser;
        _guard = guard;
        _logger = logger;
    }

    public async Task<WebFetchOutcome> FetchAsync(WebFetchRequest request, CancellationToken ct)
    {
        if (_browser is null)
        {
            return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.BrowserUnavailable, false, false,
                "The stealth browser service is not available.", Stage: "browser"));
        }

        // Policy gate BEFORE navigation: the browser must never be an SSRF bypass.
        var policy = await _guard.ValidateAsync(request.Url, ct).ConfigureAwait(false);
        if (policy != null)
        {
            return WebFetchOutcome.Fail(policy with { Stage = "browser" });
        }

        try
        {
            var result = await _browser.FetchDocumentAsync(request.Url, request.MaxChars, ct).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                return WebFetchOutcome.Fail(result.Failure with { Stage = "browser" });
            }

            if (string.IsNullOrWhiteSpace(result.Markdown))
            {
                return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.EmptyContent, false, false,
                    "The browser loaded the page but no content could be extracted.", Stage: "browser"));
            }

            WebDocument doc;
            var extractor = new ContentExtractor();
            if (!string.IsNullOrEmpty(result.RawHtml))
            {
                var bodyBytes = Encoding.UTF8.GetBytes(result.RawHtml);
                doc = extractor.ExtractDocument(
                    bodyBytes,
                    request.Url,
                    result.FinalUrl ?? request.Url,
                    "text/html",
                    result.HttpStatus,
                    WebFetchMethod.Browser,
                    request.MaxChars,
                    new WebDiagnostics(new[] { request.Url }, new[] { "browser" }, 1, 0));
            }
            else
            {
                doc = new WebDocument(
                    request.Url,
                    result.FinalUrl ?? request.Url,
                    result.Title,
                    result.Markdown,
                    "text/html",
                    result.HttpStatus,
                    WebFetchMethod.Browser,
                    result.ContentWasTruncated,
                    DateTimeOffset.UtcNow,
                    ComputeHash(result.Markdown),
                    new WebDiagnostics(new[] { request.Url }, new[] { "browser" }, 1, 0));
            }

            return WebFetchOutcome.Ok(doc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Browser fetch failed for {Url}", request.Url);
            return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.BrowserNavigationFailure, false, false,
                $"Browser fetch failed: {ex.Message}", Stage: "browser"));
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

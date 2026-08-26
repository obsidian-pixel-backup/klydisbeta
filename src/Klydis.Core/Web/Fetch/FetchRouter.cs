using Klydis.Core.Web.Models;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Fetch;

/// <summary>
/// The web-fetch decision engine. Implements the escalation state machine:
///
///   HTTP attempt 1
///     ├─ success + meaningful content  → DONE
///     ├─ success + insufficient content → browser (JS shell)
///     ├─ retryable failure (timeout/5xx/429) → backoff → HTTP attempt 2
///     │     ├─ success → DONE
///     │     └─ failure → browser if the failure class recommends it
///     ├─ 403 / challenge → browser
///     └─ terminal (404, 4xx, policy block, TLS, too large) → FAIL, no browser
///
/// Retry and fallback are DIFFERENT concepts: retry = same mechanism, same target;
/// fallback = different mechanism. A browser escalation is never counted as an HTTP retry,
/// and a terminal failure never triggers either.
/// </summary>
public sealed class FetchRouter
{
    /// <summary>Minimum meaningful (non-whitespace) characters for "the page has real content".</summary>
    public const int MinMeaningfulContentChars = 200;

    /// <summary>Maximum HTTP attempts (1 + 1 retry for retryable failures).</summary>
    public const int MaxHttpAttempts = 2;

    private static readonly TimeSpan[] BackoffSchedule = { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    private readonly IWebFetcher _http;
    private readonly IWebFetcher? _browser;
    private readonly ILogger? _logger;

    public FetchRouter(IWebFetcher httpFetcher, IWebFetcher? browserFetcher, ILogger? logger = null)
    {
        _http = httpFetcher;
        _browser = browserFetcher;
        _logger = logger;
    }

    public async Task<WebFetchOutcome> FetchAsync(WebFetchRequest request, CancellationToken ct)
    {
        // ── HTTP attempt 1 ────────────────────────────────────────────────────────────────
        var outcome = await _http.FetchAsync(request, ct).ConfigureAwait(false);

        if (outcome.IsSuccess)
        {
            var doc = outcome.Document!;
            if (doc.MeaningfulCharCount >= MinMeaningfulContentChars || !request.AllowBrowserFallback || _browser is null)
            {
                return outcome;
            }

            // Success but a JS shell / near-empty page: the browser may render the real content.
            _logger?.LogInformation(
                "HTTP content insufficient ({Count} chars) for {Url}; escalating to browser.",
                doc.MeaningfulCharCount, request.Url);
            return await AttemptBrowserAsync(request, doc.HttpStatus, ct).ConfigureAwait(false);
        }

        var failure = outcome.Failure!;

        // ── Terminal classes: never retry, never escalate. ───────────────────────────────
        if (failure.Code is WebFailureCode.InvalidUrl
            or WebFailureCode.BlockedByPolicy
            or WebFailureCode.Http404
            or WebFailureCode.Http4xx
            or WebFailureCode.RedirectLimit
            or WebFailureCode.ContentTooLarge
            or WebFailureCode.UnsupportedContentType)
        {
            return outcome;
        }

        // ── Retryable: backoff, then a second HTTP attempt. ───────────────────────────────
        if (failure.Retryable && failure.Attempt < MaxHttpAttempts)
        {
            await BackoffAsync(failure, ct).ConfigureAwait(false);
            var retry = await _http.FetchAsync(request, ct).ConfigureAwait(false);

            if (retry.IsSuccess)
            {
                var retryDoc = retry.Document!;
                if (retryDoc.MeaningfulCharCount >= MinMeaningfulContentChars || !request.AllowBrowserFallback || _browser is null)
                {
                    return retry;
                }
                return await AttemptBrowserAsync(request, retryDoc.HttpStatus, ct).ConfigureAwait(false);
            }

            failure = retry.Failure!;
            if (!failure.BrowserFallbackRecommended)
            {
                return retry;
            }
        }

        // ── Browser escalation: 403 / challenge / 429-exhausted / 5xx / timeout / empty. ──
        if (failure.BrowserFallbackRecommended && request.AllowBrowserFallback && _browser is not null)
        {
            return await AttemptBrowserAsync(request, failure.HttpStatus, ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<WebFetchOutcome> AttemptBrowserAsync(WebFetchRequest request, int? httpStatus, CancellationToken ct)
    {
        _logger?.LogInformation("Escalating {Url} to stealth browser (HTTP status {Status}).", request.Url, httpStatus);
        var browserOutcome = await _browser!.FetchAsync(request, ct).ConfigureAwait(false);

        if (browserOutcome.IsSuccess)
        {
            return browserOutcome;
        }

        // Preserve the ORIGINAL failure when the browser fails too — the agent needs to know
        // why the page could not be fetched (e.g. HTTP 403), not just that the browser failed.
        var browserFailure = browserOutcome.Failure!;
        return WebFetchOutcome.Fail(browserFailure with
        {
            Attempt = browserFailure.Attempt + 1,
            Message = $"Browser fallback failed after HTTP {httpStatus}: {browserFailure.Message}"
        });
    }

    private static async Task BackoffAsync(WebFailure failure, CancellationToken ct)
    {
        TimeSpan delay;
        if (failure.Code == WebFailureCode.Http429 && double.TryParse(failure.RetryAfter, out var retryAfterSeconds))
        {
            delay = TimeSpan.FromSeconds(Math.Clamp(retryAfterSeconds, 1, 60));
        }
        else
        {
            var index = Math.Clamp(failure.Attempt - 1, 0, BackoffSchedule.Length - 1);
            var baseDelay = BackoffSchedule[index];
            // ±20% jitter so an agent's burst does not synchronize with the target.
            var jitter = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2);
            delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitter);
        }

        await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}

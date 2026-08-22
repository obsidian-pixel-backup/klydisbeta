namespace Klydis.Core.Web.Models;

/// <summary>
/// A structured, machine-readable web failure. Never throw a generic exception for a web
/// operation the agent can reason about: the harness classifies every failure into a code,
/// states whether retrying is sensible, and states whether escalating to the browser is
/// sensible — so the model (and the runtime's retry policy) never has to guess.
/// </summary>
/// <param name="Code">The failure class.</param>
/// <param name="Retryable">True when retrying the same mechanism is sensible (timeout, 5xx, 429).</param>
/// <param name="BrowserFallbackRecommended">True when escalating from HTTP to the stealth browser is sensible (403, JS shell, empty content).</param>
/// <param name="Message">Compact human-readable message for the model.</param>
/// <param name="HttpStatus">The HTTP status that produced the failure, when applicable.</param>
/// <param name="Stage">Which stage failed: policy, dns, http, redirect, extract, browser, search.</param>
/// <param name="Attempt">Which attempt (1-based) produced this failure.</param>
/// <param name="RetryAfter">Retry-After hint from the server (seconds), for 429 responses.</param>
public sealed record WebFailure(
    WebFailureCode Code,
    bool Retryable,
    bool BrowserFallbackRecommended,
    string Message,
    int? HttpStatus = null,
    string? Stage = null,
    int Attempt = 1,
    string? RetryAfter = null)
{
    /// <summary>
    /// Classifies an HTTP status into the canonical failure semantics (retryable? browser?).
    /// </summary>
    public static WebFailure FromHttpStatus(int status, string? retryAfter = null) => status switch
    {
        403 => new(WebFailureCode.Http403, Retryable: false, BrowserFallbackRecommended: true,
            "HTTP 403 Forbidden — the server refuses the request (often a bot challenge); a stealth browser retry may succeed.", status),
        404 => new(WebFailureCode.Http404, Retryable: false, BrowserFallbackRecommended: false,
            "HTTP 404 Not Found — the page does not exist; do not retry.", status),
        410 => new(WebFailureCode.Http404, Retryable: false, BrowserFallbackRecommended: false,
            "HTTP 410 Gone — the resource is permanently gone; do not retry.", status),
        429 => new(WebFailureCode.Http429, Retryable: true, BrowserFallbackRecommended: false,
            "HTTP 429 Too Many Requests — back off before retrying; do not escalate to the browser.", status, RetryAfter: retryAfter),
        >= 500 and < 600 => new(WebFailureCode.Http5xx, Retryable: true, BrowserFallbackRecommended: false,
            $"HTTP {status} server error — retryable, but a browser retry will not help.", status),
        >= 400 => new(WebFailureCode.Http4xx, Retryable: false, BrowserFallbackRecommended: false,
            $"HTTP {status} client error — terminal, do not retry.", status),
        _ => new(WebFailureCode.Http4xx, Retryable: false, BrowserFallbackRecommended: false,
            $"Unexpected HTTP status {status}.", status)
    };

    /// <summary>Short machine tag used in model projections, e.g. <c>HTTP_404</c>.</summary>
    public string Tag => Code.ToString().ToUpperInvariant();
}

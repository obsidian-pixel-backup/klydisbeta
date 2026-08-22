namespace Klydis.Core.Web.Models;

/// <summary>
/// Machine-readable classification of every way a web operation can fail. The agent reasons
/// over these codes instead of free-text exceptions, so it can distinguish "404 — don't retry"
/// from "403 — use the browser" from "429 — back off" from "timeout — retry once".
/// </summary>
public enum WebFailureCode
{
    /// <summary>URL could not be parsed or is not absolute.</summary>
    InvalidUrl,

    /// <summary>Rejected by URL policy: blocked scheme, private/reserved address, credentials, SSRF.</summary>
    BlockedByPolicy,

    /// <summary>Hostname did not resolve.</summary>
    DnsFailure,

    /// <summary>TCP connection could not be established.</summary>
    ConnectionFailure,

    /// <summary>TLS handshake failed.</summary>
    TlsFailure,

    /// <summary>Request or navigation exceeded its time budget.</summary>
    Timeout,

    /// <summary>Generic client error (4xx) that is not specifically classified below.</summary>
    Http4xx,

    /// <summary>HTTP 403 — often means "use the browser" or a bot challenge.</summary>
    Http403,

    /// <summary>HTTP 404/410 — terminal, never retry or escalate.</summary>
    Http404,

    /// <summary>HTTP 429 — back off (honor Retry-After), do not escalate to the browser.</summary>
    Http429,

    /// <summary>HTTP 5xx — retryable server error.</summary>
    Http5xx,

    /// <summary>Too many redirect hops.</summary>
    RedirectLimit,

    /// <summary>Response body exceeded the configured byte limit (checked before processing).</summary>
    ContentTooLarge,

    /// <summary>Content type is not supported by any extractor (e.g. PDF without an extractor).</summary>
    UnsupportedContentType,

    /// <summary>The stealth browser engine could not be launched or is not available.</summary>
    BrowserUnavailable,

    /// <summary>The browser navigated but the fetch failed (navigation error, crash, ...).</summary>
    BrowserNavigationFailure,

    /// <summary>A bot-detection / human-verification page was detected (Captcha, Cloudflare, ...).</summary>
    BotChallenge,

    /// <summary>Content was retrieved but no meaningful content could be extracted.</summary>
    ExtractionFailure,

    /// <summary>Response was empty or contained only whitespace.</summary>
    EmptyContent,

    /// <summary>Rate limiting detected outside a specific HTTP status (search provider, ...).</summary>
    RateLimited,

    /// <summary>All search providers failed for a query.</summary>
    SearchProviderFailed
}

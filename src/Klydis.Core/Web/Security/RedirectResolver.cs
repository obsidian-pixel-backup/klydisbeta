using System.Net;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Security;

/// <summary>
/// Shared redirect resolution and per-hop policy validation across HTTP, Search, and Browser.
/// Enforces redirect bounds, relative-to-absolute resolution, and strict per-hop SSRF validation.
/// </summary>
public static class RedirectResolver
{
    public const int DefaultMaxRedirects = 10;

    /// <summary>
    /// Resolves the absolute URL for a redirect location header relative to the source URL.
    /// Returns null if the target URL is invalid.
    /// </summary>
    public static string? ResolveTargetUrl(string sourceUrl, string locationHeader)
    {
        if (string.IsNullOrWhiteSpace(locationHeader)) return null;

        if (Uri.TryCreate(locationHeader, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, locationHeader, out var combinedUri))
        {
            return combinedUri.ToString();
        }

        return null;
    }

    /// <summary>
    /// Validates and resolves the next hop in a redirect chain.
    /// Checks max redirect hops, location header presence, syntax, and runs full host SSRF validation.
    /// </summary>
    public static async Task<(string? NextUrl, WebFailure? Failure)> ValidateAndResolveNextHopAsync(
        string currentUrl,
        string? locationHeader,
        int hopCount,
        IWebSecurityPolicy policy,
        CancellationToken ct)
    {
        if (hopCount > DefaultMaxRedirects)
        {
            return (null, new WebFailure(WebFailureCode.RedirectLimit, false, false,
                $"More than {DefaultMaxRedirects} redirects.", Stage: "redirect", Attempt: hopCount));
        }

        if (string.IsNullOrWhiteSpace(locationHeader))
        {
            return (null, new WebFailure(WebFailureCode.Http4xx, false, false,
                "Redirect response without a Location header.", Stage: "redirect", Attempt: hopCount));
        }

        var targetUrl = ResolveTargetUrl(currentUrl, locationHeader);
        if (targetUrl == null)
        {
            return (null, new WebFailure(WebFailureCode.InvalidUrl, false, false,
                $"Invalid redirect target '{locationHeader}'.", Stage: "redirect", Attempt: hopCount));
        }

        var validation = await policy.ValidateRedirectAsync(currentUrl, targetUrl, hopCount, ct).ConfigureAwait(false);
        if (validation != null)
        {
            return (null, validation);
        }

        return (targetUrl, null);
    }
}

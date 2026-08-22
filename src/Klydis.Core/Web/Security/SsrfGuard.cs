using System.Net;
using Klydis.Core.Web.Models;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Security;

/// <summary>
/// The single SSRF gate every web mechanism (HTTP fetcher AND browser fetcher) must pass
/// before touching the network. Responsibilities:
///   • scheme allowlist (http/https only; file://, data://, ... blocked by policy)
///   • host validation: IP literals checked directly; hostnames resolved and every address
///     checked — private/reserved/link-local/loopback addresses are rejected
///   • DNS pinning: <see cref="ResolvePublicAddressesAsync"/> returns the verified addresses
///     so a connect callback can talk ONLY to those IPs (closes the DNS-rebinding TOCTOU)
///   • redirect revalidation: callers re-run <see cref="ValidateAsync"/> on every redirect hop
/// A blocked request returns a structured <see cref="WebFailure"/> (BlockedByPolicy) — never
/// an exception the model has to decode.
/// </summary>
public sealed class SsrfGuard
{
    private readonly IDnsResolver _resolver;
    private readonly bool _allowLoopback;
    private readonly ILogger? _logger;

    public SsrfGuard(ILogger? logger = null, IDnsResolver? resolver = null, bool allowLoopback = false)
    {
        _logger = logger;
        _resolver = resolver ?? new SystemDnsResolver();
        _allowLoopback = allowLoopback;
    }

    /// <summary>True only in test setups that intentionally fetch loopback test servers.</summary>
    public bool AllowLoopback => _allowLoopback;

    /// <summary>
    /// Validates URL syntax and scheme without I/O. Returns a failure, or null when the URL
    /// is structurally acceptable.
    /// </summary>
    public WebFailure? ValidateSyntax(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new WebFailure(WebFailureCode.InvalidUrl, false, false, "The URL is empty.", Stage: "policy");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
        {
            return new WebFailure(WebFailureCode.InvalidUrl, false, false,
                $"'{Shorten(url)}' is not a valid absolute URL.", Stage: "policy");
        }

        if (!UrlPolicy.IsSchemeAllowed(uri))
        {
            return new WebFailure(WebFailureCode.BlockedByPolicy, false, false,
                $"Scheme '{uri.Scheme}' is not allowed — only http and https can be fetched.", Stage: "policy");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return new WebFailure(WebFailureCode.InvalidUrl, false, false,
                $"'{Shorten(url)}' has no host.", Stage: "policy");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new WebFailure(WebFailureCode.BlockedByPolicy, false, false,
                "URLs with embedded credentials are not allowed.", Stage: "policy");
        }

        return null;
    }

    /// <summary>
    /// Full validation: syntax + host resolution. Every resolved address is checked; if any
    /// resolves to a private/reserved address the request is blocked. Call this before every
    /// request and again on every redirect hop.
    /// </summary>
    public async Task<WebFailure?> ValidateAsync(string url, CancellationToken ct)
    {
        var syntax = ValidateSyntax(url);
        if (syntax != null) return syntax;

        var uri = new Uri(url, UriKind.Absolute);

        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            if (!_allowLoopback && UrlPolicy.IsPrivateOrReserved(literal))
            {
                return Blocked($"Address '{literal}' is private, reserved, or link-local.");
            }
            return null;
        }

        var addresses = await ResolveAsync(uri.Host, ct).ConfigureAwait(false);
        if (addresses is null || addresses.Count == 0)
        {
            return new WebFailure(WebFailureCode.DnsFailure, true, false,
                $"DNS resolution failed for host '{uri.Host}'.", Stage: "dns");
        }

        if (!_allowLoopback)
        {
            var blocked = addresses.FirstOrDefault(UrlPolicy.IsPrivateOrReserved);
            if (blocked != null)
            {
                return Blocked($"Host '{uri.Host}' resolves to '{blocked}', which is private, reserved, or link-local.");
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a host and returns ONLY addresses that passed policy. Used by the HTTP
    /// fetcher's connect callback so the socket connects to the verified IPs — a second DNS
    /// lookup (and a rebinding attacker) cannot redirect the connection elsewhere.
    /// </summary>
    /// <exception cref="WebPolicyException">When the host is blocked or unresolvable.</exception>
    public async Task<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            if (!_allowLoopback && UrlPolicy.IsPrivateOrReserved(literal))
            {
                throw new WebPolicyException($"Address '{literal}' is private, reserved, or link-local.");
            }
            return new[] { literal };
        }

        var addresses = await ResolveAsync(host, ct).ConfigureAwait(false);
        if (addresses is null || addresses.Count == 0)
        {
            throw new WebPolicyException($"DNS resolution failed for host '{host}'.");
        }

        if (!_allowLoopback)
        {
            var blocked = addresses.FirstOrDefault(UrlPolicy.IsPrivateOrReserved);
            if (blocked != null)
            {
                throw new WebPolicyException($"Host '{host}' resolves to blocked address '{blocked}'.");
            }
        }

        return addresses;
    }

    private async Task<IReadOnlyList<IPAddress>?> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await _resolver.ResolveAsync(host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "DNS resolution failed for host '{Host}'", host);
            return null;
        }
    }

    private WebFailure Blocked(string message) =>
        new(WebFailureCode.BlockedByPolicy, false, false, message, Stage: "policy");

    private static string Shorten(string url) => url.Length <= 120 ? url : url[..120] + "…";
}

/// <summary>
/// Thrown by <see cref="SsrfGuard.ResolvePublicAddressesAsync"/> when a host is blocked by
/// policy or unresolvable. Distinct from <see cref="WebFailure"/> because this path is used
/// inside connection code where a structured outcome cannot be returned.
/// </summary>
public sealed class WebPolicyException : Exception
{
    public WebPolicyException(string message) : base(message) { }

    public WebPolicyException(string message, Exception inner) : base(message, inner) { }
}

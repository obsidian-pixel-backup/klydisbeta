using System.Net;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Security;

/// <summary>
/// The unified security contract that gates all network interactions across the web subsystem
/// (direct HTTP fetcher, search HTTP clients, and browser request interception).
/// </summary>
public interface IWebSecurityPolicy
{
    /// <summary>Checks whether loopback addresses are allowed (typically true only in test environments).</summary>
    bool AllowLoopback { get; }

    /// <summary>
    /// Validates URL syntax, scheme allowlist, and embedded credentials without performing I/O.
    /// </summary>
    WebFailure? ValidateSyntax(string url);

    /// <summary>
    /// Performs full validation: syntax, scheme, and DNS host resolution. Verifies that no resolved
    /// IP is private, reserved, loopback, or link-local.
    /// </summary>
    Task<WebFailure?> ValidateAsync(string url, CancellationToken ct);

    /// <summary>
    /// Resolves a host and returns only verified public IP addresses for socket pinning / DNS rebinding defense.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(string host, CancellationToken ct);

    /// <summary>
    /// Validates a redirect from <paramref name="sourceUrl"/> to <paramref name="targetUrl"/> at hop <paramref name="hopCount"/>.
    /// </summary>
    Task<WebFailure?> ValidateRedirectAsync(string sourceUrl, string targetUrl, int hopCount, CancellationToken ct);
}

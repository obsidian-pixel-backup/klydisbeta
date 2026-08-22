using System.Net;

namespace Klydis.Core.Web.Security;

/// <summary>
/// DNS resolution seam. Injected so the SSRF guard's resolution/validation logic is fully
/// unit-testable without touching the real resolver (and so DNS-rebinding tests can script
/// arbitrary resolutions).
/// </summary>
public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Real DNS resolution via <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses;
    }
}

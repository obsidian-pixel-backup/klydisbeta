using System.Net;

namespace Klydis.Core.Web.Security;

/// <summary>
/// Contextual security tracker for a single web execution task or navigation chain.
/// Keeps track of verified hosts, redirect hops, byte counts, and execution timeline.
/// </summary>
public sealed class WebSecurityContext
{
    public Uri InitialUri { get; init; }
    public IWebSecurityPolicy Policy { get; init; }
    public HashSet<string> AllowedDomains { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<IPAddress> VerifiedAddresses { get; } = new();
    public List<string> RedirectChain { get; } = new();
    public int RedirectCount { get; set; }
    public int RequestCount { get; set; }
    public long BytesReceived { get; set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public WebSecurityContext(Uri initialUri, IWebSecurityPolicy policy)
    {
        InitialUri = initialUri;
        Policy = policy;
        if (!string.IsNullOrEmpty(initialUri.Host))
        {
            AllowedDomains.Add(initialUri.Host);
        }
    }
}

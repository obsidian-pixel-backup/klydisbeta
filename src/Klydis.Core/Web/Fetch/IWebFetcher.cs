using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Fetch;

/// <summary>
/// A fetch mechanism (HTTP or browser). Both implementations return structured
/// <see cref="WebFetchOutcome"/> — never bare exceptions — and both are policed by the same
/// SSRF guard so neither can bypass URL security.
/// </summary>
public interface IWebFetcher
{
    string Name { get; }

    Task<WebFetchOutcome> FetchAsync(WebFetchRequest request, CancellationToken ct);
}

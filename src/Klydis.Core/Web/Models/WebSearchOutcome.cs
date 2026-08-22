namespace Klydis.Core.Web.Models;

/// <summary>Search results plus an optional structured failure (all providers exhausted).</summary>
public sealed record WebSearchOutcome(IReadOnlyList<WebSearchResult> Results, WebFailure? Failure)
{
    public bool IsSuccess => Failure is null;
}

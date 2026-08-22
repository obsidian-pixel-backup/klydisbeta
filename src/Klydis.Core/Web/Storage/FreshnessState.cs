namespace Klydis.Core.Web.Storage;

/// <summary>
/// The freshness state of a cached web document.
/// </summary>
public enum FreshnessState
{
    Fresh,
    Stale,
    Expired,
    Invalidated,
    NotFound
}

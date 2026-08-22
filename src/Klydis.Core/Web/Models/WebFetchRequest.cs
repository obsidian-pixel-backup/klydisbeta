namespace Klydis.Core.Web.Models;

/// <summary>
/// A semantic web-open request. The model asks to open a URL; the runtime decides whether
/// that means plain HTTP, HTTP + extraction, or a stealth-browser escalation.
/// </summary>
public sealed record WebFetchRequest(
    string Url,
    int MaxChars = 20_000,
    bool AllowBrowserFallback = true,
    int MaxBytes = 10 * 1024 * 1024);

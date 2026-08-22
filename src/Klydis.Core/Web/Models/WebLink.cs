namespace Klydis.Core.Web.Models;

/// <summary>
/// A structured hyperlink discovered during web extraction.
/// Preserves destination URLs alongside anchor text so autonomous agents can navigate accurately.
/// </summary>
public sealed record WebLink(
    string Text,
    string Url,
    string? Context = null,
    bool IsExternal = false);

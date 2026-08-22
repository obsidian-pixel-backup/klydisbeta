namespace Klydis.Core.Web.Models;

/// <summary>
/// A structured image asset reference extracted from a web document.
/// </summary>
public sealed record WebImage(
    string? Alt,
    string Url,
    string? Caption = null);

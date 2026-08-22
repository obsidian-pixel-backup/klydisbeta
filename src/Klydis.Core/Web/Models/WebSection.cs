namespace Klydis.Core.Web.Models;

/// <summary>
/// A structured document section delineated by a heading.
/// Enables targeted reading of specific chapters, installation guides, or API methods off-context.
/// </summary>
public sealed record WebSection(
    string Heading,
    int Level,
    string ContentMarkdown,
    string? Id = null);

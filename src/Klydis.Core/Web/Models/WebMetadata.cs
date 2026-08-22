namespace Klydis.Core.Web.Models;

/// <summary>
/// Structured document metadata extracted from HTML meta tags, OpenGraph, JSON-LD schema.org, and headers.
/// </summary>
public sealed record WebMetadata(
    string? Title = null,
    string? Description = null,
    string? CanonicalUrl = null,
    string? Author = null,
    DateTimeOffset? PublishedAt = null,
    DateTimeOffset? ModifiedAt = null,
    string? SiteName = null,
    string? Language = null,
    string? ContentType = null,
    int? WordCount = null,
    string? JsonLd = null,
    IReadOnlyDictionary<string, string>? OpenGraph = null);

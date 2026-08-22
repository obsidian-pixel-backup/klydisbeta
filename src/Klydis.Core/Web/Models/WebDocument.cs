namespace Klydis.Core.Web.Models;

/// <summary>
/// A normalized, structured representation of a fetched web document.
/// Encapsulates structured sections, links, tables, metadata, pagination, and evidence hashes.
/// </summary>
public sealed record WebDocument(
    string RequestedUrl,
    string? FinalUrl,
    string? Title,
    string ContentMarkdown,
    string ContentType,
    int? HttpStatus,
    WebFetchMethod FetchMethod,
    bool ContentWasTruncated,
    DateTimeOffset RetrievedAt,
    string ContentHash,
    WebDiagnostics Diagnostics,
    string? Id = null,
    PageType PageType = PageType.Generic,
    WebMetadata? Metadata = null,
    IReadOnlyList<WebSection>? Sections = null,
    IReadOnlyList<WebLink>? Links = null,
    IReadOnlyList<WebTable>? Tables = null,
    IReadOnlyList<WebImage>? Images = null,
    WebPagination? Pagination = null,
    string? ArtifactPath = null,
    string? RawHtml = null)
{
    public string Id { get; init; } = Id ?? ("web-" + Guid.NewGuid().ToString("N")[..12]);
    public PageType PageType { get; init; } = PageType;
    public WebMetadata Metadata { get; init; } = Metadata ?? new WebMetadata(Title: Title, ContentType: ContentType);
    public IReadOnlyList<WebSection> Sections { get; init; } = Sections ?? Array.Empty<WebSection>();
    public IReadOnlyList<WebLink> Links { get; init; } = Links ?? Array.Empty<WebLink>();
    public IReadOnlyList<WebTable> Tables { get; init; } = Tables ?? Array.Empty<WebTable>();
    public IReadOnlyList<WebImage> Images { get; init; } = Images ?? Array.Empty<WebImage>();
    public WebPagination? Pagination { get; init; } = Pagination;
    public string? ArtifactPath { get; init; } = ArtifactPath;
    public string? RawHtml { get; init; } = RawHtml;

    /// <summary>Non-whitespace characters — the router's "does this page have real content?" signal.</summary>
    public int MeaningfulCharCount => ContentMarkdown.Count(c => !char.IsWhiteSpace(c));
}

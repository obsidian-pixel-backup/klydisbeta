namespace Klydis.Core.Web.Models;

/// <summary>
/// A normalized, structured representation of a fetched web document. Replaces raw
/// <c>string</c> returns: the model context is built from this, the raw content is storable
/// off-context, and the hash gives evidence identity.
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
    WebDiagnostics Diagnostics)
{
    /// <summary>Non-whitespace characters — the router's "does this page have real content?" signal.</summary>
    public int MeaningfulCharCount => ContentMarkdown.Count(c => !char.IsWhiteSpace(c));
}

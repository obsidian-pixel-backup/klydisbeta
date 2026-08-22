namespace Klydis.Core.Web.Models;

/// <summary>
/// Either a <see cref="WebDocument"/> or a structured <see cref="WebFailure"/> — never a
/// bare exception across the web subsystem boundary.
/// </summary>
public sealed record WebFetchOutcome(WebDocument? Document, WebFailure? Failure)
{
    public bool IsSuccess => Document is not null;

    public static WebFetchOutcome Ok(WebDocument document) => new(document, null);

    public static WebFetchOutcome Fail(WebFailure failure) => new(null, failure);
}

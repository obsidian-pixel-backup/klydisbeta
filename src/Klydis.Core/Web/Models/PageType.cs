namespace Klydis.Core.Web.Models;

/// <summary>
/// Classified semantic category of a web page.
/// Guides the selection of specialized extraction and parsing strategies.
/// </summary>
public enum PageType
{
    Generic,
    Article,
    Documentation,
    GitHub,
    Wikipedia,
    SearchResults,
    Product,
    Forum,
    Blog,
    Reference,
    Listing,
    Table,
    SPA,
    LoginRequired,
    BotChallenge,
    ErrorPage
}

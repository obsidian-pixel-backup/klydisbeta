namespace Klydis.Core.Web.Models;

/// <summary>
/// Pagination navigation controls and state discovered in listing/search pages.
/// </summary>
public sealed record WebPagination(
    int? CurrentPage = null,
    string? NextUrl = null,
    string? PreviousUrl = null,
    int? TotalPages = null);

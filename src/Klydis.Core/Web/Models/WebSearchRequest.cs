namespace Klydis.Core.Web.Models;

/// <summary>A semantic web-search request.</summary>
public sealed record WebSearchRequest(
    string Query,
    int MaxResults = 5,
    bool FreshnessRequired = false);

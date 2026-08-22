namespace Klydis.Core.Web.Models;

/// <summary>
/// Resource budget governing web execution during an agent turn.
/// </summary>
public sealed class WebBudget
{
    public int MaxSearches { get; init; } = 10;
    public int MaxCrawls { get; init; } = 20;
    public int MaxBrowserNavigations { get; init; } = 8;
    public long MaxBytes { get; init; } = 100_000_000;
    public TimeSpan MaxTotalDuration { get; init; } = TimeSpan.FromMinutes(5);
}

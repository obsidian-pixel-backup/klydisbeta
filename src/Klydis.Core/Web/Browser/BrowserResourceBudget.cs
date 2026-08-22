namespace Klydis.Core.Web.Browser;

/// <summary>
/// Fine-grained resource consumption limits for browser navigation sessions.
/// Guards against memory exhaustion, recursive sub-requests, and excessive bandwidth.
/// </summary>
public sealed record BrowserResourceBudget(
    int MaxRequests = 60,
    long MaxTotalBytes = 25_000_000, // 25 MB
    int MaxImages = 0,
    int MaxScripts = 40,
    int MaxFonts = 0,
    int MaxFrames = 3,
    TimeSpan MaxNavigationTime = default,
    TimeSpan MaxHydrationTime = default,
    bool AllowImages = false,
    bool AllowFonts = false,
    bool AllowMedia = false,
    bool AllowWebSockets = false)
{
    public static readonly BrowserResourceBudget Default = new(
        MaxNavigationTime: TimeSpan.FromSeconds(25),
        MaxHydrationTime: TimeSpan.FromSeconds(5));

    public static readonly BrowserResourceBudget Permissive = new(
        MaxRequests: 150,
        MaxTotalBytes: 50_000_000,
        MaxImages: 50,
        MaxScripts: 80,
        MaxFonts: 10,
        MaxFrames: 10,
        MaxNavigationTime: TimeSpan.FromSeconds(35),
        MaxHydrationTime: TimeSpan.FromSeconds(8),
        AllowImages: true,
        AllowFonts: true,
        AllowMedia: false,
        AllowWebSockets: false);
}

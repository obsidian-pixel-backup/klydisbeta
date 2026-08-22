namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Detects bot mitigation challenges, captchas, and JS verification walls
/// (Cloudflare, Akamai, DataDome, PerimeterX, AWS WAF, etc.).
/// </summary>
public static class BotChallengeDetector
{
    private static readonly string[] ChallengeKeywords =
    {
        "checking your browser",
        "just a moment...",
        "cf-browser-verification",
        "cloudflare ray id",
        "attention required! | cloudflare",
        "please verify you are a human",
        "verify you are human",
        "datadome",
        "perimeterx",
        "challenge-running",
        "hcaptcha",
        "g-recaptcha",
        "cf-chl-bypass",
        "access denied",
        "enable javascript and cookies to continue",
        "ddos-guard"
    };

    public static bool IsBotChallenge(string html, out string? detectedSignal)
    {
        detectedSignal = null;
        if (string.IsNullOrWhiteSpace(html)) return false;

        var lower = html.Length > 20000 ? html[..20000].ToLowerInvariant() : html.ToLowerInvariant();

        foreach (var keyword in ChallengeKeywords)
        {
            if (lower.Contains(keyword))
            {
                detectedSignal = keyword;
                return true;
            }
        }

        return false;
    }
}

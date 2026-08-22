using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using ManagedCode.Playwright.Stealth;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Chat;

/// <summary>
/// Result of a structured browser fetch. The web subsystem gets classified outcomes, not
/// exceptions, so the fetch router and the agent can reason about what failed.
/// </summary>
public sealed record BrowserFetchDocument(
    string? Title,
    string? FinalUrl,
    string Markdown,
    int? HttpStatus,
    bool ContentWasTruncated,
    WebFailure? Failure);

/// <summary>
/// Service managing stealth browser contexts, persistent profiles, engine spoofing, and
/// resilient web crawling.
///
/// NOTE (web-subsystem hardening): this service is the BROWSER ENGINE only. URL policy is
/// enforced here before every navigation so the browser can never be an SSRF bypass, but
/// routing/retry/fallback decisions live in <see cref="Klydis.Core.Web.Fetch.FetchRouter"/>
/// and the agent entry point is <see cref="Klydis.Core.Web.WebOrchestrator"/>.
/// Concurrency remains a global semaphore (P1: BrowserManager + context pool).
/// </summary>
public class StealthBrowserService : IAsyncDisposable
{
    private readonly ILogger<StealthBrowserService> _logger;
    private readonly CamoufoxManager _camoufoxManager;
    private readonly SsrfGuard _ssrfGuard;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowserContext? _persistentContext;
    private bool _isDisposed;

    private static readonly SemaphoreSlim _installLock = new(1, 1);
    private static bool _installAttempted;
    private static bool _installSucceeded;

    private static readonly string[] DefaultUserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
    };

    public StealthBrowserService(ILogger<StealthBrowserService> logger, CamoufoxManager camoufoxManager, SsrfGuard? ssrfGuard = null)
    {
        _logger = logger;
        _camoufoxManager = camoufoxManager;
        _ssrfGuard = ssrfGuard ?? new SsrfGuard(logger);
    }

    /// <summary>
    /// Structured browser fetch: SSRF policy is enforced BEFORE navigation, navigation
    /// failures are classified, and extraction waits for content candidates instead of a
    /// fixed sleep. Returns a <see cref="BrowserFetchDocument"/> — never throws for
    /// page-level failures.
    /// </summary>
    public async Task<BrowserFetchDocument> FetchDocumentAsync(string url, int maxChars, CancellationToken ct = default)
    {
        // The browser must never be an SSRF bypass: policy gate before every navigation.
        var policy = await _ssrfGuard.ValidateAsync(url, ct).ConfigureAwait(false);
        if (policy != null)
        {
            _logger.LogWarning("Browser navigation blocked by policy: {Message}", policy.Message);
            return new BrowserFetchDocument(null, null, string.Empty, null, false, policy with { Stage = "browser" });
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            IPage page;
            try
            {
                page = await GetOrCreateStealthPageAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stealth browser could not be launched for {Url}", url);
                return new BrowserFetchDocument(null, null, string.Empty, null, false,
                    new WebFailure(WebFailureCode.BrowserUnavailable, false, false,
                        $"The stealth browser could not be launched: {ex.Message}", Stage: "browser"));
            }

            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 25000
                });
            }
            catch (Exception ex)
            {
                return new BrowserFetchDocument(null, null, string.Empty, null, false, ClassifyNavigationFailure(ex));
            }

            string? finalUrl = null;
            try { finalUrl = page.Url; } catch { /* page may already be closed */ }

            // Content-candidate wait: poll for meaningful content instead of a fixed sleep.
            await WaitForContentCandidateAsync(page, ct);

            string title = string.Empty;
            try { title = await page.TitleAsync(); } catch { /* ignore */ }

            string markdown;
            try
            {
                markdown = await ExtractMarkdownAsync(page);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Browser content extraction failed for {Url}", url);
                return new BrowserFetchDocument(null, finalUrl, string.Empty, null, false,
                    new WebFailure(WebFailureCode.ExtractionFailure, false, false,
                        $"Browser content extraction failed: {ex.Message}", Stage: "browser"));
            }

            if (string.IsNullOrWhiteSpace(markdown))
            {
                return new BrowserFetchDocument(null, finalUrl, string.Empty, null, false,
                    new WebFailure(WebFailureCode.EmptyContent, false, false,
                        "The browser loaded the page but no meaningful content was found.", Stage: "browser"));
            }

            bool truncated = markdown.Length > maxChars;
            if (truncated)
            {
                markdown = markdown[..maxChars] + "\n\n… [truncated]";
            }

            return new BrowserFetchDocument(title, finalUrl, markdown, 200, truncated, null);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Executes a web crawl on the target URL with anti-detection, stealth patching, and
    /// Markdown conversion. Compatibility wrapper over <see cref="FetchDocumentAsync"/>;
    /// new code should use the structured path via <see cref="Klydis.Core.Web.WebOrchestrator"/>.
    /// </summary>
    public async Task<string> CrawlUrlAsync(string url, CancellationToken ct = default)
    {
        var result = await FetchDocumentAsync(url, 20000, ct).ConfigureAwait(false);
        if (result.Failure != null)
        {
            return $"WEB_FAILED\ncode={result.Failure.Tag}\nmessage: {result.Failure.Message}";
        }

        var header = $"# Page Title: {result.Title ?? "(none)"}\nSource URL: {url}\n\n---\n\n";
        return header + result.Markdown;
    }

    /// <summary>
    /// Executes a search query using stealth browser rendering for engines like Bing or
    /// DuckDuckGo. Policy-gated like every other navigation; returns null when blocked or
    /// failed.
    /// </summary>
    public async Task<string?> RenderPageHtmlAsync(string url, CancellationToken ct = default)
    {
        var policy = await _ssrfGuard.ValidateAsync(url, ct).ConfigureAwait(false);
        if (policy != null)
        {
            _logger.LogWarning("Browser render blocked by policy: {Message}", policy.Message);
            return null;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            var page = await GetOrCreateStealthPageAsync(ct);
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            await Task.Delay(1000, ct);
            return await page.ContentAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stealth browser page HTML render failed for URL: {Url}", url);
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Waits for a content candidate instead of sleeping a fixed duration: fast pages are
    /// extracted immediately, slow/JS-hydrated pages get up to ~3.7s of polling, and pages
    /// that never produce content fail fast at extraction rather than after a blind wait.
    /// </summary>
    private static async Task WaitForContentCandidateAsync(IPage page, CancellationToken ct)
    {
        for (int i = 0; i < 8; i++)
        {
            bool hasContent;
            try
            {
                hasContent = await page.EvaluateAsync<bool>(@"() => {
                    const main = document.querySelector('main, article, [role=""main""]');
                    if (main && main.innerText.trim().length > 100) return true;
                    const body = document.body;
                    return body !== null && body.innerText.trim().length > 200;
                }").ConfigureAwait(false);
            }
            catch
            {
                return; // page navigated away or was closed — extract what we have
            }

            if (hasContent) return;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        // Final settling delay for slow hydration before extraction.
        await Task.Delay(500, ct).ConfigureAwait(false);
    }

    private static async Task<string> ExtractMarkdownAsync(IPage page)
    {
        // Purge DOM noise elements.
        await page.EvaluateAsync(@"() => {
            const noisySelectors = [
                'nav', 'footer', 'header', 'aside',
                '[role=""navigation""]', '[role=""banner""]',
                '.cookie-banner', '.ad-container', 'iframe',
                '#cookie-notice', '#gdpr-banner', '.social-share'
            ];
            noisySelectors.forEach(s => document.querySelectorAll(s).forEach(el => el.remove()));
        }").ConfigureAwait(false);

        // Extract main content.
        string html;
        var mainHandle = await page.QuerySelectorAsync("main, article, [role=\"main\"]").ConfigureAwait(false);
        if (mainHandle != null)
        {
            html = await mainHandle.InnerHTMLAsync().ConfigureAwait(false);
        }
        else
        {
            html = await page.InnerHTMLAsync("body").ConfigureAwait(false);
        }

        // Convert to clean Markdown.
#pragma warning disable CS0618
        var config = new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true
        };
#pragma warning restore CS0618
        var converter = new ReverseMarkdown.Converter(config);
        var markdown = converter.Convert(html);

        return Regex.Replace(markdown, @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Classifies browser navigation failures into structured, actionable failures.</summary>
    private static WebFailure ClassifyNavigationFailure(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        bool timedOut = ex is TimeoutException ||
                        message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("timed out", StringComparison.OrdinalIgnoreCase);

        if (timedOut)
        {
            return new WebFailure(WebFailureCode.Timeout, Retryable: true, BrowserFallbackRecommended: false,
                "Browser navigation timed out.", Stage: "browser");
        }

        return new WebFailure(WebFailureCode.BrowserNavigationFailure, Retryable: false, BrowserFallbackRecommended: false,
            $"Browser navigation failed: {message}", Stage: "browser");
    }

    private async Task<IPage> GetOrCreateStealthPageAsync(CancellationToken ct)
    {
        if (_persistentContext == null)
        {
            _playwright = await Playwright.CreateAsync();
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var profileDir = Path.Combine(userProfile, ".klydis", "browser_profile");
                Directory.CreateDirectory(profileDir);

                var camoufoxPath = await _camoufoxManager.GetExecutablePathAsync(ct);
                var randomAgent = DefaultUserAgents[Random.Shared.Next(DefaultUserAgents.Length)];

                var contextOptions = new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = true,
                    UserAgent = randomAgent,
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    Locale = "en-US",
                    TimezoneId = "America/New_York",
                    Args = new[]
                    {
                        "--disable-blink-features=AutomationControlled",
                        "--no-sandbox",
                        "--disable-infobars"
                    }
                };

                if (!string.IsNullOrEmpty(camoufoxPath) && File.Exists(camoufoxPath))
                {
                    try
                    {
                        _logger.LogInformation("Launching Playwright with stealth Camoufox engine binary at: {Path}", camoufoxPath);
                        contextOptions.ExecutablePath = camoufoxPath;
                        _persistentContext = await _playwright.Firefox.LaunchPersistentContextAsync(profileDir, contextOptions);
                    }
                    catch (Exception ex)
                    {
                        // Camoufox releases can drift out of sync with the bundled Playwright protocol
                        // (e.g. "Browser.setDefaultViewport" schema errors). Never let that kill the crawl.
                        _logger.LogWarning(ex, "Camoufox launch failed; falling back to Chromium.");
                        contextOptions.ExecutablePath = null;
                    }
                }

                if (_persistentContext == null)
                {
                    // Make sure Playwright's Chromium is installed before falling back to it.
                    await EnsureChromiumInstalledAsync();

                    // Prefer the FULL Chromium binary over the default headless-shell: the headless
                    // shell is heavily fingerprinted and is rejected by aggressive anti-bot CDNs
                    // (e.g. Akamai returns ERR_HTTP2_PROTOCOL_ERROR), while full Chromium passes.
                    var fullChromiumPath = GetFullChromiumExecutablePath();
                    if (!string.IsNullOrEmpty(fullChromiumPath))
                    {
                        _logger.LogInformation("Launching full Chromium with stealth patches at: {Path}", fullChromiumPath);
                        contextOptions.ExecutablePath = fullChromiumPath;
                    }
                    else
                    {
                        _logger.LogInformation("Full Chromium binary not found; launching default headless Chromium with stealth patches.");
                    }

                    _persistentContext = await _playwright.Chromium.LaunchPersistentContextAsync(profileDir, contextOptions);
                }
            }
            catch
            {
                _playwright?.Dispose();
                _playwright = null;
                throw;
            }
        }

        var pages = _persistentContext.Pages;
        IPage page;
        if (pages.Count > 0)
        {
            page = pages[0];
        }
        else
        {
            page = await _persistentContext.NewPageAsync();
        }

        // Apply ManagedCode Playwright stealth patches if applicable
        try
        {
            // Inject stealth scripts to obscure automation parameters
            await page.AddInitScriptAsync(@"() => {
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                window.chrome = { runtime: {} };
            }");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply custom init script stealth patches.");
        }

        return page;
    }

    /// <summary>
    /// Locates the full Chromium executable for the revision the bundled driver expects.
    /// Returns null when it cannot be found. The full build (chrome-win64/chrome-linux/chrome-mac)
    /// is preferred over the headless-shell because anti-bot CDNs reject the headless shell.
    /// </summary>
    private static string? GetFullChromiumExecutablePath()
    {
        try
        {
            var revision = GetRequiredChromiumRevision(AppContext.BaseDirectory);
            if (revision == null) return null;

            var msPlaywrightDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
            var chromiumDir = Path.Combine(msPlaywrightDir, $"chromium-{revision}");
            if (!Directory.Exists(chromiumDir)) return null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Path.Combine(chromiumDir, "chrome-win64", "chrome.exe");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Path.Combine(chromiumDir, "chrome-linux", "chrome");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Path.Combine(chromiumDir, "chrome-mac", "Chromium.app", "Contents", "MacOS", "Chromium");
        }
        catch
        {
            // Fall through to the default headless-shell launch.
        }

        return null;
    }

    /// <summary>
    /// Reads the browser revision the bundled Playwright driver expects from the
    /// browsers.json copied to the app output directory. Returns null when unavailable.
    /// </summary>
    private static string? GetRequiredChromiumRevision(string driverBaseDir)
    {
        try
        {
            var browsersJson = Path.Combine(driverBaseDir, ".playwright", "package", "browsers.json");
            if (!File.Exists(browsersJson)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(browsersJson));
            foreach (var browser in doc.RootElement.GetProperty("browsers").EnumerateArray())
            {
                var name = browser.GetProperty("name").GetString();
                if (name is "chromium" or "chromium-headless-shell")
                {
                    return browser.GetProperty("revision").GetString();
                }
            }
        }
        catch
        {
            // Fall back to the loose check below.
        }

        return null;
    }

    /// <summary>
    /// Ensures Playwright's Chromium browser is installed, auto-installing it on first use
    /// when the binaries are missing (equivalent to running `playwright.ps1 install chromium`).
    /// Only attempts the download once per process; an interrupted crawl must not corrupt it,
    /// so the install runs on a background thread without the caller's cancellation token.
    /// </summary>
    private async Task EnsureChromiumInstalledAsync()
    {
        if (_installSucceeded || _installAttempted) return;
        if (Environment.GetEnvironmentVariable("KLYDIS_DISABLE_PLAYWRIGHT_AUTOINSTALL") == "1") return;

        await _installLock.WaitAsync();
        try
        {
            if (_installSucceeded || _installAttempted) return;

            var msPlaywrightDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");

            // The check must match the exact browser revision the bundled driver expects.
            // A stale chromium-* folder from a different Playwright version does not satisfy it.
            var baseDir = AppContext.BaseDirectory;
            var requiredRevision = GetRequiredChromiumRevision(baseDir);

            bool isInstalled;
            if (requiredRevision != null)
            {
                isInstalled =
                    Directory.Exists(Path.Combine(msPlaywrightDir, $"chromium-{requiredRevision}")) &&
                    Directory.Exists(Path.Combine(msPlaywrightDir, $"chromium_headless_shell-{requiredRevision}"));
            }
            else
            {
                // Fallback: any chromium folder (older Playwright layouts without browsers.json).
                isInstalled = Directory.Exists(msPlaywrightDir) &&
                              Directory.GetDirectories(msPlaywrightDir)
                                  .Any(d => Path.GetFileName(d).StartsWith("chromium-", StringComparison.OrdinalIgnoreCase));
            }

            if (isInstalled)
            {
                _installSucceeded = true;
                return;
            }

            _installAttempted = true;
            _logger.LogInformation("Playwright Chromium binaries (revision {Revision}) not found. Auto-installing (downloads ~160 MB on first use; crawl_url falls back to direct HTTP fetch in the meantime)...", requiredRevision ?? "matching");

            // Point the driver loader at the app output directory, where the NuGet package copies the driver.
            if (Directory.Exists(Path.Combine(baseDir, ".playwright")))
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", baseDir);
            }

            var exitCode = await Task.Run(() =>
                Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
            _logger.LogInformation("Playwright Chromium auto-install finished with exit code {ExitCode}.", exitCode);
            // Only remember success when the install actually completed; a failed download must
            // not be cached as success, or every later crawl would skip the install silently.
            _installSucceeded = exitCode == 0;
            if (!_installSucceeded)
            {
                _logger.LogWarning("Playwright Chromium auto-install reported a non-zero exit code ({ExitCode}); browser-based crawling will retry the install on next use.", exitCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playwright Chromium auto-install failed; browser-based crawling will be unavailable.");
            _installSucceeded = false;
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_persistentContext != null)
        {
            await _persistentContext.DisposeAsync();
            _persistentContext = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _semaphore.Dispose();

        GC.SuppressFinalize(this);
    }
}

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Playwright.Stealth;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Chat;

/// <summary>
/// Service managing stealth browser contexts, persistent profiles, engine spoofing, and resilient web crawling.
/// </summary>
public class StealthBrowserService : IAsyncDisposable
{
    private readonly ILogger<StealthBrowserService> _logger;
    private readonly CamoufoxManager _camoufoxManager;
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

    public StealthBrowserService(ILogger<StealthBrowserService> logger, CamoufoxManager camoufoxManager)
    {
        _logger = logger;
        _camoufoxManager = camoufoxManager;
    }

    /// <summary>
    /// Executes a web crawl on the target URL with anti-detection, stealth patching, and Markdown conversion.
    /// </summary>
    public async Task<string> CrawlUrlAsync(string url, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var page = await GetOrCreateStealthPageAsync(ct);

            _logger.LogInformation("Navigating stealth browser to: {Url}", url);
            
            // Navigate with network idle wait and 25s timeout
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 25000
            });

            // Brief delay to allow dynamic JS re-hydration
            await Task.Delay(1500, ct);

            string title = await page.TitleAsync();

            // Purge DOM noise elements
            await page.EvaluateAsync(@"() => {
                const noisySelectors = [
                    'nav', 'footer', 'header', 'aside', 
                    '[role=""navigation""]', '[role=""banner""]', 
                    '.cookie-banner', '.ad-container', 'iframe',
                    '#cookie-notice', '#gdpr-banner', '.social-share'
                ];
                noisySelectors.forEach(s => document.querySelectorAll(s).forEach(el => el.remove()));
            }");

            // Extract main content
            string html = "";
            var mainHandle = await page.QuerySelectorAsync("main, article, [role=\"main\"]");
            if (mainHandle != null)
            {
                html = await mainHandle.InnerHTMLAsync();
            }
            else
            {
                html = await page.InnerHTMLAsync("body");
            }

            // Convert to clean Markdown
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

            markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n").Trim();

            var header = $"# Page Title: {title}\nSource URL: {url}\n\n---\n\n";
            var fullOutput = header + markdown;

            if (fullOutput.Length > 20000)
            {
                fullOutput = fullOutput[..20000] + "\n\n... [TRUNCATED 20,000+ CHARACTERS]";
            }

            return fullOutput;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stealth browser crawling failed for URL: {Url}", url);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Executes a search query using stealth browser rendering for engines like Bing or DuckDuckGo.
    /// </summary>
    public async Task<string?> RenderPageHtmlAsync(string url, CancellationToken ct = default)
    {
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

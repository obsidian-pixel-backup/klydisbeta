using System;
using System.IO;
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
                    _logger.LogInformation("Launching Playwright with stealth Camoufox engine binary at: {Path}", camoufoxPath);
                    contextOptions.ExecutablePath = camoufoxPath;
                    _persistentContext = await _playwright.Firefox.LaunchPersistentContextAsync(profileDir, contextOptions);
                }
                else
                {
                    _logger.LogInformation("Camoufox binary unavailable. Launching standard Playwright Chromium with stealth patches.");
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

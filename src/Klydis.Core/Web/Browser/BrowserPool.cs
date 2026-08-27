using System.Runtime.InteropServices;
using System.Text.Json;
using Klydis.Core.Chat;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Klydis.Core.Web.Browser;

/// <summary>
/// Manages a pool of browser instances, isolated contexts, and concurrency limits.
/// Provides isolated browser contexts per crawl to prevent cross-site fingerprint and state contamination.
/// </summary>
public sealed class BrowserPool : IAsyncDisposable
{
    private readonly CamoufoxManager _camoufoxManager;
    private readonly BrowserHealthMonitor _healthMonitor;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _concurrencyLimit;
    private readonly SemaphoreSlim _poolLock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
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

    public BrowserHealthMonitor HealthMonitor => _healthMonitor;

    public BrowserPool(
        CamoufoxManager camoufoxManager,
        int maxConcurrentCrawls = 4,
        BrowserHealthMonitor? healthMonitor = null,
        ILogger? logger = null)
    {
        _camoufoxManager = camoufoxManager;
        _healthMonitor = healthMonitor ?? new BrowserHealthMonitor(logger);
        _concurrencyLimit = new SemaphoreSlim(Math.Max(1, maxConcurrentCrawls), Math.Max(1, maxConcurrentCrawls));
        _logger = logger;
    }

    /// <summary>
    /// Creates a fresh, isolated browser session with request routing interception and SSRF protection attached.
    /// </summary>
    public async Task<BrowserSession> CreateSessionAsync(
        IWebSecurityPolicy securityPolicy,
        BrowserResourceBudget? budget = null,
        CancellationToken ct = default)
    {
        await _concurrencyLimit.WaitAsync(ct).ConfigureAwait(false);
        var lease = new ConcurrencyLease(_concurrencyLimit);

        try
        {
            var browser = await GetOrInitializeBrowserAsync(ct).ConfigureAwait(false);
            var randomAgent = DefaultUserAgents[Random.Shared.Next(DefaultUserAgents.Length)];

            var contextOptions = new BrowserNewContextOptions
            {
                UserAgent = randomAgent,
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                Locale = "en-US",
                TimezoneId = "America/New_York",
                ServiceWorkers = ServiceWorkerPolicy.Block // Block service workers for request routing security
            };

            var context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);

            // Attach request interception and SSRF guard
            var networkPolicy = new BrowserNetworkPolicy(securityPolicy, budget, _logger);
            await networkPolicy.AttachAsync(context).ConfigureAwait(false);

            var page = await context.NewPageAsync().ConfigureAwait(false);

            try
            {
                await page.SetViewportSizeAsync(1920, 1080).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Note: Page viewport sizing fallback used.");
            }

            // Apply stealth init patches
            try
            {
                await page.AddInitScriptAsync(@"() => {
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                    Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                    window.chrome = { runtime: {} };
                }").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to apply custom stealth init script.");
            }

            return new BrowserSession(context, page, networkPolicy, lease);
        }
        catch (Exception ex)
        {
            lease.Dispose();
            _healthMonitor.RecordNavigationFailure(ex);
            throw;
        }
    }

    private async Task<IBrowser> GetOrInitializeBrowserAsync(CancellationToken ct)
    {
        if (_browser != null && !_healthMonitor.NeedsRestart() && _browser.IsConnected)
        {
            return _browser;
        }

        await _poolLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_browser != null && !_healthMonitor.NeedsRestart() && _browser.IsConnected)
            {
                return _browser;
            }

            if (_browser != null)
            {
                try { await _browser.CloseAsync().ConfigureAwait(false); } catch { }
                try { await _browser.DisposeAsync().ConfigureAwait(false); } catch { }
                _browser = null;
            }

            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);

            var camoufoxPath = await _camoufoxManager.GetExecutablePathAsync(ct).ConfigureAwait(false);
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-infobars",
                    "--window-size=1920,1080"
                }
            };

            if (!string.IsNullOrEmpty(camoufoxPath) && File.Exists(camoufoxPath))
            {
                try
                {
                    _logger?.LogInformation("Launching Playwright with stealth Camoufox engine binary at: {Path}", camoufoxPath);
                    launchOptions.ExecutablePath = camoufoxPath;
                    _browser = await _playwright.Firefox.LaunchAsync(launchOptions).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Camoufox launch failed; falling back to Chromium.");
                    launchOptions.ExecutablePath = null;
                }
            }

            if (_browser == null)
            {
                await EnsureChromiumInstalledAsync().ConfigureAwait(false);
                var fullChromiumPath = GetFullChromiumExecutablePath();
                if (!string.IsNullOrEmpty(fullChromiumPath))
                {
                    _logger?.LogInformation("Launching full Chromium at: {Path}", fullChromiumPath);
                    launchOptions.ExecutablePath = fullChromiumPath;
                }

                _browser = await _playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
            }

            return _browser;
        }
        finally
        {
            _poolLock.Release();
        }
    }

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
        catch { }

        return null;
    }

    internal static string? GetRequiredChromiumRevision(string driverBaseDir)
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
        catch { }

        return null;
    }

    private async Task EnsureChromiumInstalledAsync()
    {
        if (_installSucceeded || _installAttempted) return;
        if (Environment.GetEnvironmentVariable("KLYDIS_DISABLE_PLAYWRIGHT_AUTOINSTALL") == "1") return;

        await _installLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_installSucceeded || _installAttempted) return;

            var msPlaywrightDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
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
            _logger?.LogInformation("Playwright Chromium binaries not found. Auto-installing...");

            if (Directory.Exists(Path.Combine(baseDir, ".playwright")))
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", baseDir);
            }

            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" })).ConfigureAwait(false);
            _installSucceeded = exitCode == 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Playwright Chromium auto-install failed.");
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

        if (_browser != null)
        {
            try { await _browser.CloseAsync().ConfigureAwait(false); } catch { }
            try { await _browser.DisposeAsync().ConfigureAwait(false); } catch { }
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _concurrencyLimit.Dispose();
        _poolLock.Dispose();

        GC.SuppressFinalize(this);
    }

    private sealed class ConcurrencyLease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public ConcurrencyLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _semaphore, null);
            sem?.Release();
        }
    }
}

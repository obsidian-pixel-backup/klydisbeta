using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Chat;

/// <summary>
/// Manages the stealth Camoufox browser binary lifecycle, path resolution, and auto-download.
/// </summary>
public class CamoufoxManager
{
    private readonly ILogger<CamoufoxManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _camoufoxDir;

    private const string DefaultGitHubReleaseUrl = "https://github.com/daijro/camoufox/releases/download/v152.0.4-beta.28/camoufox-152.0.4-beta.28-win.x86_64.zip";
    private const string LatestReleasesApiUrl = "https://api.github.com/repos/daijro/camoufox/releases/latest";

    public static string ReleaseUrl => Environment.GetEnvironmentVariable("CAMOUFOX_RELEASE_URL") ?? DefaultGitHubReleaseUrl;

    public CamoufoxManager(ILogger<CamoufoxManager> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _camoufoxDir = Path.Combine(userProfile, ".klydis", "camoufox");
    }

    /// <summary>
    /// Ensures Camoufox binary is available and returns the path to the executable if present.
    /// Returns null if unavailable and auto-download failed or was cancelled.
    /// </summary>
    public async Task<string?> GetExecutablePathAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Check environment variable override
            var envPath = Environment.GetEnvironmentVariable("CAMOUFOX_PATH");
            if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            {
                _logger.LogInformation("Using Camoufox executable from environment variable: {Path}", envPath);
                return envPath;
            }

            // 2. Check local .klydis directory
            if (!Directory.Exists(_camoufoxDir))
            {
                Directory.CreateDirectory(_camoufoxDir);
            }

            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "camoufox.exe" : "camoufox";
            var localExePath = Path.Combine(_camoufoxDir, exeName);

            if (File.Exists(localExePath))
            {
                return localExePath;
            }

            // Also check subdirectories (e.g. camoufox-win64/camoufox.exe)
            var subDirExes = Directory.GetFiles(_camoufoxDir, exeName, SearchOption.AllDirectories);
            if (subDirExes.Length > 0)
            {
                return subDirExes[0];
            }

            // 3. Auto-download Camoufox release if not found
            _logger.LogInformation("Camoufox binary not found locally. Attempting automatic download to {Dir}...", _camoufoxDir);
            var downloadedPath = await DownloadAndExtractAsync(localExePath, ct);
            return downloadedPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve or download Camoufox binary. Will fall back to standard Playwright browser.");
            return null;
        }
    }

    private async Task<string?> DownloadAndExtractAsync(string targetExePath, CancellationToken ct)
    {
        var zipPath = Path.Combine(_camoufoxDir, "camoufox-download.zip");
        try
        {
            var releaseUrl = await ResolveReleaseUrlAsync(ct);
            _logger.LogInformation("Downloading Camoufox from: {Url}", releaseUrl);

            using var response = await _httpClient.GetAsync(releaseUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Camoufox download request returned HTTP status {StatusCode}", response.StatusCode);
                return null;
            }

            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, ct);
            }

            _logger.LogInformation("Camoufox archive downloaded successfully. Extracting...");
            ZipFile.ExtractToDirectory(zipPath, _camoufoxDir, overwriteFiles: true);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            var exeName = Path.GetFileName(targetExePath);
            var foundExes = Directory.GetFiles(_camoufoxDir, exeName, SearchOption.AllDirectories);
            if (foundExes.Length > 0)
            {
                _logger.LogInformation("Camoufox binary extracted to: {Path}", foundExes[0]);
                return foundExes[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download/Extraction of Camoufox binary failed.");
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the download URL for Camoufox. Prefers the CAMOUFOX_RELEASE_URL environment
    /// override, otherwise queries the GitHub latest-release API for the current Windows asset
    /// (the hardcoded fallback is only a last resort).
    /// </summary>
    private async Task<string> ResolveReleaseUrlAsync(CancellationToken ct)
    {
        var envUrl = Environment.GetEnvironmentVariable("CAMOUFOX_RELEASE_URL");
        if (!string.IsNullOrWhiteSpace(envUrl)) return envUrl;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleasesApiUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "KlydisAssistant/1.0");

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameElement) ||
                            !asset.TryGetProperty("browser_download_url", out var urlElement))
                        {
                            continue;
                        }

                        var name = nameElement.GetString();
                        if (name != null &&
                            (name.EndsWith("win.x86_64.zip", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith("win64.zip", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.LogInformation("Resolved Camoufox release asset: {Name}", name);
                            return urlElement.GetString()!;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Camoufox latest release from GitHub API.");
        }

        return DefaultGitHubReleaseUrl;
    }
}

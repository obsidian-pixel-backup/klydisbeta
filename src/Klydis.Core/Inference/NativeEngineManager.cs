using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference;

/// <summary>
/// Manages local native llama.dll execution engines and override directories in %USERPROFILE%\.klydis\native\.
/// Ensures in-process GGUF inference can utilize updated native C++ builds without external 3rd-party services.
/// </summary>
public static class NativeEngineManager
{
    private static readonly string KlydisHomeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".klydis"
    );

    /// <summary>
    /// Path to user native DLL override folder (%USERPROFILE%\.klydis\native).
    /// </summary>
    public static string CustomNativeDirectory => Path.Combine(KlydisHomeDir, "native");

    /// <summary>
    /// Checks whether custom/updated native llama.dll binaries are present in the override folder.
    /// </summary>
    public static bool HasCustomNativeEngine()
    {
        try
        {
            if (!Directory.Exists(CustomNativeDirectory))
                return false;

            string nativeDll = Path.Combine(CustomNativeDirectory, "llama.dll");
            return File.Exists(nativeDll) && new FileInfo(nativeDll).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deploys custom native binaries from %USERPROFILE%\.klydis\native\ to the target application directory
    /// and all runtime subfolders if available.
    /// </summary>
    /// <param name="targetDirectory">Target directory (defaults to AppDomain BaseDirectory).</param>
    /// <param name="logger">Optional logger for telemetry.</param>
    /// <returns>Number of native DLL files copied.</returns>
    public static int SyncCustomNativeEngine(string? targetDirectory = null, ILogger? logger = null)
    {
        targetDirectory ??= AppDomain.CurrentDomain.BaseDirectory;
        int copiedCount = 0;

        try
        {
            if (!HasCustomNativeEngine())
            {
                logger?.LogDebug("No custom native engine override found in {CustomNativeDirectory}", CustomNativeDirectory);
                return 0;
            }

            logger?.LogInformation("Deploying custom in-process native engine from {CustomNativeDirectory} to {TargetDirectory}", CustomNativeDirectory, targetDirectory);

            var targets = new List<string> { targetDirectory };
            var runtimeDir = Path.Combine(targetDirectory, "runtimes", "win-x64", "native");
            if (Directory.Exists(runtimeDir))
            {
                targets.Add(runtimeDir);
                foreach (var subDir in Directory.GetDirectories(runtimeDir))
                {
                    targets.Add(subDir);
                }
            }

            foreach (var target in targets)
            {
                foreach (var file in Directory.GetFiles(CustomNativeDirectory, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(file);
                    var destPath = Path.Combine(target, fileName);

                    try
                    {
                        var srcInfo = new FileInfo(file);
                        var destInfo = File.Exists(destPath) ? new FileInfo(destPath) : null;

                        if (destInfo == null || destInfo.Length != srcInfo.Length || destInfo.LastWriteTimeUtc < srcInfo.LastWriteTimeUtc)
                        {
                            File.Copy(file, destPath, overwrite: true);
                            copiedCount++;
                            logger?.LogInformation("Copied custom native binary: {FileName} to {Target}", fileName, target);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Could not overwrite native file {FileName} in {Target} (file may already be loaded in memory)", fileName, target);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error synchronizing custom native engine binaries");
        }

        return copiedCount;
    }

    /// <summary>
    /// Ensures the custom native directory structure exists.
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        try
        {
            if (!Directory.Exists(CustomNativeDirectory))
            {
                Directory.CreateDirectory(CustomNativeDirectory);
            }
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// Resolves direct release asset download URLs dynamically from GitHub API or release redirects.
    /// </summary>
    private static async Task<List<string>> ResolveLatestReleaseDownloadUrlsAsync(HttpClient httpClient, ILogger? logger)
    {
        var urls = new List<string>();
        try
        {
        var customReposEnv = Environment.GetEnvironmentVariable("LLAMA_CPP_GITHUB_REPOS");
        string[] repos = !string.IsNullOrWhiteSpace(customReposEnv)
            ? customReposEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[]
            {
                "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest",
                "https://api.github.com/repos/ggerganov/llama.cpp/releases/latest"
            };

            foreach (var repoUrl in repos)
            {
                try
                {
                    logger?.LogInformation("Querying GitHub release API at {Url}...", repoUrl);
                    using var request = new HttpRequestMessage(HttpMethod.Get, repoUrl);
                    
                    var response = await httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger?.LogWarning("GitHub API returned HTTP {StatusCode} for {Url}", response.StatusCode, repoUrl);
                        continue;
                    }

                    var jsonStr = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonStr);
                    if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    List<(string name, string url)> candidates = new();
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var nameProp) && 
                            asset.TryGetProperty("browser_download_url", out var urlProp))
                        {
                            var name = nameProp.GetString() ?? "";
                            var url = urlProp.GetString() ?? "";
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                                (name.Contains("x64", StringComparison.OrdinalIgnoreCase) || name.Contains("64", StringComparison.OrdinalIgnoreCase)))
                            {
                                candidates.Add((name, url));
                            }
                        }
                    }

                    if (candidates.Count == 0) continue;

                    var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    bool hasCuda = File.Exists(Path.Combine(system32, "nvcuda.dll"));

                    // Include CUDA runtime helper zip if present
                    var cudartAsset = candidates.FirstOrDefault(a => a.name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(cudartAsset.url))
                    {
                        urls.Add(cudartAsset.url);
                    }

                    if (hasCuda)
                    {
                        var cudaAsset = candidates.FirstOrDefault(a => a.name.Contains("cuda", StringComparison.OrdinalIgnoreCase) && !a.name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(cudaAsset.url))
                        {
                            logger?.LogInformation("Selected CUDA release asset: {Name}", cudaAsset.name);
                            urls.Add(cudaAsset.url);
                            return urls;
                        }
                    }

                    var vulkanAsset = candidates.FirstOrDefault(a => a.name.Contains("vulkan", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(vulkanAsset.url))
                    {
                        logger?.LogInformation("Selected Vulkan release asset: {Name}", vulkanAsset.name);
                        urls.Add(vulkanAsset.url);
                        return urls;
                    }

                    var cpuAsset = candidates.FirstOrDefault(a => a.name.Contains("cpu", StringComparison.OrdinalIgnoreCase) || a.name.Contains("avx2", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(cpuAsset.url))
                    {
                        logger?.LogInformation("Selected CPU release asset: {Name}", cpuAsset.name);
                        urls.Add(cpuAsset.url);
                        return urls;
                    }

                    var fallbackAsset = candidates.First();
                    logger?.LogInformation("Selected fallback release asset: {Name}", fallbackAsset.name);
                    urls.Add(fallbackAsset.url);
                    return urls;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed querying GitHub release endpoint {Url}", repoUrl);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to resolve latest release URL from GitHub API.");
        }

        return urls;
    }

    /// <summary>
    /// Downloads and extracts updated native llama.dll binaries into %USERPROFILE%\.klydis\native\.
    /// </summary>
    /// <param name="downloadUrl">Optional custom download URL for zip package containing llama.dll.</param>
    /// <param name="logger">Optional logger for telemetry.</param>
    /// <returns>True if downloaded and deployed successfully.</returns>
    public static async Task<bool> DownloadLatestNativeEngineAsync(string? downloadUrl = null, ILogger? logger = null)
    {
        EnsureDirectoriesExist();

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KlydisApp/1.0");

        List<string> urlsToDownload = new();
        downloadUrl ??= Environment.GetEnvironmentVariable("LLAMA_CPP_RELEASE_URL");
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            urlsToDownload.Add(downloadUrl);
        }
        else
        {
            urlsToDownload = await ResolveLatestReleaseDownloadUrlsAsync(httpClient, logger);
        }

        if (urlsToDownload.Count == 0)
        {
            logger?.LogError("Could not resolve any valid llama.cpp release download URLs.");
            return false;
        }

        int totalExtracted = 0;

        foreach (var url in urlsToDownload)
        {
            try
            {
                logger?.LogInformation("Downloading updated native engine package from {Url}...", url);

                var tempZipPath = Path.Combine(Path.GetTempPath(), $"llama_native_{Guid.NewGuid():N}.zip");
                var zipBytes = await httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tempZipPath, zipBytes);

                var tempExtractDir = Path.Combine(Path.GetTempPath(), $"llama_extracted_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempExtractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, tempExtractDir);

                foreach (var file in Directory.GetFiles(tempExtractDir, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".dll" || ext == ".exe")
                    {
                        var dest = Path.Combine(CustomNativeDirectory, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                        totalExtracted++;
                    }
                }

                try { File.Delete(tempZipPath); } catch { }
                try { Directory.Delete(tempExtractDir, recursive: true); } catch { }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed downloading or extracting package from {Url}", url);
            }
        }

        if (totalExtracted > 0)
        {
            int deployed = SyncCustomNativeEngine(logger: logger);
            logger?.LogInformation("Successfully deployed {Count} updated native binaries to {Dir}", totalExtracted, CustomNativeDirectory);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Restarts the application by launching a new process and exiting the current one.
    /// Used after downloading updated native engine DLLs that require a fresh process
    /// to take effect (the in-memory llama.dll is locked by the running process).
    /// </summary>
    /// <param name="logger">Optional logger for telemetry.</param>
    public static void RestartApplication(ILogger? logger = null)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                logger?.LogInformation("Restarting application to apply updated native engine: {ExePath}", exePath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
            else
            {
                logger?.LogError("Cannot determine executable path for restart.");
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to start new process for restart.");
        }

        Environment.Exit(0);
    }
}

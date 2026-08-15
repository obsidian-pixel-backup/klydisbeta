using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference;

/// <summary>
/// Manages local native llama.dll execution engines and override directories in %USERPROFILE%\.klydis\native\.
/// Ensures in-process GGUF inference can utilize updated native C++ builds without external 3rd-party services.
/// </summary>
public static class NativeEngineManager
{
    private static int _nativeConfigInitialized = 0;
    private static int _nativeBackendsLoaded = 0;

    /// <summary>
    /// Loads all ggml backend plugins (CPU/CUDA/Vulkan) from the deployed native directory.
    /// llama.cpp builds after the "backends as plugins" refactor (b9181+) require backends to
    /// be loaded explicitly via ggml_backend_load_all(); the bundled backend auto-loaded them
    /// at init. Must run after the native DLLs are deployed and before the first model load.
    /// </summary>
    public static void LoadNativeBackends(ILogger? logger = null)
    {
        if (Interlocked.Exchange(ref _nativeBackendsLoaded, 1) == 1) return;
        try
        {
            NativeBackendLoadAll();
            logger?.LogInformation("ggml backend plugins loaded.");
        }
        catch (EntryPointNotFoundException)
        {
            // Very old llama.cpp builds do not export ggml_backend_load_all; they auto-load backends.
            logger?.LogDebug("ggml_backend_load_all not exported by the native library; skipping explicit backend load.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to explicitly load ggml backend plugins; relying on native defaults.");
        }
    }

    [System.Runtime.InteropServices.DllImport("ggml.dll", EntryPoint = "ggml_backend_load_all")]
    private static extern void NativeBackendLoadAll();

    /// <summary>
    /// Guarantees that LLamaSharp NativeLibraryConfig is initialized safely exactly once process-wide.
    /// Idempotent and thread-safe across parallel test runs and application execution.
    /// </summary>
    public static void EnsureNativeLibraryConfigured(bool enableCuda = true, bool enableVulkan = false)
    {
        if (Interlocked.CompareExchange(ref _nativeConfigInitialized, 1, 0) == 0)
        {
            try
            {
                EnsureCudaRuntimesSynced();

                // If a custom engine is installed, deploy it into EVERY location the wrapper's
                // resolver can reach (app top-level, runtimes/win-x64/native root, and every
                // subdir — avx/avx2/avx512/noavx/cuda12/cuda13/vulkan). This runs again here
                // right before resolution so a stale bundled engine re-copied by a rebuild can
                // never win the search: the resolver combines its relative paths
                // (e.g. runtimes/win-x64/native/cuda12/llama.dll) with each search directory,
                // so every candidate must hold the ABI-matched custom binary. Idempotent: the
                // sync only copies when the destination length differs (or is missing).
                if (HasCustomNativeEngine())
                {
                    try { SyncCustomNativeEngine(logger: null); } catch { /* best effort */ }
                }

                var appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                var cuda12Dir = Path.Combine(appBaseDir, "runtimes", "win-x64", "native", "cuda12");
                var cuda13Dir = Path.Combine(appBaseDir, "runtimes", "win-x64", "native", "cuda13");

                var config = LLama.Native.NativeLibraryConfig.All
                    .WithCuda(enableCuda)
                    .WithVulkan(enableVulkan)
                    .WithLogCallback((level, message) => {
                        // Rotating log in %LOCALAPPDATA%\Klydis\logs — never blocks or throws.
                        Klydis.Core.Diagnostics.KlydisLog.AppendNativeLog($"[{level}] {message}{Environment.NewLine}");
                    });

                // Make the wrapper search the custom native engine directory FIRST whenever one
                // is installed. The NuGet backend packages drop their own (older ABI) llama.dll
                // into the app output (top-level AND runtimes subdirs), and `dotnet run`/
                // rebuilds re-copy those bundled binaries over any synced custom engine. If the
                // wrapper then resolves the bundled engine — which has an older ABI than the
                // custom one — every managed struct that gained fields since (e.g.
                // n_outputs_max_per_seq) is misaligned and native fails with "Unsupported ctx
                // type". Searching the custom directory first (plus the sync in
                // StartupSequence) guarantees the ABI-matched engine wins.
                if (HasCustomNativeEngine())
                {
                    config = config.WithSearchDirectory(CustomNativeDirectory);
                }

                config
                    .WithSearchDirectory(appBaseDir)
                    .WithSearchDirectory(cuda12Dir)
                    .WithSearchDirectory(cuda13Dir);
            }
            catch (InvalidOperationException)
            {
                // Native library was already loaded by prior native operations in the process.
            }
        }
    }

    private static readonly string KlydisHomeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".klydis"
    );

    /// <summary>
    /// Path to user native DLL override folder (%USERPROFILE%\.klydis\native).
    /// </summary>
    public static string CustomNativeDirectory => Path.Combine(KlydisHomeDir, "native");

    /// <summary>
    /// Path to a small JSON marker recording which llama.cpp release is currently installed as
    /// the custom native engine, and when we last checked for a newer release.
    /// </summary>
    private static string NativeVersionFilePath => Path.Combine(CustomNativeDirectory, "version.json");

    /// <summary>
    /// How often the app re-checks GitHub for a newer llama.cpp release (once per day).
    /// </summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Returns the llama.cpp release tag currently installed in the custom native directory,
    /// or null when unknown (e.g. the user placed DLLs there manually).
    /// </summary>
    public static string? GetInstalledNativeTag()
    {
        try
        {
            if (!File.Exists(NativeVersionFilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(NativeVersionFilePath));
            return doc.RootElement.TryGetProperty("tag", out var tag) ? tag.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Records the llama.cpp release tag currently installed as the custom native engine.
    /// A fresh install counts as an update check, so the first online re-check happens a day later.
    /// </summary>
    public static void SaveInstalledNativeTag(string tag)
    {
        try
        {
            EnsureDirectoriesExist();
            var payload = JsonSerializer.Serialize(new
            {
                tag,
                installedAtUtc = DateTime.UtcNow.ToString("O"),
                lastCheckUtc = DateTime.UtcNow.ToString("O")
            });
            File.WriteAllText(NativeVersionFilePath, payload);
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// True when the daily online update check is due (no record yet, or last check older than 24h).
    /// </summary>
    private static bool IsUpdateCheckDue()
    {
        try
        {
            if (!File.Exists(NativeVersionFilePath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(NativeVersionFilePath));
            if (!doc.RootElement.TryGetProperty("lastCheckUtc", out var prop) ||
                !DateTime.TryParse(prop.GetString(), out var lastCheck))
            {
                return true;
            }
            return DateTime.UtcNow - lastCheck >= UpdateCheckInterval;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Marks the online update check as performed now (preserving the installed tag), so the next
    /// re-check is throttled until a day from now. Only called after a successful release lookup.
    /// </summary>
    private static void RecordUpdateCheck()
    {
        try
        {
            string? tag = GetInstalledNativeTag();
            var payload = JsonSerializer.Serialize(new
            {
                tag,
                installedAtUtc = DateTime.UtcNow.ToString("O"),
                lastCheckUtc = DateTime.UtcNow.ToString("O")
            });
            File.WriteAllText(NativeVersionFilePath, payload);
        }
        catch
        {
            // Non-critical
        }
    }

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

                        // ABI-critical binaries (llama.dll / ggml.dll) MUST always be replaced
                        // with the custom build: the NuGet backend packages drop their own
                        // (older) copies into the app output on every build, and a stale one
                        // misaligns the managed structs ("Unsupported ctx type"). Timestamps
                        // are unreliable here (package restores can be newer than the custom
                        // engine), so compare content length when deciding for these.
                        bool isAbiCritical = string.Equals(fileName, "llama.dll", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(fileName, "ggml.dll", StringComparison.OrdinalIgnoreCase);
                        bool shouldCopy = isAbiCritical
                            ? destInfo == null || destInfo.Length != srcInfo.Length
                            : destInfo == null || destInfo.Length != srcInfo.Length || destInfo.LastWriteTimeUtc < srcInfo.LastWriteTimeUtc;

                        if (shouldCopy)
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
    /// Ensures CUDA runtime subdirectories (including cuda13 mapped from cuda12 if needed)
    /// and root app directories are synced with CUDA native binaries.
    /// </summary>
    public static void EnsureCudaRuntimesSynced(string? targetDirectory = null, ILogger? logger = null)
    {
        try
        {
            targetDirectory ??= AppDomain.CurrentDomain.BaseDirectory;
            var nativeDir = Path.Combine(targetDirectory, "runtimes", "win-x64", "native");

            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            bool isCudaSupported = File.Exists(Path.Combine(system32, "nvcuda.dll"));

            if (!isCudaSupported || !Directory.Exists(nativeDir)) return;

            var cuda13Path = Path.Combine(nativeDir, "cuda13");
            var cuda12Path = Path.Combine(nativeDir, "cuda12");

            if (!Directory.Exists(cuda13Path) && Directory.Exists(cuda12Path))
            {
                try
                {
                    Directory.CreateDirectory(cuda13Path);
                    foreach (var file in Directory.GetFiles(cuda12Path))
                    {
                        File.Copy(file, Path.Combine(cuda13Path, Path.GetFileName(file)), overwrite: true);
                    }
                    logger?.LogInformation("Created and populated cuda13 native directory from cuda12.");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed replicating cuda12 binaries to cuda13 directory.");
                }
            }

            string sourceSubFolder = Directory.Exists(cuda13Path) ? "cuda13" : (Directory.Exists(cuda12Path) ? "cuda12" : "");
            if (!string.IsNullOrEmpty(sourceSubFolder))
            {
                var sourcePath = Path.Combine(nativeDir, sourceSubFolder);
                foreach (var file in Directory.GetFiles(sourcePath))
                {
                    var fileName = Path.GetFileName(file);
                    var destFile = Path.Combine(targetDirectory, fileName);
                    try
                    {
                        var srcInfo = new FileInfo(file);
                        var destInfo = File.Exists(destFile) ? new FileInfo(destFile) : null;
                        if (destInfo == null || destInfo.Length != srcInfo.Length)
                        {
                            File.Copy(file, destFile, overwrite: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Could not sync CUDA file {File} to base directory.", fileName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error ensuring CUDA runtimes are synced.");
        }
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
    /// Returns the release tag name, the asset URLs, and each asset's published SHA-256 digest
    /// (the GitHub releases API exposes it as "sha256:&lt;hex&gt;"; absent on older responses or
    /// custom URLs). The digest is used to verify archives before they are extracted and deployed.
    /// </summary>
    private static async Task<(string? Tag, List<string> Urls, Dictionary<string, string> DigestByUrl)> ResolveLatestReleaseDownloadUrlsAsync(HttpClient httpClient, ILogger? logger, CancellationToken ct = default)
    {
        var urls = new List<string>();
        var digestByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? tag = null;
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
                    request.Headers.TryAddWithoutValidation("User-Agent", "KlydisApp/1.0");
                    
                    var response = await httpClient.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger?.LogWarning("GitHub API returned HTTP {StatusCode} for {Url}", response.StatusCode, repoUrl);
                        continue;
                    }

                    var jsonStr = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonStr);
                    if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
                    {
                        tag = tagProp.GetString();
                    }
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
                            // Only x64 Windows assets: the loose "64" check wrongly matches ARM64
                            // zips (e.g. ...-win-cpu-arm64.zip), whose llama.dll would then overwrite
                            // the x64 one and produce a BadImageFormatException at load time.
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                                name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                            {
                                candidates.Add((name, url));
                                // GitHub publishes each asset's SHA-256 as "sha256:<hex>"; keep it
                                // keyed by download URL so the downloader can verify before deploying.
                                if (asset.TryGetProperty("digest", out var digestProp) &&
                                    digestProp.ValueKind == JsonValueKind.String)
                                {
                                    string digest = (digestProp.GetString() ?? string.Empty).Trim();
                                    const string Sha256Prefix = "sha256:";
                                    if (digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
                                    {
                                        digest = digest.Substring(Sha256Prefix.Length);
                                    }
                                    if (!string.IsNullOrWhiteSpace(digest))
                                    {
                                        digestByUrl[url] = digest;
                                    }
                                }
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

                            // Also fetch the CPU build so inference still works if the CUDA
                            // runtime cannot initialize on this machine (llama.cpp falls back).
                            var cpuFallback = candidates.FirstOrDefault(a => a.name.Contains("cpu", StringComparison.OrdinalIgnoreCase));
                            if (!string.IsNullOrEmpty(cpuFallback.url) && cpuFallback.url != cudaAsset.url)
                            {
                                logger?.LogInformation("Also fetching CPU release asset as fallback: {Name}", cpuFallback.name);
                                urls.Add(cpuFallback.url);
                            }
                            return (tag, urls, digestByUrl);
                        }
                    }

                    var vulkanAsset = candidates.FirstOrDefault(a => a.name.Contains("vulkan", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(vulkanAsset.url))
                    {
                        logger?.LogInformation("Selected Vulkan release asset: {Name}", vulkanAsset.name);
                        urls.Add(vulkanAsset.url);
                        return (tag, urls, digestByUrl);
                    }

                    var cpuAsset = candidates.FirstOrDefault(a => a.name.Contains("cpu", StringComparison.OrdinalIgnoreCase) || a.name.Contains("avx2", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(cpuAsset.url))
                    {
                        logger?.LogInformation("Selected CPU release asset: {Name}", cpuAsset.name);
                        urls.Add(cpuAsset.url);
                        return (tag, urls, digestByUrl);
                    }

                    var fallbackAsset = candidates.First();
                    logger?.LogInformation("Selected fallback release asset: {Name}", fallbackAsset.name);
                    urls.Add(fallbackAsset.url);
                    return (tag, urls, digestByUrl);
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

        return (tag, urls, digestByUrl);
    }

    /// <summary>
    /// Computes the lowercase-hex SHA-256 of a file.
    /// </summary>
    private static string ComputeFileSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(hash.ComputeHash(stream));
    }

    /// <summary>
    /// Downloads and extracts updated native llama.dll binaries into %USERPROFILE%\.klydis\native\.
    /// </summary>
    /// <param name="downloadUrl">Optional custom download URL for zip package containing llama.dll.</param>
    /// <param name="logger">Optional logger for telemetry.</param>
    /// <returns>True if downloaded and deployed successfully.</returns>
    public static async Task<bool> DownloadLatestNativeEngineAsync(string? downloadUrl = null, ILogger? logger = null, CancellationToken ct = default, Action<string>? statusCallback = null)
    {
        EnsureDirectoriesExist();

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KlydisApp/1.0");
        // A llama.cpp release zip is hundreds of MB — cap the whole download so a stalled
        // connection can never hang the app (the startup watchdog also cancels via ct).
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        List<string> urlsToDownload = new();
        var digestByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? resolvedTag = null;
        downloadUrl ??= Environment.GetEnvironmentVariable("LLAMA_CPP_RELEASE_URL");
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            urlsToDownload.Add(downloadUrl);
        }
        else
        {
            (resolvedTag, urlsToDownload, digestByUrl) = await ResolveLatestReleaseDownloadUrlsAsync(httpClient, logger, ct);
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
                statusCallback?.Invoke("Downloading updated native engine — this can take a few minutes…");

                // Stream to disk instead of buffering the whole zip in RAM: llama.cpp release
                // zips are hundreds of MB, and GetByteArrayAsync would hold all of that in
                // memory at once. ResponseHeadersRead + CopyToAsync keeps peak memory flat.
                var tempZipPath = Path.Combine(Path.GetTempPath(), $"llama_native_{Guid.NewGuid():N}.zip");
                using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await contentStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                    }
                }

                // Security: verify the archive against the GitHub-published SHA-256 digest BEFORE
                // extracting anything. The extracted .dll/.exe files are executed in-process, so
                // an unverified download (MITM, compromised asset, truncated transfer) is arbitrary
                // code execution. Custom URLs (env override) have no published digest and are
                // logged as unverified rather than silently trusted.
                if (digestByUrl.TryGetValue(url, out var expectedDigest) && !string.IsNullOrWhiteSpace(expectedDigest))
                {
                    string actualDigest = ComputeFileSha256(tempZipPath);
                    if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.LogError("SHA-256 mismatch for {Url}: expected {Expected}, got {Actual}. Refusing to deploy the package.", url, expectedDigest, actualDigest);
                        try { File.Delete(tempZipPath); } catch { }
                        continue;
                    }
                    logger?.LogInformation("Verified SHA-256 digest for {Url}.", url);
                }
                else
                {
                    logger?.LogWarning("No SHA-256 digest available for {Url}; skipping integrity verification.", url);
                }

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
            if (!string.IsNullOrWhiteSpace(resolvedTag))
            {
                SaveInstalledNativeTag(resolvedTag);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Auto-updates the native llama.cpp engine to the latest release when the current one is
    /// missing or stale (the vendored LLamaSharp wrapper matches the CURRENT llama.cpp ABI, so
    /// the bundled backend alone is not enough). Returns true when a new native engine was
    /// downloaded and synced (caller should restart the app), false when nothing was needed.
    /// </summary>
    /// <param name="logger">Optional logger for telemetry.</param>
    /// <param name="forceCheck">When true, always performs the online "is there a newer release"
    /// check. When false (default), the online re-check is throttled to once per day; a missing
    /// custom native engine is always downloaded immediately since the wrapper requires it.</param>
    public static async Task<bool> TryAutoUpdateNativeEngineAsync(ILogger? logger = null, bool forceCheck = false, CancellationToken ct = default, Action<string>? statusCallback = null)
    {
        if (!HasCustomNativeEngine())
        {
            // The patched wrapper requires the current llama.cpp ABI — install before first load.
            logger?.LogInformation("No custom native engine installed. Downloading the latest llama.cpp release...");
            return await DownloadLatestNativeEngineAsync(logger: logger, ct: ct, statusCallback: statusCallback);
        }

        if (!forceCheck && !IsUpdateCheckDue())
        {
            logger?.LogInformation("Native engine update check not yet due (checked within the last 24h).");
            return false;
        }

        try
        {
            statusCallback?.Invoke("Checking for native engine updates…");
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KlydisApp/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var (latestTag, _, _) = await ResolveLatestReleaseDownloadUrlsAsync(httpClient, logger, ct);
            if (string.IsNullOrWhiteSpace(latestTag))
            {
                return false;
            }

            // Only record the check when the lookup succeeded, so a network failure retries on the
            // next launch instead of being throttled for a day.
            RecordUpdateCheck();

            var installedTag = GetInstalledNativeTag();
            if (string.Equals(installedTag, latestTag, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogInformation("Native engine already up to date ({LatestTag}).", latestTag);
                return false;
            }

            // An installed engine with an unknown tag (e.g. the DLLs were placed manually or the
            // version record was lost) must NOT trigger a multi-hundred-MB download "just in case":
            // with a null tag the comparison below could never match, so the app would re-download
            // on every daily check — and on the very first launch it would stall the splash for
            // minutes. Record the check, log the unknown state, and keep the current engine; a
            // normal download path always writes the tag, and forceCheck still forces a real
            // comparison when the user explicitly requests one.
            if (string.IsNullOrEmpty(installedTag) && !forceCheck)
            {
                logger?.LogWarning("Native engine installed but its release tag is unknown; skipping auto-download for now (latest: {LatestTag}). Run with force-check to update.", latestTag);
                return false;
            }

            logger?.LogInformation("Newer llama.cpp release available: {LatestTag} (installed: {InstalledTag}). Downloading...", latestTag, installedTag);
            return await DownloadLatestNativeEngineAsync(logger: logger, ct: ct, statusCallback: statusCallback);
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning("Native engine update check was cancelled (startup watchdog timeout).");
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Native engine update check failed.");
            return false;
        }
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

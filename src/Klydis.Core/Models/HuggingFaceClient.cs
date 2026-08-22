using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Models;

/// <summary>
/// Represents the current state of a model download.
/// </summary>
public enum DownloadState
{
    /// <summary>Download is idle and hasn't started.</summary>
    Idle,
    /// <summary>Currently downloading data.</summary>
    Downloading,
    /// <summary>Download is paused.</summary>
    Paused,
    /// <summary>Download completed successfully.</summary>
    Completed,
    /// <summary>Download failed due to an error.</summary>
    Failed
}

/// <summary>
/// Represents information about a model repository on Hugging Face.
/// </summary>
/// <param name="RepoId">The full repository ID (e.g., 'TheBloke/Llama-2-7b-Chat-GGUF').</param>
/// <param name="Author">The author or organization name.</param>
/// <param name="ModelName">The name of the model.</param>
/// <param name="Downloads">The number of downloads.</param>
/// <param name="Likes">The number of likes.</param>
/// <param name="LastModified">The last modification date.</param>
/// <param name="Tags">Associated tags.</param>
/// <param name="Description">A short description or summary if available.</param>
public record HfModelInfo(
    string RepoId,
    string Author,
    string ModelName,
    int Downloads,
    int Likes,
    DateTimeOffset LastModified,
    string[] Tags,
    string Description,
    string PipelineTag = ""
);

/// <summary>
/// Represents information about a specific GGUF file within a model repository.
/// </summary>
/// <param name="Filename">The name of the file.</param>
/// <param name="SizeBytes">The size of the file in bytes.</param>
/// <param name="QuantType">The quantization type parsed from the filename (e.g., Q4_K_M).</param>
/// <param name="ParameterSize">The parameter size parsed from the filename (e.g., 7B, 13B).</param>
/// <param name="Sha256">The LFS SHA-256 published by the Hub for this file, used to verify
/// downloads. Empty when the API did not expose it (e.g. non-LFS files).</param>
public record HfFileInfo(
    string Filename,
    long SizeBytes,
    string QuantType,
    string ParameterSize,
    string Sha256 = ""
);

/// <summary>
/// Represents the progress of an active download.
/// </summary>
/// <param name="BytesDownloaded">Total bytes downloaded so far.</param>
/// <param name="TotalBytes">Total size of the file in bytes.</param>
/// <param name="SpeedBytesPerSecond">Current download speed in bytes per second.</param>
/// <param name="EstimatedSecondsRemaining">Estimated time remaining in seconds.</param>
/// <param name="PercentComplete">Percentage of completion (0 to 100).</param>
public record DownloadProgress(
    long BytesDownloaded,
    long TotalBytes,
    double SpeedBytesPerSecond,
    double EstimatedSecondsRemaining,
    double PercentComplete
);

/// <summary>
/// Client for interacting with the Hugging Face Hub API to search, browse, and download GGUF models.
/// </summary>
public partial class HuggingFaceClient
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadClient;
    private readonly ILogger<HuggingFaceClient> _logger;

    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    public static string BaseUrl
    {
        get
        {
            var envEndpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT");
            return !string.IsNullOrWhiteSpace(envEndpoint) ? envEndpoint.TrimEnd('/') : "https://huggingface.co";
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Timestamp, List<HfFileInfo> Files)> _modelFilesCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    [GeneratedRegex(@"(?i)\b(I?Q[0-9]_[A-Z0-9_]+|TQ[0-9]_[A-Z0-9_]+|BF16|FP16|F16|FP32|F32)\b|(?i)(I?Q[0-9]_[A-Z0-9_]+|TQ[0-9]_[A-Z0-9_]+|BF16|FP16|F16|FP32|F32)")]
    private static partial Regex QuantTypeRegex();

    [GeneratedRegex(@"(?i)(\d+(?:\.\d+)?[BT])")]
    private static partial Regex ParameterSizeRegex();

    /// <summary>
    /// Initializes a new instance of the <see cref="HuggingFaceClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="logger">The logger instance.</param>
    public HuggingFaceClient(HttpClient httpClient, ILogger<HuggingFaceClient> logger)
        : this(httpClient, CreateDownloadClient(), logger)
    {
    }

    /// <summary>
    /// Test seam: accepts a dedicated download client (e.g. backed by a stub handler) instead
    /// of the real network client used for large downloads.
    /// </summary>
    internal HuggingFaceClient(HttpClient httpClient, HttpClient downloadClient, ILogger<HuggingFaceClient> logger)
    {
        _httpClient = httpClient;
        _downloadClient = downloadClient;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Klydis/1.0");
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("Klydis/1.0");

        var token = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            var auth = new AuthenticationHeaderValue("Bearer", token);
            _httpClient.DefaultRequestHeaders.Authorization = auth;
            // Mirror the bearer token onto the dedicated download client so gated models work.
            _downloadClient.DefaultRequestHeaders.Authorization = auth;
        }
    }

    /// <summary>
    /// Searches the Hugging Face API for GGUF models.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="limit">The maximum number of results to return.</param>
    /// <param name="sort">The sort parameter (downloads, likes, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of model information records.</returns>
    public async Task<List<HfModelInfo>> SearchModelsAsync(string query, int limit = 60, string sort = "downloads", CancellationToken ct = default)
    {
        var encodedQuery = string.IsNullOrWhiteSpace(query) ? "" : $"search={Uri.EscapeDataString(query)}&";
        var url = $"{BaseUrl}/api/models?{encodedQuery}filter=gguf&sort={sort}&direction=-1&limit={limit}&expand[]=downloads&expand[]=likes&expand[]=tags&expand[]=pipeline_tag&expand[]=lastModified";

        _logger.LogInformation("Searching Hugging Face models with query: {Query}", query);

        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        var jsonDocs = JsonSerializer.Deserialize<JsonElement[]>(content);

        var results = new List<HfModelInfo>();

        if (jsonDocs != null)
        {
            foreach (var element in jsonDocs)
            {
                var id = element.GetProperty("id").GetString() ?? string.Empty;
                
                // Filter out MTP architectures since they are unsupported by the current LLamaSharp version
                if (id.Contains("-mtp", StringComparison.OrdinalIgnoreCase) || 
                    id.Contains("mtp-", StringComparison.OrdinalIgnoreCase) || 
                    id.EndsWith("-mtp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = id.Split('/');
                var author = parts.Length > 1 ? parts[0] : "unknown";
                var modelName = parts.Length > 1 ? parts[1] : id;
                var downloads = element.TryGetProperty("downloads", out var dl) ? dl.GetInt32() : 0;
                var likes = element.TryGetProperty("likes", out var lk) ? lk.GetInt32() : 0;
                var tags = element.TryGetProperty("tags", out var tg)
                    ? tg.EnumerateArray().Select(t => t.GetString() ?? string.Empty).ToArray()
                    : [];
                
                var pipelineTag = element.TryGetProperty("pipeline_tag", out var pt) ? pt.GetString() ?? string.Empty : string.Empty;

                var lastModified = element.TryGetProperty("lastModified", out var lm) 
                    ? lm.GetDateTimeOffset() 
                    : DateTimeOffset.UtcNow;

                results.Add(new HfModelInfo(id, author, modelName, downloads, likes, lastModified, tags, "", pipelineTag));
            }
        }

        // Direct repo lookup fallback if query looks like "author/model" and generic search returned nothing
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(query) && query.Contains('/') && !query.Contains(' '))
        {
            var directModel = await GetModelInfoDirectAsync(query.Trim(), ct);
            if (directModel != null)
            {
                results.Add(directModel);
            }
        }

        return results;
    }

    private async Task<HfModelInfo?> GetModelInfoDirectAsync(string repoId, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}/api/models/{repoId}";
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync(ct);
            var element = JsonSerializer.Deserialize<JsonElement>(content);

            var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? repoId : repoId;
            var parts = id.Split('/');
            var author = parts.Length > 1 ? parts[0] : "unknown";
            var modelName = parts.Length > 1 ? parts[1] : id;
            var downloads = element.TryGetProperty("downloads", out var dl) ? dl.GetInt32() : 0;
            var likes = element.TryGetProperty("likes", out var lk) ? lk.GetInt32() : 0;
            var tags = element.TryGetProperty("tags", out var tg)
                ? tg.EnumerateArray().Select(t => t.GetString() ?? string.Empty).ToArray()
                : [];
            var pipelineTag = element.TryGetProperty("pipeline_tag", out var pt) ? pt.GetString() ?? string.Empty : string.Empty;
            var lastModified = element.TryGetProperty("lastModified", out var lm)
                ? lm.GetDateTimeOffset()
                : DateTimeOffset.UtcNow;

            return new HfModelInfo(id, author, modelName, downloads, likes, lastModified, tags, "", pipelineTag);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct model lookup failed for {RepoId}", repoId);
            return null;
        }
    }

    /// <summary>
    /// Lists all GGUF files in a specific model repository.
    /// </summary>
    /// <param name="repoId">The repository ID.</param>
    /// <param name="forceRefresh">Whether to bypass the in-memory cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of file information records.</returns>
    public async Task<List<HfFileInfo>> GetModelFilesAsync(string repoId, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return [];

        if (!forceRefresh && _modelFilesCache.TryGetValue(repoId, out var cached))
        {
            if (DateTimeOffset.UtcNow - cached.Timestamp < CacheTtl)
            {
                return cached.Files;
            }
        }

        _logger.LogInformation("Fetching files for repository: {RepoId}", repoId);

        List<HfFileInfo> files;
        try
        {
            // 1. Primary approach: Hugging Face Tree API (recursive=true) on main branch
            files = await FetchFilesFromTreeApiAsync(repoId, "main", ct);

            // 2. If main branch returns empty, try master branch
            if (files.Count == 0)
            {
                files = await FetchFilesFromTreeApiAsync(repoId, "master", ct);
            }

            // 3. Fallback approach: Model metadata API with siblings
            if (files.Count == 0)
            {
                files = await FetchFilesFromModelApiAsync(repoId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch files from tree API for {RepoId}, falling back to model metadata API.", repoId);
            files = await FetchFilesFromModelApiAsync(repoId, ct);
        }

        _modelFilesCache[repoId] = (DateTimeOffset.UtcNow, files);
        return files;
    }

    /// <summary>
    /// Overload for backwards compatibility.
    /// </summary>
    public Task<List<HfFileInfo>> GetModelFilesAsync(string repoId, CancellationToken ct) =>
        GetModelFilesAsync(repoId, forceRefresh: false, ct);

    private async Task<List<HfFileInfo>> FetchFilesFromTreeApiAsync(string repoId, string branch, CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/models/{repoId}/tree/{branch}?recursive=true";
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var items = JsonSerializer.Deserialize<JsonElement[]>(content);
        if (items == null || items.Length == 0)
        {
            return [];
        }

        var files = new List<HfFileInfo>();
        foreach (var element in items)
        {
            if (!element.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "file")
                continue;

            if (!element.TryGetProperty("path", out var pathProp))
                continue;

            var filename = pathProp.GetString();
            if (string.IsNullOrWhiteSpace(filename) ||
                !filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("-mtp", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("mtp-", StringComparison.OrdinalIgnoreCase) ||
                repoId.Contains("-mtp", StringComparison.OrdinalIgnoreCase) ||
                repoId.Contains("mtp-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long sizeBytes = 0;
            string sha256 = string.Empty;

            if (element.TryGetProperty("lfs", out var lfsProp) && lfsProp.ValueKind == JsonValueKind.Object)
            {
                if (lfsProp.TryGetProperty("size", out var lfsSizeProp) && lfsSizeProp.ValueKind == JsonValueKind.Number)
                {
                    sizeBytes = lfsSizeProp.GetInt64();
                }
                if (lfsProp.TryGetProperty("oid", out var oidProp) && oidProp.ValueKind == JsonValueKind.String)
                {
                    sha256 = oidProp.GetString() ?? string.Empty;
                }
                else if (lfsProp.TryGetProperty("sha256", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
                {
                    sha256 = shaProp.GetString() ?? string.Empty;
                }
            }

            if (sizeBytes <= 0 && element.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
            {
                sizeBytes = sizeProp.GetInt64();
            }

            if (string.IsNullOrEmpty(sha256) && element.TryGetProperty("oid", out var fileOidProp) && fileOidProp.ValueKind == JsonValueKind.String)
            {
                var oid = fileOidProp.GetString();
                if (!string.IsNullOrEmpty(oid) && oid.Length == 64)
                {
                    sha256 = oid;
                }
            }

            var quantMatch = QuantTypeRegex().Match(filename);
            var quantType = quantMatch.Success ? quantMatch.Value.ToUpperInvariant() : "Unknown";

            var paramMatch = ParameterSizeRegex().Match(repoId + "/" + filename);
            var paramSize = paramMatch.Success ? paramMatch.Value.ToUpperInvariant() : "Unknown";

            files.Add(new HfFileInfo(filename, sizeBytes, quantType, paramSize, sha256));
        }

        return files;
    }

    private async Task<List<HfFileInfo>> FetchFilesFromModelApiAsync(string repoId, CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/models/{repoId}";
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var root = JsonSerializer.Deserialize<JsonElement>(content);

        var files = new List<HfFileInfo>();
        if (!root.TryGetProperty("siblings", out var siblings))
            return files;

        var ggufElements = siblings.EnumerateArray()
            .Where(s => {
                if (!s.TryGetProperty("rfilename", out var rfn)) return false;
                var f = rfn.GetString();
                return !string.IsNullOrEmpty(f) && 
                       f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                       !f.Contains("-mtp", StringComparison.OrdinalIgnoreCase) &&
                       !f.Contains("mtp-", StringComparison.OrdinalIgnoreCase) &&
                       !repoId.Contains("-mtp", StringComparison.OrdinalIgnoreCase) &&
                       !repoId.Contains("mtp-", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        foreach (var element in ggufElements)
        {
            var filename = element.GetProperty("rfilename").GetString()!;

            long sizeBytes = 0;
            string sha256 = string.Empty;
            if (element.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
            {
                sizeBytes = sizeProp.GetInt64();
            }
            else if (element.TryGetProperty("lfs", out var lfsProp) && lfsProp.ValueKind == JsonValueKind.Object &&
                     lfsProp.TryGetProperty("size", out var lfsSizeProp) && lfsSizeProp.ValueKind == JsonValueKind.Number)
            {
                sizeBytes = lfsSizeProp.GetInt64();
            }

            if (element.TryGetProperty("lfs", out var lfsShaProp) && lfsShaProp.ValueKind == JsonValueKind.Object &&
                lfsShaProp.TryGetProperty("sha256", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
            {
                sha256 = shaProp.GetString() ?? string.Empty;
            }

            if (sizeBytes <= 0)
            {
                sizeBytes = await GetFileSizeAsync(repoId, filename, ct);
            }

            var quantMatch = QuantTypeRegex().Match(filename);
            var quantType = quantMatch.Success ? quantMatch.Value.ToUpperInvariant() : "Unknown";

            var paramMatch = ParameterSizeRegex().Match(repoId + "/" + filename);
            var paramSize = paramMatch.Success ? paramMatch.Value.ToUpperInvariant() : "Unknown";

            files.Add(new HfFileInfo(filename, sizeBytes, quantType, paramSize, sha256));
        }

        return files;
    }

    /// <summary>
    /// Utility to parse model parameter size in Billions (e.g. 7, 8, 14, 70, 0.5) from repository ID or tags.
    /// </summary>
    public static double? ExtractParameterSize(string repoId, string[] tags)
    {
        var match = Regex.Match(repoId, @"(?i)\b(\d+(?:\.\d+)?)\s*b\b");
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double size))
        {
            return size;
        }

        if (tags != null)
        {
            foreach (var tag in tags)
            {
                match = Regex.Match(tag, @"(?i)^(\d+(?:\.\d+)?)\s*b$");
                if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double tagSize))
                {
                    return tagSize;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Ranks Hugging Face search results by quality, uploader reputation, exact query matches, and popularity.
    /// </summary>
    public static List<HfModelInfo> RankResults(List<HfModelInfo> results, string query)
    {
        if (results == null || results.Count == 0) return new List<HfModelInfo>();

        var reputableAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bartowski", "unsloth", "TheBloke", "pmortensen", "mradermacher", 
            "QuantFactory", "city96", "ggml-org", "meta-llama", "Qwen", 
            "deepseek-ai", "mistralai", "google", "microsoft"
        };

        var queryTokens = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split(new[] { ' ', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries);

        return results
            .OrderByDescending(m =>
            {
                double score = 0;

                // Reputable uploader boost
                if (reputableAuthors.Contains(m.Author)) score += 1000;

                // Popularity score
                score += Math.Log10(m.Downloads + 1) * 100;
                score += Math.Log10(m.Likes + 1) * 50;

                // Query matching score
                if (queryTokens.Length > 0)
                {
                    int matchCount = 0;
                    foreach (var token in queryTokens)
                    {
                        if (m.RepoId.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            matchCount++;
                            score += 500;
                        }
                    }
                    if (matchCount == queryTokens.Length)
                    {
                        score += 2000;
                    }
                }

                return score;
            })
            .ToList();
    }

    /// <summary>
    /// Gets the README/model card text for a repository.
    /// </summary>
    /// <param name="repoId">The repository ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The markdown text of the model card.</returns>
    public async Task<string> GetModelCardAsync(string repoId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{repoId}/raw/main/README.md";
        _logger.LogInformation("Fetching model card for {RepoId}", repoId);

        try
        {
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch model card for {RepoId}", repoId);
        }

        return string.Empty;
    }

    /// <summary>
    /// Downloads a model file with progress reporting, resume capability, mid-stream retry,
    /// and (when the Hub publishes one) SHA-256 integrity verification.
    /// </summary>
    /// <param name="repoId">The repository ID.</param>
    /// <param name="filename">The filename to download.</param>
    /// <param name="destinationPath">The full path where the file should be saved.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="expectedSha256">The LFS SHA-256 published by the Hub (hex). When provided,
    /// the completed file is verified against it and a mismatch is treated as a failed,
    /// re-downloadable download instead of silently shipping a corrupted model.</param>
    public async Task DownloadModelAsync(
        string repoId, 
        string filename, 
        string destinationPath, 
        IProgress<DownloadProgress> progress, 
        CancellationToken ct = default,
        string? expectedSha256 = null)
    {
        var url = $"{BaseUrl}/{repoId}/resolve/main/{filename}";
        
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(destinationPath))
        {
            if (string.IsNullOrWhiteSpace(expectedSha256) ||
                await VerifySha256Async(destinationPath, expectedSha256, ct))
            {
                _logger.LogInformation("File already fully downloaded at {Path}.", destinationPath);
                var fi = new FileInfo(destinationPath);
                progress?.Report(new DownloadProgress(fi.Length, fi.Length, 0, 0, 100));
                return;
            }

            // An existing file that fails verification is corrupt (truncated or tampered) —
            // discard it and download afresh rather than silently loading a broken model.
            _logger.LogWarning("Existing file at {Path} failed SHA-256 verification; re-downloading.", destinationPath);
            File.Delete(destinationPath);
        }

        string tempFilePath = destinationPath + ".download";
        long completedBytes = File.Exists(tempFilePath) ? new FileInfo(tempFilePath).Length : 0;

        _logger.LogInformation("Starting download of {Filename} from {RepoId} to {Path}. Resuming from {Bytes} bytes.", 
            filename, repoId, tempFilePath, completedBytes);

        // Mid-stream retry: long downloads routinely drop connections. Each attempt re-opens the
        // request with a Range header from the current on-disk offset, so a partial write is
        // never duplicated and progress is continuous. The inner SendWithRetryAsync already
        // handles 429/HTTP-level errors before the body starts.
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                completedBytes = await DownloadChunkAsync(url, tempFilePath, completedBytes, filename, progress, ct);
                break;
            }
            catch (Exception ex) when (
                (ex is IOException || ex is HttpRequestException) &&
                attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                // Resume from wherever the file actually ended up (the failed attempt may have
                // written some complete chunks before the connection dropped).
                completedBytes = File.Exists(tempFilePath) ? new FileInfo(tempFilePath).Length : 0;
                _logger.LogWarning(ex, "Download of {Filename} interrupted at {Bytes} bytes (attempt {Attempt}/{Max}). Resuming from current offset.",
                    filename, completedBytes, attempt, MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(BaseDelay.TotalSeconds * Math.Pow(2, attempt - 1)), ct);
            }
        }

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Move(tempFilePath, destinationPath);

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (!await VerifySha256Async(destinationPath, expectedSha256, ct))
            {
                _logger.LogError("SHA-256 verification failed for {Filename} after download. Discarding the file.", filename);
                try { File.Delete(destinationPath); } catch { }
                throw new InvalidOperationException($"SHA-256 verification failed for {filename}. The downloaded file was discarded and will be re-downloaded on the next attempt.");
            }
        }

        progress?.Report(new DownloadProgress(completedBytes, completedBytes, 0, 0, 100));
        _logger.LogInformation("Download of {Filename} completed successfully.", filename);
    }

    /// <summary>
    /// Streams one chunk of a download into <paramref name="tempFilePath"/>, resuming from
    /// <paramref name="startOffset"/> via a Range header. Returns the total number of bytes on
    /// disk after a complete chunk. Throws <see cref="IOException"/> when the response ends
    /// prematurely (the caller retries from the current offset).
    /// </summary>
    private async Task<long> DownloadChunkAsync(
        string url,
        string tempFilePath,
        long startOffset,
        string filename,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        long existingBytes = startOffset;

        using var response = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingBytes > 0) req.Headers.Range = new RangeHeaderValue(existingBytes, null);
            return req;
        }, ct, HttpCompletionOption.ResponseHeadersRead, useDownloadClient: true);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server has nothing beyond our offset: the temp file already holds the whole
            // payload (a previous run finished writing but crashed before the rename). Finalize
            // it instead of leaving the download stuck at ~100% forever.
            _logger.LogInformation("Range check indicates {Filename} is already complete ({Bytes} bytes); finalizing temp file.", filename, existingBytes);
            return existingBytes;
        }

        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1L;
        bool canResume = response.StatusCode == HttpStatusCode.PartialContent;
        
        if (canResume)
        {
            totalBytes += existingBytes;
        }
        else
        {
            existingBytes = 0; // Server doesn't support resume, restart from 0
            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
        }

        // Refuse to start a multi-GB write when the disk cannot hold the remaining payload.
        if (totalBytes > 0)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(tempFilePath)) ?? Path.GetTempPath();
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < totalBytes - existingBytes)
                {
                    throw new IOException($"Insufficient disk space on {drive.Name}: need {totalBytes - existingBytes} bytes but only {drive.AvailableFreeSpace} are available for {filename}.");
                }
            }
            catch (IOException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect disk space before download; continuing.");
            }
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        long totalDownloaded = existingBytes;
        await using (var fileStream = new FileStream(tempFilePath, canResume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            int bytesRead;
            var sw = Stopwatch.StartNew();
            long lastReportedBytes = totalDownloaded;
            var lastReportTime = sw.Elapsed;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalDownloaded += bytesRead;

                var timeSinceLastReport = sw.Elapsed - lastReportTime;
                if (timeSinceLastReport.TotalMilliseconds >= 500)
                {
                    var bytesSinceLastReport = totalDownloaded - lastReportedBytes;
                    var speed = bytesSinceLastReport / timeSinceLastReport.TotalSeconds;
                    
                    double remainingSeconds = 0;
                    if (speed > 0 && totalBytes > 0)
                    {
                        remainingSeconds = (totalBytes - totalDownloaded) / speed;
                    }

                    double percent = totalBytes > 0 ? (double)totalDownloaded / totalBytes * 100 : 0;

                    progress?.Report(new DownloadProgress(
                        totalDownloaded,
                        totalBytes,
                        speed,
                        remainingSeconds,
                        percent
                    ));

                    lastReportedBytes = totalDownloaded;
                    lastReportTime = sw.Elapsed;
                }
            }
        }
        
        if (totalBytes > 0 && totalDownloaded < totalBytes)
        {
            // Premature end-of-stream: resumable, so signal via IOException for the caller's
            // bounded retry loop rather than failing the whole download.
            _logger.LogWarning("Download of {Filename} ended prematurely: received {Downloaded} of {Total} bytes.", filename, totalDownloaded, totalBytes);
            throw new IOException($"Download terminated prematurely. Downloaded {totalDownloaded} bytes out of {totalBytes} total bytes for {filename}.");
        }

        return totalDownloaded;
    }

    /// <summary>
    /// Computes the SHA-256 of a file and compares it (case-insensitively) to the expected hex
    /// digest published by the Hub. Returns false for any failure — verification errors are
    /// treated as corruption by the caller.
    /// </summary>
    private static async Task<bool> VerifySha256Async(string filePath, string expectedSha256, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, ct);
            var actual = Convert.ToHexString(hash);
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SHA-256 verification failed for {filePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Converts a repository ID (e.g. 'TheBloke/Llama-2-7B-Chat-GGUF') into a safe, single
    /// directory name for scoping downloaded files per repository, preventing filename
    /// collisions between repos that publish identically named GGUF files.
    /// </summary>
    public static string SanitizeRepoIdForPath(string repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return "unknown-repo";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(repoId.Length);
        foreach (var ch in repoId)
        {
            sb.Append(ch == '/' || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        var result = sb.ToString().Trim('_', ' ');
        return string.IsNullOrEmpty(result) ? "unknown-repo" : result;
    }

    private async Task<long> GetFileSizeAsync(string repoId, string filename, CancellationToken ct)
    {
        var url = $"{BaseUrl}/{repoId}/resolve/main/{filename}";
        try
        {
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Head, url), ct);
            if (response.IsSuccessStatusCode)
            {
                return response.Content.Headers.ContentLength ?? 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get file size for {Filename}", filename);
        }
        return 0;
    }

    /// <summary>
    /// Dedicated client for large downloads. The DI-registered singleton HttpClient keeps the
    /// framework default 100-second timeout, which a multi-GB model download would blow through;
    /// the download client disables that timeout (cancellation is driven solely by the caller's
    /// CancellationToken) and mirrors the user agent / bearer token.
    /// </summary>
    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Klydis/1.0");
        return client;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, 
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        bool useDownloadClient = false)
    {
        var client = useDownloadClient ? _downloadClient : _httpClient;

        for (int i = 0; i < MaxRetries; i++)
        {
            var request = requestFactory();
            HttpResponseMessage response = null!;
            try
            {
                response = await client.SendAsync(request, completionOption, ct);

                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }

                _logger.LogWarning("Rate limited (429). Retrying in {Seconds}s. Attempt {Attempt}/{Max}", 
                    BaseDelay.TotalSeconds * Math.Pow(2, i), i + 1, MaxRetries);
            }
            catch (HttpRequestException ex) when (i < MaxRetries - 1)
            {
                _logger.LogWarning(ex, "HTTP request failed. Retrying in {Seconds}s. Attempt {Attempt}/{Max}", 
                    BaseDelay.TotalSeconds * Math.Pow(2, i), i + 1, MaxRetries);
            }

            var delay = TimeSpan.FromSeconds(BaseDelay.TotalSeconds * Math.Pow(2, i));
            await Task.Delay(delay, ct);
        }

        // Final attempt without catching
        return await client.SendAsync(requestFactory(), completionOption, ct);
    }
}

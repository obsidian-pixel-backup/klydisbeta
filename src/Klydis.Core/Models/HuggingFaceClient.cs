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
public record HfFileInfo(
    string Filename,
    long SizeBytes,
    string QuantType,
    string ParameterSize
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

    [GeneratedRegex(@"(?i)Q[0-8]_[A-Z0-9_]+")]
    private static partial Regex QuantTypeRegex();

    [GeneratedRegex(@"(?i)(\d+(?:\.\d+)?[BT])")]
    private static partial Regex ParameterSizeRegex();

    /// <summary>
    /// Initializes a new instance of the <see cref="HuggingFaceClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="logger">The logger instance.</param>
    public HuggingFaceClient(HttpClient httpClient, ILogger<HuggingFaceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Klydis/1.0");

        var token = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// Searches the Hugging Face API for GGUF models.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="limit">The maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of model information records.</returns>
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

        if (jsonDocs == null) return results;

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

        return results;
    }

    /// <summary>
    /// Lists all GGUF files in a specific model repository.
    /// </summary>
    /// <param name="repoId">The repository ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of file information records.</returns>
    public async Task<List<HfFileInfo>> GetModelFilesAsync(string repoId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/models/{repoId}";
        _logger.LogInformation("Fetching files for repository: {RepoId}", repoId);

        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

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
            if (element.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
            {
                sizeBytes = sizeProp.GetInt64();
            }
            else if (element.TryGetProperty("lfs", out var lfsProp) && lfsProp.ValueKind == JsonValueKind.Object &&
                     lfsProp.TryGetProperty("size", out var lfsSizeProp) && lfsSizeProp.ValueKind == JsonValueKind.Number)
            {
                sizeBytes = lfsSizeProp.GetInt64();
            }

            if (sizeBytes <= 0)
            {
                sizeBytes = await GetFileSizeAsync(repoId, filename, ct);
            }

            var quantMatch = QuantTypeRegex().Match(filename);
            var quantType = quantMatch.Success ? quantMatch.Value : "Unknown";

            var paramMatch = ParameterSizeRegex().Match(repoId + "/" + filename);
            var paramSize = paramMatch.Success ? paramMatch.Value : "Unknown";

            files.Add(new HfFileInfo(filename, sizeBytes, quantType, paramSize));
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
    /// Downloads a model file with progress reporting and resume capability.
    /// </summary>
    /// <param name="repoId">The repository ID.</param>
    /// <param name="filename">The filename to download.</param>
    /// <param name="destinationPath">The full path where the file should be saved.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DownloadModelAsync(
        string repoId, 
        string filename, 
        string destinationPath, 
        IProgress<DownloadProgress> progress, 
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{repoId}/resolve/main/{filename}";
        
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(destinationPath))
        {
            _logger.LogInformation("File already fully downloaded at {Path}.", destinationPath);
            var fi = new FileInfo(destinationPath);
            progress?.Report(new DownloadProgress(fi.Length, fi.Length, 0, 0, 100));
            return;
        }

        string tempFilePath = destinationPath + ".download";
        var fileInfo = new FileInfo(tempFilePath);
        long existingBytes = fileInfo.Exists ? fileInfo.Length : 0;

        _logger.LogInformation("Starting download of {Filename} from {RepoId} to {Path}. Resuming from {Bytes} bytes.", 
            filename, repoId, tempFilePath, existingBytes);

        using var response = await SendWithRetryAsync(() => 
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingBytes > 0) req.Headers.Range = new RangeHeaderValue(existingBytes, null);
            return req;
        }, ct, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The file is already fully downloaded
            _logger.LogInformation("File already fully downloaded based on range check.");
            progress?.Report(new DownloadProgress(existingBytes, existingBytes, 0, 0, 100));
            return;
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
            fileInfo.Delete();
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        var fileStream = new FileStream(tempFilePath, canResume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalDownloaded = existingBytes;
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

        fileStream.Dispose();
        
        if (totalBytes > 0 && totalDownloaded < totalBytes)
        {
            _logger.LogError("Download of {Filename} ended prematurely: received {Downloaded} of {Total} bytes.", filename, totalDownloaded, totalBytes);
            throw new InvalidOperationException($"Download terminated prematurely. Downloaded {totalDownloaded} bytes out of {totalBytes} total bytes for {filename}.");
        }

        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Move(tempFilePath, destinationPath);

        progress?.Report(new DownloadProgress(totalDownloaded, totalBytes > 0 ? totalBytes : totalDownloaded, 0, 0, 100));
        _logger.LogInformation("Download of {Filename} completed successfully.", filename);
    }

    private async Task<long> GetFileSizeAsync(string repoId, string filename, CancellationToken ct)
    {
        var url = $"{BaseUrl}/{repoId}/resolve/main/{filename}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
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

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, 
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        for (int i = 0; i < MaxRetries; i++)
        {
            var request = requestFactory();
            HttpResponseMessage response = null!;
            try
            {
                response = await _httpClient.SendAsync(request, completionOption, ct);

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
        return await _httpClient.SendAsync(requestFactory(), completionOption, ct);
    }
}

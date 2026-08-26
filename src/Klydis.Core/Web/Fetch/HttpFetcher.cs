using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Klydis.Core.Web.Extraction;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Fetch;

/// <summary>
/// The direct-HTTP fetcher. Hardened against the failure classes an autonomous agent
/// actually hits:
///   • DNS pinning — sockets connect ONLY to addresses the SSRF guard verified (DNS
///     rebinding cannot redirect the connection after validation)
///   • redirects are NOT auto-followed: every hop is revalidated by the guard (a public URL
///     can never redirect the agent to 169.254.169.254)
///   • body size is capped before processing, not after download
///   • every failure is a structured <see cref="WebFailure"/> with retry semantics
/// </summary>
public sealed class HttpFetcher : IWebFetcher, IDisposable
{
    public const int MaxRedirects = 10;

    /// <summary>Fixed, realistic headers (deterministic — never randomized per request).</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private readonly SsrfGuard _guard;
    private readonly ILogger? _logger;
    private readonly HttpClient _client;
    private readonly TimeSpan _responseTimeout;
    private readonly IContentExtractor _extractor;

    public string Name => "http";

    public HttpFetcher(
        SsrfGuard guard,
        ILogger? logger = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? responseTimeout = null,
        IContentExtractor? extractor = null)
    {
        _guard = guard;
        _logger = logger;
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(30);
        _extractor = extractor ?? new ContentExtractor();

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false, // every hop revalidated by the guard
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            ConnectCallback = PinnedConnectAsync
        };
        _client = new HttpClient(handler) { Timeout = _responseTimeout };
    }

    public async Task<WebFetchOutcome> FetchAsync(WebFetchRequest request, CancellationToken ct)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        var stages = new List<string> { "http" };
        var chain = new List<string>();
        int attempt = 0;

        var syntax = _guard.ValidateSyntax(request.Url);
        if (syntax != null)
        {
            return WebFetchOutcome.Fail(syntax);
        }

        var currentUrl = request.Url;
        string? finalUrl = null;
        int? httpStatus = null;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            attempt++;

            // Revalidate the CURRENT hop (initial URL and every redirect target).
            var policy = await _guard.ValidateAsync(currentUrl, ct).ConfigureAwait(false);
            if (policy != null)
            {
                return WebFetchOutcome.Fail(policy with { Attempt = attempt });
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            req.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            try
            {
                using var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                httpStatus = (int)response.StatusCode;
                chain.Add(currentUrl);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location?.ToString();
                    var (nextUrl, redirectFailure) = await RedirectResolver.ValidateAndResolveNextHopAsync(
                        currentUrl, location, hop + 1, _guard, ct).ConfigureAwait(false);

                    if (redirectFailure != null)
                    {
                        return WebFetchOutcome.Fail(redirectFailure with { Attempt = attempt });
                    }

                    currentUrl = nextUrl!;
                    finalUrl = currentUrl;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0");
                    return WebFetchOutcome.Fail(WebFailure.FromHttpStatus(httpStatus.Value, retryAfter)
                        with { Stage = "http", Attempt = attempt });
                }

                // Size limit BEFORE processing: never buffer a multi-hundred-MB response.
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var body = await ReadLimitedAsync(stream, request.MaxBytes, ct).ConfigureAwait(false);
                if (body is null)
                {
                    return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.ContentTooLarge, false, false,
                        $"Response exceeded the {request.MaxBytes / (1024 * 1024)} MB size limit.", httpStatus, "http", attempt));
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                var detected = ContentTypeDetector.Detect(contentType, currentUrl, body);
                if (detected is ContentTypeDetector.Pdf or ContentTypeDetector.OctetStream)
                {
                    return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.UnsupportedContentType, false, false,
                        $"Content type '{detected}' is not supported by the extractor.", httpStatus, "extract", attempt));
                }

                WebDocument doc;
                if (_extractor is ContentExtractor ce)
                {
                    doc = ce.ExtractDocument(body, request.Url, finalUrl ?? currentUrl, detected, httpStatus,
                        WebFetchMethod.Http, request.MaxChars, new WebDiagnostics(chain, stages, attempt, start.ElapsedMilliseconds));
                }
                else
                {
                    var extracted = _extractor.Extract(body, detected, request.MaxChars);
                    doc = new WebDocument(
                        request.Url,
                        finalUrl ?? currentUrl,
                        extracted.Title,
                        extracted.Markdown,
                        detected,
                        httpStatus,
                        WebFetchMethod.Http,
                        extracted.Truncated,
                        DateTimeOffset.UtcNow,
                        ComputeHash(extracted.Markdown),
                        new WebDiagnostics(chain, stages, attempt, start.ElapsedMilliseconds));
                }

                if (string.IsNullOrWhiteSpace(doc.ContentMarkdown))
                {
                    return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.EmptyContent, false, true,
                        "The response contained no extractable content.", httpStatus, "extract", attempt));
                }

                return WebFetchOutcome.Ok(doc);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.Timeout, true, true,
                    $"Request timed out after {_responseTimeout.TotalSeconds:0}s.", httpStatus, "http", attempt));
            }
            catch (HttpRequestException ex) when (IsTlsError(ex))
            {
                var detail = ex.InnerException?.Message ?? ex.Message;
                return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.TlsFailure, false, true,
                    $"TLS handshake failed: {detail}", httpStatus, "http", attempt));
            }
            catch (HttpRequestException ex)
            {
                var detail = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.ConnectionFailure, true, true,
                    $"Connection failed: {detail}", httpStatus, "http", attempt));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "HTTP fetch failed for {Url}", currentUrl);
                var detail = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.ConnectionFailure, true, true,
                    $"Unexpected fetch error: {detail}", httpStatus, "http", attempt));
            }
        }

        return WebFetchOutcome.Fail(new WebFailure(WebFailureCode.RedirectLimit, false, false,
            $"More than {MaxRedirects} redirects.", httpStatus, "redirect", attempt));
    }

    /// <summary>
    /// Connects a socket ONLY to addresses the SSRF guard verified for the target host.
    /// This is the DNS-rebinding defense: the OS resolver is never consulted after policy validation.
    /// SocketsHttpHandler performs TLS negotiation on top of the returned stream for HTTPS requests.
    /// </summary>
    private async ValueTask<Stream> PinnedConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint?.Host ?? throw new HttpRequestException("Missing DnsEndPoint in connection context.");

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _guard.ResolvePublicAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Host '{host}' is blocked or unresolvable: {ex.Message}", ex);
        }

        // Prioritize IPv4 addresses first to avoid unroutable IPv6 timeout hangs on dual-stack hosts
        var orderedAddresses = addresses
            .OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();

        Exception? last = null;
        foreach (var ip in orderedAddresses)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(ip, context.DnsEndPoint.Port, connectCts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                try { socket?.Dispose(); } catch { /* ignore */ }
            }
        }

        throw new HttpRequestException($"Could not connect to '{host}': {last?.Message}", last);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectKeepVerb
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsTlsError(HttpRequestException ex)
    {
        for (Exception? inner = ex; inner != null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException ||
                inner.GetType().Name.Contains("Tls", StringComparison.OrdinalIgnoreCase) ||
                inner.GetType().Name.Contains("Ssl", StringComparison.OrdinalIgnoreCase) ||
                inner.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                inner.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                inner.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
                inner.Message.Contains("SEC_E_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Copies the response stream up to <paramref name="maxBytes"/> and returns null when
    /// the body exceeds the limit — the limit is enforced while reading, so an oversized
    /// response never fully enters memory.
    /// </summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

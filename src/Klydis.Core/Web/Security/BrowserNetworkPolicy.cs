using System.Collections.Concurrent;
using System.Net;
using Klydis.Core.Web.Browser;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Klydis.Core.Web.Security;

/// <summary>
/// Intercepts and policy-gates all network requests made across all pages in a browser context.
/// Implements full SSRF validation (private/reserved IP blocking, DNS resolution, scheme allowlist)
/// and enforces resource budgets (blocking heavy media, fonts, images, downloads) on sub-resources.
/// </summary>
public sealed class BrowserNetworkPolicy
{
    private readonly IWebSecurityPolicy _securityPolicy;
    private readonly BrowserResourceBudget _budget;
    private readonly ILogger? _logger;

    private int _requestCount;
    private int _imageCount;
    private int _scriptCount;
    private int _fontCount;
    private int _blockedRequests;

    public int RequestCount => _requestCount;
    public int BlockedRequests => _blockedRequests;

    public BrowserNetworkPolicy(
        IWebSecurityPolicy securityPolicy,
        BrowserResourceBudget? budget = null,
        ILogger? logger = null)
    {
        _securityPolicy = securityPolicy;
        _budget = budget ?? BrowserResourceBudget.Default;
        _logger = logger;
    }

    /// <summary>
    /// Attaches request interception routing to the given Playwright browser context.
    /// </summary>
    public async Task AttachAsync(IBrowserContext context)
    {
        await context.RouteAsync("**/*", HandleRouteAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles an intercepted route for a request within the browser context.
    /// </summary>
    public async Task HandleRouteAsync(IRoute route)
    {
        var request = route.Request;
        var url = request.Url;
        var resourceType = request.ResourceType?.ToLowerInvariant() ?? "other";

        // Allow initial about:blank if needed
        if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            await route.ContinueAsync().ConfigureAwait(false);
            return;
        }

        // 1. Enforce total request budget
        int currentCount = Interlocked.Increment(ref _requestCount);
        if (currentCount > _budget.MaxRequests)
        {
            Interlocked.Increment(ref _blockedRequests);
            _logger?.LogDebug("Browser request {Url} blocked: exceeded MaxRequests budget ({Budget})", url, _budget.MaxRequests);
            await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            return;
        }

        // 2. Resource Type Policy
        if (!IsResourceTypeAllowed(resourceType))
        {
            Interlocked.Increment(ref _blockedRequests);
            _logger?.LogDebug("Browser request {Url} blocked: resource type '{Type}' disallowed", url, resourceType);
            await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            return;
        }

        // 3. Syntax and Scheme Validation (no I/O)
        var syntaxFailure = _securityPolicy.ValidateSyntax(url);
        if (syntaxFailure != null)
        {
            Interlocked.Increment(ref _blockedRequests);
            _logger?.LogWarning("Browser sub-request {Url} blocked by syntax policy: {Message}", url, syntaxFailure.Message);
            await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            return;
        }

        // 4. SSRF & Host Resolution Validation (full DNS check for private/reserved/loopback addresses)
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var policyFailure = await _securityPolicy.ValidateAsync(url, cts.Token).ConfigureAwait(false);
            if (policyFailure != null)
            {
                Interlocked.Increment(ref _blockedRequests);
                _logger?.LogWarning("Browser sub-request {Url} blocked by SSRF policy: {Message}", url, policyFailure.Message);
                await route.AbortAsync("accessdenied").ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _blockedRequests);
            _logger?.LogWarning(ex, "Browser sub-request {Url} validation threw exception; aborting request", url);
            await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            return;
        }

        // Request passed all security and resource policies -> continue
        await route.ContinueAsync().ConfigureAwait(false);
    }

    private bool IsResourceTypeAllowed(string resourceType)
    {
        switch (resourceType)
        {
            case "document":
            case "xhr":
            case "fetch":
            case "eventsource":
            case "manifest":
            case "stylesheet":
                return true;

            case "script":
                int scriptCount = Interlocked.Increment(ref _scriptCount);
                return scriptCount <= _budget.MaxScripts;

            case "image":
                if (!_budget.AllowImages) return false;
                int imageCount = Interlocked.Increment(ref _imageCount);
                return imageCount <= _budget.MaxImages;

            case "font":
                if (!_budget.AllowFonts) return false;
                int fontCount = Interlocked.Increment(ref _fontCount);
                return fontCount <= _budget.MaxFonts;

            case "media":
                return _budget.AllowMedia;

            case "websocket":
                return _budget.AllowWebSockets;

            case "ping":
            case "csp_report":
            case "other":
            default:
                return false;
        }
    }
}

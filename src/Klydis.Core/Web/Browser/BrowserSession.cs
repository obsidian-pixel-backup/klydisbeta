using Klydis.Core.Web.Security;
using Microsoft.Playwright;

namespace Klydis.Core.Web.Browser;

/// <summary>
/// An isolated browser execution session representing a single isolated <see cref="IBrowserContext"/>
/// and page. Automatically policy-gated and cleanly disposed when the crawl completes.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly BrowserNetworkPolicy _networkPolicy;
    private readonly IDisposable? _concurrencyLease;
    private bool _isDisposed;

    public IBrowserContext Context => _context;
    public IPage Page => _page;
    public BrowserNetworkPolicy NetworkPolicy => _networkPolicy;

    public BrowserSession(
        IBrowserContext context,
        IPage page,
        BrowserNetworkPolicy networkPolicy,
        IDisposable? concurrencyLease = null)
    {
        _context = context;
        _page = page;
        _networkPolicy = networkPolicy;
        _concurrencyLease = concurrencyLease;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            await _page.CloseAsync().ConfigureAwait(false);
        }
        catch { }

        try
        {
            await _context.CloseAsync().ConfigureAwait(false);
            await _context.DisposeAsync().ConfigureAwait(false);
        }
        catch { }

        _concurrencyLease?.Dispose();
        GC.SuppressFinalize(this);
    }
}

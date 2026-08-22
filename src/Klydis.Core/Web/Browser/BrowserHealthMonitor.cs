using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Browser;

/// <summary>
/// Monitors the health and stability of Playwright browser instances and contexts.
/// Detects crashes, protocol disconnects, and navigation deadlocks to trigger automatic recycling.
/// </summary>
public sealed class BrowserHealthMonitor
{
    private readonly ILogger? _logger;
    private int _consecutiveCrashes;
    private int _totalNavigations;
    private int _successfulNavigations;
    private int _failedNavigations;
    private DateTimeOffset _lastCrashTime = DateTimeOffset.MinValue;

    public int ConsecutiveCrashes => _consecutiveCrashes;
    public int TotalNavigations => _totalNavigations;
    public int SuccessfulNavigations => _successfulNavigations;
    public int FailedNavigations => _failedNavigations;

    public BrowserHealthMonitor(ILogger? logger = null)
    {
        _logger = logger;
    }

    public void RecordNavigationSuccess()
    {
        Interlocked.Increment(ref _totalNavigations);
        Interlocked.Increment(ref _successfulNavigations);
        Interlocked.Exchange(ref _consecutiveCrashes, 0);
    }

    public void RecordNavigationFailure(Exception ex)
    {
        Interlocked.Increment(ref _totalNavigations);
        Interlocked.Increment(ref _failedNavigations);

        if (IsCrashOrProtocolError(ex))
        {
            Interlocked.Increment(ref _consecutiveCrashes);
            _lastCrashTime = DateTimeOffset.UtcNow;
            _logger?.LogWarning(ex, "Browser crash or protocol error detected (consecutive crashes: {Count})", _consecutiveCrashes);
        }
    }

    public bool NeedsRestart()
    {
        // If 2+ consecutive crashes occurred recently, recommend a full browser process restart
        return _consecutiveCrashes >= 2 ||
               (DateTimeOffset.UtcNow - _lastCrashTime < TimeSpan.FromSeconds(30) && _consecutiveCrashes > 0);
    }

    public static bool IsCrashOrProtocolError(Exception ex)
    {
        if (ex == null) return false;
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("browser has been closed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Protocol error", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Connection closed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Process exited", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Crash", StringComparison.OrdinalIgnoreCase);
    }
}

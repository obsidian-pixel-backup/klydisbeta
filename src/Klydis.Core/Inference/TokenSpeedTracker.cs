namespace Klydis.Core.Inference;

/// <summary>
/// Live tokens-per-second tracker for the UI speed counter. Computes an exponential moving
/// average of the per-token interval, so the displayed value tracks real-time throughput
/// (context-growth slowdowns, speculative-accept bursts) instead of a flat lifetime average.
/// The old <c>(tokenCount-1)/elapsed</c> average converged to a constant for steady
/// generation and looked "stuck" (e.g. pinned at 50.2 t/s for an entire long generation).
/// </summary>
public sealed class TokenSpeedTracker
{
    /// <summary>Weight of the previous EMA reading; higher = smoother, slower to move.</summary>
    private const double Smoothing = 0.85;

    private double _lastElapsedSec;
    private double _ema;

    /// <summary>
    /// Feeds one token's timing. <paramref name="elapsedSec"/> is the stopwatch reading at the
    /// moment the token arrived, and <paramref name="tokenCount"/> is the 1-based token index.
    /// Returns the current EMA reading (0 until two tokens are available).
    /// </summary>
    public double Update(double elapsedSec, int tokenCount)
    {
        double intervalSec = elapsedSec - _lastElapsedSec;
        _lastElapsedSec = elapsedSec;

        // The first token has no interval yet; also skip degenerate sub-millisecond readings
        // (batched decodes can emit several tokens with an effectively zero gap).
        if (tokenCount > 1 && intervalSec > 0.0005)
        {
            double instantTps = 1.0 / intervalSec;
            _ema = _ema <= 0
                ? instantTps
                : _ema * Smoothing + instantTps * (1.0 - Smoothing);
        }

        return _ema;
    }

    /// <summary>Current EMA reading (0 until the second token is fed).</summary>
    public double Current => _ema;
}

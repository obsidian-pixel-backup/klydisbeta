using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Chat;

/// <summary>
/// Priority / kind of an inference generation request.
/// Foreground turns have priority; background tasks (title, summary, compaction) cannot cancel foreground turns.
/// </summary>
public enum GenerationKind
{
    ForegroundTurn,
    BackgroundTitle,
    ContextCompression,
    SummaryExtraction,
    Evaluation
}

/// <summary>
/// Authoritative coordinator managing per-session turn serialization and global inference locks.
/// Ensures that no two primary turns execute concurrently within the same session, and background
/// operations never cross-cancel foreground user generations.
/// </summary>
public sealed class TurnCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeSessionCts = new(StringComparer.Ordinal);

    /// <summary>
    /// Acquires a primary turn lease for the specified session.
    /// Serializes all turns within that session.
    /// </summary>
    public async Task<ITurnLease> AcquireTurnLeaseAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new TurnLease(gate);
    }

    /// <summary>
    /// Acquires a global inference execution lease.
    /// Prevents concurrent execution on a single non-reentrant native LLM context.
    /// </summary>
    public async Task<IInferenceLease> AcquireInferenceLeaseAsync(GenerationKind kind, CancellationToken ct)
    {
        await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        return new InferenceLease(_inferenceGate, kind);
    }

    /// <summary>
    /// Registers an active cancellation source for the session, cleanly cancelling and disposing any previous one.
    /// </summary>
    public CancellationTokenSource CreateSessionCts(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));
        var newCts = new CancellationTokenSource();
        if (_activeSessionCts.TryGetValue(sessionId, out var oldCts))
        {
            try
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            catch { }
        }
        _activeSessionCts[sessionId] = newCts;
        return newCts;
    }

    /// <summary>
    /// Clears the active cancellation source if it matches the current instance.
    /// </summary>
    public void ClearSessionCts(string sessionId, CancellationTokenSource cts)
    {
        if (_activeSessionCts.TryGetValue(sessionId, out var current) && ReferenceEquals(current, cts))
        {
            _activeSessionCts.TryRemove(sessionId, out _);
        }
    }

    private sealed class TurnLease : ITurnLease
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public TurnLease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InferenceLease : IInferenceLease
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public GenerationKind Kind { get; }

        public InferenceLease(SemaphoreSlim semaphore, GenerationKind kind)
        {
            _semaphore = semaphore;
            Kind = kind;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Lease representing exclusive ownership of a session's turn execution.
/// </summary>
public interface ITurnLease : IAsyncDisposable { }

/// <summary>
/// Lease representing exclusive ownership of the global inference engine.
/// </summary>
public interface IInferenceLease : IAsyncDisposable
{
    GenerationKind Kind { get; }
}

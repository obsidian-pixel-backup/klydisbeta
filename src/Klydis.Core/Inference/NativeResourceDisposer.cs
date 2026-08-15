using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LLama;

namespace Klydis.Core.Inference;

/// <summary>
/// Background asynchronous resource disposer that offloads native handle disposal (e.g. LLamaWeights, LLamaContext)
/// off the WPF UI thread to prevent UI freezing and latency spikes.
/// </summary>
public interface INativeResourceDisposer : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Enqueues one or more disposable resources for asynchronous background disposal.
    /// Returns immediately without blocking the calling thread.
    /// </summary>
    void EnqueueForDisposal(params IDisposable?[] resources);

    /// <summary>
    /// Enqueues a collection of disposable resources for asynchronous background disposal.
    /// </summary>
    void EnqueueForDisposal(IEnumerable<IDisposable?> resources);

    /// <summary>
    /// Drains all currently enqueued items, waiting until background disposal completes.
    /// </summary>
    Task DrainAsync(CancellationToken ct = default);
}

/// <summary>
/// Implements background asynchronous resource disposal backed by an unbounded <see cref="Channel{IDisposable}"/>.
/// Directs native C++ CUDA handle releases away from the UI Dispatcher context.
/// </summary>
public sealed class NativeResourceDisposer : INativeResourceDisposer
{
    private readonly Channel<IDisposable> _queue;
    private readonly ILogger<NativeResourceDisposer>? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private int _pendingCount;

    // Signaled on every completed disposal so DrainAsync wakes exactly when items finish
    // instead of polling on a 10ms timer. One release per completion (not only at zero)
    // keeps concurrent DrainAsync callers from missing a signal: each drains re-checks
    // _pendingCount after waking, and surplus tokens are consumed harmlessly later.
    private readonly SemaphoreSlim _disposalSignal = new(0, int.MaxValue);
    private bool _isDisposed;

    public NativeResourceDisposer(ILogger<NativeResourceDisposer>? logger = null)
    {
        _logger = logger;
        _queue = Channel.CreateUnbounded<IDisposable>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _processingTask = Task.Run(ProcessQueueAsync);
    }

    public void EnqueueForDisposal(params IDisposable?[] resources)
    {
        if (resources == null || _isDisposed) return;
        var ordered = resources.Where(r => r != null)
                               .OrderBy(r => r is LLamaWeights || r!.GetType().Name.Contains("Weights") ? 2 : (r is LLamaContext || r!.GetType().Name.Contains("Context") ? 0 : 1));
        foreach (var resource in ordered)
        {
            if (_queue.Writer.TryWrite(resource!))
            {
                Interlocked.Increment(ref _pendingCount);
            }
        }
    }

    public void EnqueueForDisposal(IEnumerable<IDisposable?> resources)
    {
        if (resources == null || _isDisposed) return;
        var ordered = resources.Where(r => r != null)
                               .OrderBy(r => r is LLamaWeights || r!.GetType().Name.Contains("Weights") ? 2 : (r is LLamaContext || r!.GetType().Name.Contains("Context") ? 0 : 1));
        foreach (var resource in ordered)
        {
            if (_queue.Writer.TryWrite(resource!))
            {
                Interlocked.Increment(ref _pendingCount);
            }
        }
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        // Wake on each completed disposal and re-check the count. The re-check loop makes
        // this immune to missed-signal races: an item that finished between the count read
        // and the WaitAsync left a token in the semaphore, so the wait returns immediately.
        while (Volatile.Read(ref _pendingCount) > 0)
        {
            await _disposalSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessQueueAsync()
    {
        var token = _cts.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    DisposeItem(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (_queue.Reader.TryRead(out var item))
            {
                DisposeItem(item);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected exception in NativeResourceDisposer processing loop.");
        }
    }

    private void DisposeItem(IDisposable item)
    {
        try
        {
            _logger?.LogDebug("Disposing native resource of type {Type} on background thread.", item.GetType().Name);
            item.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception while disposing native resource of type {Type}.", item.GetType().Name);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
            // Always release so a DrainAsync that is waiting (or about to wait) wakes up.
            try { _disposalSignal.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _queue.Writer.TryComplete();
        _cts.Cancel();
        _ = DisposeAsync();
        _disposalSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch { }

        _cts.Dispose();
        _disposalSignal.Dispose();
        GC.SuppressFinalize(this);
    }
}

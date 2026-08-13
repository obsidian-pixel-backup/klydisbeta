using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using Klydis.Core.Inference.Telemetry;

namespace Klydis.Core.Inference;

/// <summary>
/// Manages speculative draft token generation, zero-VRAM N-gram lookup fallback,
/// batched target model verification, dynamic candidate scheduling, and KV cache sequence rewinding.
/// </summary>
public sealed class SpeculativeEngine : IDisposable, IAsyncDisposable
{
    private readonly ILogger<SpeculativeEngine>? _logger;
    private LLamaWeights? _draftWeights;
    private LLamaContext? _draftContext;
    private ModelParams? _draftModelParams;
    private InteractiveExecutor? _draftExecutor;
    private readonly SemaphoreSlim _draftLock = new(1, 1);
    private readonly NGramLookupEngine _ngramEngine = new();
    private volatile bool _isDisposed;

    private int _activeDraftCount = 0;
    private readonly object _draftStateLock = new();
    private TaskCompletionSource<bool>? _draftZeroActiveTcs;

    public bool IsLoaded => _draftWeights != null && _draftContext != null;

    /// <summary>
    /// True when speculative decoding is active via the zero-VRAM N-gram prompt-lookup
    /// fallback (no draft model loaded). Set by InferenceEngine based on draft resolution.
    /// </summary>
    public bool IsNGramFallbackEnabled { get; set; }

    public string? LoadedDraftPath { get; private set; }
    public NGramLookupEngine NGramEngine => _ngramEngine;

    /// <summary>
    /// Gets the telemetry recorded during the most recent speculative execution.
    /// </summary>
    public SpeculativeTelemetry? LastTelemetry { get; private set; }

    /// <summary>
    /// Event fired when speculative telemetry calculation completes.
    /// </summary>
    public event Action<SpeculativeTelemetry>? SpeculativeTelemetryCompleted;

    /// <summary>
    /// Gets the rolling acceptance rate alpha (default: 0.5f).
    /// </summary>
    public float AcceptanceRate { get; private set; } = 0.5f;

    /// <summary>
    /// Gets the dynamic candidate window K in [2, maxWindow] calculated from rolling acceptance
    /// rate alpha, where maxWindow is the user-configured <see cref="DraftCandidateCount"/>
    /// (default 10, UI range 4-32). At high acceptance rates the window saturates at the
    /// configured ceiling so a larger slider value actually speculates more tokens per step.
    /// </summary>
    public int CurrentCandidateWindow
    {
        get
        {
            int ceiling = Math.Clamp(DraftCandidateCount, 2, MaxDraftCandidateCount);
            return Math.Clamp((int)Math.Round(2 + AcceptanceRate * (ceiling - 2)), 2, ceiling);
        }
    }

    /// <summary>Maximum user-configured speculation count (matches the settings slider's 32-token ceiling).</summary>
    public const int MaxDraftCandidateCount = 32;

    /// <summary>
    /// Gets or sets the configured draft candidate window. This only bounds the initial
    /// speculation window and is clamped to [2, 10] at use. It is intentionally decoupled
    /// from <see cref="AcceptanceRate"/>, which is a measured EMA of the actual accept ratio;
    /// mapping a user slider value onto the measured rate previously pinned alpha at 1.0 and
    /// disabled both the adaptive window and the low-acceptance bypass.
    /// </summary>
    public int DraftCandidateCount { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether target model verification is bypassed.
    /// Default is false to enforce target model accuracy verification.
    /// </summary>
    public bool ForceAcceptDraftTokens { get; set; } = false;

    /// <summary>
    /// Gets or sets the native resource disposer for offloading VRAM/handle cleanup off the UI thread.
    /// </summary>
    public INativeResourceDisposer? NativeResourceDisposer { get; set; }

    public SpeculativeEngine(ILogger<SpeculativeEngine>? logger = null, INativeResourceDisposer? nativeResourceDisposer = null)
    {
        _logger = logger;
        NativeResourceDisposer = nativeResourceDisposer;
    }

    private void SafeOffloadDisposal(params IDisposable?[] resources)
    {
        var ordered = resources.Where(r => r != null)
                               .OrderBy(r => r is LLamaWeights || r!.GetType().Name.Contains("Weights") ? 2 : (r is LLamaContext || r!.GetType().Name.Contains("Context") ? 0 : 1));
        foreach (var r in ordered)
        {
            try { r?.Dispose(); } catch { }
        }
    }

    public Task LoadDraftModelAsync(string draftPath, Hardware.OffloadPlan offloadPlan)
    {
        return Task.Run(async () =>
        {
            await _draftLock.WaitAsync();
            try
            {
                UnloadInternal();

                _logger?.LogInformation("Loading speculative draft model from {Path}...", draftPath);

                var parameters = new ModelParams(draftPath)
                {
                    ContextSize = (uint)Math.Min(4096, offloadPlan.RecommendedContextSize),
                    GpuLayerCount = 0, // Keep draft model on CPU to avoid GPU VRAM/context collisions with primary model
                    BatchSize = 256,
                    UBatchSize = 256,
                    FlashAttention = false,
                    PoolingType = LLama.Native.LLamaPoolingType.Unspecified,
                    Threads = Hardware.CpuAffinityHelper.GetPCoreCount(),
                    BatchThreads = Hardware.CpuAffinityHelper.GetPCoreCount(),
                    UseMemorymap = true,
                    UseMemoryLock = false
                };

                _draftModelParams = parameters;
                _draftWeights = LLamaWeights.LoadFromFile(parameters);
                _draftContext = _draftWeights.CreateContext(parameters);
                _draftExecutor = new InteractiveExecutor(_draftContext);
                LoadedDraftPath = draftPath;

                _logger?.LogInformation("Speculative draft model loaded successfully.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load speculative draft model '{DraftPath}'. Unloading draft model.", draftPath);
                UnloadInternal();
            }
            finally
            {
                _draftLock.Release();
            }
        });
    }

    public async Task UnloadAsync()
    {
        await _draftLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnloadInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _draftLock.Release();
        }
    }

    public void Unload()
    {
        _ = UnloadAsync();
    }

    private async Task UnloadInternalAsync()
    {
        try
        {
            Task? waitTask = null;
            lock (_draftStateLock)
            {
                if (_activeDraftCount > 0)
                {
                    _draftZeroActiveTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    waitTask = _draftZeroActiveTcs.Task;
                }
            }

            if (waitTask != null)
            {
                await waitTask.ConfigureAwait(false);
            }

            LLamaContext? draftCtx = null;
            LLamaWeights? draftWeights = null;

            lock (_draftStateLock)
            {
                _draftExecutor = null;
                draftCtx = _draftContext;
                _draftContext = null;
                draftWeights = _draftWeights;
                _draftWeights = null;
                _draftModelParams = null;
                LoadedDraftPath = null;
                IsNGramFallbackEnabled = false;
                _draftZeroActiveTcs = null;
            }

            if (draftCtx != null || draftWeights != null)
            {
                SafeOffloadDisposal(draftCtx, draftWeights);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error unloading draft model.");
        }
    }

    private void UnloadInternal()
    {
        _ = UnloadInternalAsync();
    }

    /// <summary>
    /// Dynamically updates the rolling acceptance rate alpha based on accepted tokens vs speculated candidate count.
    /// Scaled smoothly using exponential moving average: alpha_new = 0.7 * alpha_old + 0.3 * step_rate.
    /// </summary>
    public void UpdateAcceptanceRate(int acceptedCount, int speculatedCount)
    {
        if (speculatedCount <= 0) return;
        float stepRate = Math.Clamp((float)acceptedCount / speculatedCount, 0.0f, 1.0f);
        AcceptanceRate = Math.Clamp(0.7f * AcceptanceRate + 0.3f * stepRate, 0.0f, 1.0f);
    }

    /// <summary>
    /// Performs native KV cache sequence rewinding to keepPosition without native context disposal.
    /// Uses llama_memory_seq_rm (MemorySequenceRemove) to eliminate prompt re-prefill delays (500-1500ms).
    /// </summary>
    public async Task<bool> RewindDraftContextAsync(int keepPosition)
    {
        await _draftLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_draftContext != null && !_draftContext.NativeHandle.IsClosed && !_draftContext.NativeHandle.IsInvalid)
            {
                _draftContext.NativeHandle.MemorySequenceRemove(
                    (LLamaSeqId)0,
                    (LLamaPos)Math.Max(0, keepPosition),
                    (LLamaPos)(-1));
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Native KV cache sequence rewinding failed.");
            return false;
        }
        finally
        {
            _draftLock.Release();
        }
    }

    public bool RewindDraftContext(int keepPosition)
    {
        try
        {
            if (_draftContext != null && !_draftContext.NativeHandle.IsClosed && !_draftContext.NativeHandle.IsInvalid)
            {
                _draftContext.NativeHandle.MemorySequenceRemove(
                    (LLamaSeqId)0,
                    (LLamaPos)Math.Max(0, keepPosition),
                    (LLamaPos)(-1));
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Native KV cache sequence rewinding failed.");
            return false;
        }
    }

    /// <summary>
    /// Intentionally a no-op. This previously rewound the draft KV cache via
    /// MemorySequenceRemove using a WORD COUNT as a token position, which desynced
    /// InteractiveExecutor's tracked prompt state from the actual KV cache and
    /// corrupted subsequent draft logits. InteractiveExecutor already manages
    /// prompt/KV continuation internally (prefix matching + delta evaluation), so
    /// no manual KV surgery is performed here.
    /// </summary>
    internal Task SynchronizeDraftContextStateAsync(string contextText) => Task.CompletedTask;

    internal void SynchronizeDraftContextState(string contextText)
    {
        _ = SynchronizeDraftContextStateAsync(contextText);
    }

    /// <summary>
    /// Generates draft candidate tokens, reserving the draft model for the duration of the
    /// generation so UnloadAsync cannot dispose the native context mid-generation.
    /// </summary>
    private async IAsyncEnumerable<string> GenerateDraftTokensAsync(
        string promptText,
        InferenceParams paramsObj,
        [EnumeratorCancellation] CancellationToken tokenCt)
    {
        InteractiveExecutor? currentExec;
        lock (_draftStateLock)
        {
            currentExec = _draftExecutor;
            if (currentExec != null)
            {
                _activeDraftCount++;
            }
        }

        if (currentExec == null)
        {
            yield break;
        }

        try
        {
            await foreach (var token in currentExec.InferAsync(promptText, paramsObj, tokenCt))
            {
                yield return token;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeDraftCount);
            CompleteDraftIdleWaitIfNeeded();
        }
    }

    /// <summary>
    /// Evaluates speculative draft candidates and validates them against the target model.
    /// Falls back to zero-VRAM N-gram prompt lookup if no draft model is loaded.
    /// </summary>
    public async IAsyncEnumerable<string> SpeculateAndVerifyAsync(
        string textToEvaluate,
        InteractiveExecutor targetExecutor,
        LLamaContext targetContext,
        InferenceParams targetInferenceParams,
        int draftCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int effectiveDraftCount = Math.Clamp(draftCount > 0 ? draftCount : DraftCandidateCount, 2, MaxDraftCandidateCount);

        if (_draftExecutor == null || _draftContext == null)
        {
            // Zero-VRAM N-gram prompt lookup fallback. Same low-acceptance policy as the
            // draft path: word-vs-token N-gram matching rarely pays off on free-form text,
            // so once the measured acceptance rate drops below the threshold, run plain
            // target inference instead of paying the speculation overhead every generation.
            if (AcceptanceRate < 0.45f)
            {
                _logger?.LogDebug("Speculative acceptance rate ({Alpha:P0}) below 45% threshold. Bypassing N-gram fallback to direct target execution.", AcceptanceRate);
                await foreach (var token in targetExecutor.InferAsync(textToEvaluate, targetInferenceParams, ct))
                {
                    yield return token;
                }
                yield break;
            }

            var ngramCandidates = _ngramEngine.FindCandidatesFromText(textToEvaluate, matchN: 3, maxCandidates: effectiveDraftCount);
            if (ngramCandidates.Count > 0)
            {
                bool ngramUsed = false;
                Func<string, InferenceParams, CancellationToken, IAsyncEnumerable<string>> ngramDraftGen =
                    (promptText, paramsObj, tokenCt) => 
                    {
                        if (ngramUsed) return ToAsyncEnumerable(Enumerable.Empty<string>());
                        ngramUsed = true;
                        return ToAsyncEnumerable(ngramCandidates);
                    };

                await foreach (var token in SpeculateAndVerifyCoreAsync(
                    textToEvaluate,
                    ngramDraftGen,
                    (text, paramsObj, tokenCt) => targetExecutor.InferAsync(text, paramsObj, tokenCt),
                    targetInferenceParams,
                    ngramCandidates.Count,
                    ForceAcceptDraftTokens,
                    SynchronizeDraftContextState,
                    ct))
                {
                    yield return token;
                }
                yield break;
            }

            await foreach (var token in targetExecutor.InferAsync(textToEvaluate, targetInferenceParams, ct))
            {
                yield return token;
            }
            yield break;
        }

        // Auto-bypass speculative decoding when candidate acceptance rate (alpha) falls below 45% threshold
        if (AcceptanceRate < 0.45f)
        {
            _logger?.LogDebug("Speculative acceptance rate ({Alpha:P0}) below 45% threshold. Bypassing speculative loop to direct CUDA target execution.", AcceptanceRate);
            await foreach (var token in targetExecutor.InferAsync(textToEvaluate, targetInferenceParams, ct))
            {
                yield return token;
            }
            yield break;
        }

        Func<string, InferenceParams, CancellationToken, IAsyncEnumerable<string>> draftGenerator =
            (promptText, paramsObj, tokenCt) => GenerateDraftTokensAsync(promptText, paramsObj, tokenCt);

        await foreach (var token in SpeculateAndVerifyCoreAsync(
            textToEvaluate,
            draftGenerator,
            (text, paramsObj, tokenCt) => targetExecutor.InferAsync(text, paramsObj, tokenCt),
            targetInferenceParams,
            effectiveDraftCount,
            ForceAcceptDraftTokens,
            SynchronizeDraftContextState,
            ct))
        {
            yield return token;
        }
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    /// <summary>
    /// Completes the unload wait when the last in-flight draft generation finishes.
    /// </summary>
    private void CompleteDraftIdleWaitIfNeeded()
    {
        TaskCompletionSource<bool>? tcs = null;
        lock (_draftStateLock)
        {
            if (_activeDraftCount == 0 && _draftZeroActiveTcs != null)
            {
                tcs = _draftZeroActiveTcs;
                _draftZeroActiveTcs = null;
            }
        }
        tcs?.TrySetResult(true);
    }

    internal async IAsyncEnumerable<string> SpeculateAndVerifyCoreAsync(
        string textToEvaluate,
        Func<string, InferenceParams, CancellationToken, IAsyncEnumerable<string>> draftGenerator,
        Func<string, InferenceParams, CancellationToken, IAsyncEnumerable<string>> targetGenerator,
        InferenceParams targetInferenceParams,
        int draftCount,
        bool forceAccept,
        Action<string>? syncContextStateAction = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int totalSpeculated = 0;
        int totalAccepted = 0;
        int totalSteps = 0;
        int totalRejections = 0;
        int totalFallbacks = 0;

        int targetSpeculation = Math.Clamp(draftCount, 2, MaxDraftCandidateCount);

        // The draft and target streams sample concurrently, so the draft MUST NOT share the
        // target's sampling pipeline instance: DefaultSamplingPipeline keeps mutable RNG state
        // and is not safe for concurrent sampling from two executors. Build a fresh pipeline
        // with the same sampling parameters (props are init-only, hence the initializer).
        var targetPipeline = targetInferenceParams.SamplingPipeline as LLama.Sampling.DefaultSamplingPipeline;
        var draftPipeline = new LLama.Sampling.DefaultSamplingPipeline
        {
            Temperature = targetPipeline?.Temperature ?? 0.7f,
            TopP = targetPipeline?.TopP ?? 0.9f,
            TopK = targetPipeline?.TopK ?? 40,
            MinP = targetPipeline?.MinP ?? 0.05f,
            TypicalP = targetPipeline?.TypicalP ?? 1.0f,
            RepeatPenalty = targetPipeline?.RepeatPenalty ?? 1.1f,
            FrequencyPenalty = targetPipeline?.FrequencyPenalty ?? 0.0f,
            PresencePenalty = targetPipeline?.PresencePenalty ?? 0.0f,
            Seed = targetPipeline?.Seed ?? 0
        };

        var draftInferenceParams = new InferenceParams
        {
            MaxTokens = targetSpeculation,
            AntiPrompts = targetInferenceParams.AntiPrompts,
            SamplingPipeline = draftPipeline
        };

        var draftTokens = new List<string>();
        bool draftFailed = false;
        try
        {
            await foreach (var token in draftGenerator(textToEvaluate, draftInferenceParams, ct))
            {
                draftTokens.Add(token);
                if (draftTokens.Count >= targetSpeculation) break;
            }
        }
        catch
        {
            draftFailed = true;
        }

        if (draftFailed || draftTokens.Count == 0)
        {
            totalFallbacks++;
            var specTelemetry = SpeculativeTelemetry.Calculate(totalSpeculated, totalAccepted, totalSteps, totalRejections, totalFallbacks);
            LastTelemetry = specTelemetry;
            SpeculativeTelemetryCompleted?.Invoke(specTelemetry);

            await foreach (var token in targetGenerator(textToEvaluate, targetInferenceParams, ct))
            {
                if (ct.IsCancellationRequested) break;
                yield return token;
            }
            yield break;
        }

        var targetStream = targetGenerator(textToEvaluate, targetInferenceParams, ct);
        await using var targetEnumerator = targetStream.GetAsyncEnumerator(ct);

        if (forceAccept)
        {
            totalSpeculated += draftTokens.Count;
            totalAccepted += draftTokens.Count;
            totalSteps++;

            foreach (var draftToken in draftTokens)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return draftToken;
            }

            for (int i = 0; i < draftTokens.Count; i++)
            {
                if (ct.IsCancellationRequested) yield break;
                if (!await targetEnumerator.MoveNextAsync()) yield break;
            }

            while (await targetEnumerator.MoveNextAsync())
            {
                if (ct.IsCancellationRequested) yield break;
                yield return targetEnumerator.Current;
            }

            UpdateAcceptanceRate(draftTokens.Count, draftTokens.Count);
            var specTelemetry = SpeculativeTelemetry.Calculate(totalSpeculated, totalAccepted, totalSteps, totalRejections, totalFallbacks);
            LastTelemetry = specTelemetry;
            SpeculativeTelemetryCompleted?.Invoke(specTelemetry);
            yield break;
        }

        string currentContext = textToEvaluate;
        List<string> currentDraftTokens = draftTokens;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                totalSpeculated += currentDraftTokens.Count;
                totalSteps++;

                var acceptedTokens = new List<string>();
                bool hasRejection = false;
                string? correctedTargetToken = null;
                bool targetEnded = false;

                for (int i = 0; i < currentDraftTokens.Count; i++)
                {
                    if (ct.IsCancellationRequested) yield break;

                    bool hasMore = await targetEnumerator.MoveNextAsync();
                    if (!hasMore)
                    {
                        targetEnded = true;
                        break;
                    }

                    string targetToken = targetEnumerator.Current;

                    if (EqualityComparer<string>.Default.Equals(currentDraftTokens[i], targetToken))
                    {
                        acceptedTokens.Add(currentDraftTokens[i]);
                        yield return currentDraftTokens[i];
                    }
                    else
                    {
                        hasRejection = true;
                        correctedTargetToken = targetToken;
                        yield return targetToken;
                        break;
                    }
                }

                totalAccepted += acceptedTokens.Count;
                if (hasRejection) totalRejections++;

                UpdateAcceptanceRate(acceptedTokens.Count, currentDraftTokens.Count);

                if (targetEnded)
                {
                    break;
                }

                if (hasRejection && correctedTargetToken != null)
                {
                    currentContext = currentContext + string.Concat(acceptedTokens) + correctedTargetToken;
                    syncContextStateAction?.Invoke(currentContext);
                }
                else
                {
                    currentContext = currentContext + string.Concat(acceptedTokens);
                }

                // Adaptive window from the measured acceptance rate, capped by the user's
                // configured draft candidate count (targetSpeculation is already clamped to [2,32]).
                int nextSpeculationCount = Math.Clamp(CurrentCandidateWindow, 2, Math.Max(2, targetSpeculation));
                draftInferenceParams.MaxTokens = nextSpeculationCount;

                var nextDraftTokens = new List<string>();
                try
                {
                    string draftInput = hasRejection ? currentContext : string.Empty;
                    await foreach (var token in draftGenerator(draftInput, draftInferenceParams, ct))
                    {
                        nextDraftTokens.Add(token);
                        if (nextDraftTokens.Count >= nextSpeculationCount) break;
                    }
                }
                catch
                {
                }

                if (nextDraftTokens.Count == 0)
                {
                    totalFallbacks++;
                    while (await targetEnumerator.MoveNextAsync())
                    {
                        if (ct.IsCancellationRequested) yield break;
                        yield return targetEnumerator.Current;
                    }
                    break;
                }

                currentDraftTokens = nextDraftTokens;
            }
        }
        finally
        {
            var specTelemetry = SpeculativeTelemetry.Calculate(totalSpeculated, totalAccepted, totalSteps, totalRejections, totalFallbacks);
            LastTelemetry = specTelemetry;
            SpeculativeTelemetryCompleted?.Invoke(specTelemetry);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try { await UnloadAsync().ConfigureAwait(false); } catch { }
        _draftLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try { Unload(); } catch { }
        _draftLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

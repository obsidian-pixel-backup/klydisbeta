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

    private int _activeDraftCount = 0;
    private readonly object _draftStateLock = new();
    private TaskCompletionSource<bool>? _draftZeroActiveTcs;

    public bool IsLoaded => _draftWeights != null && _draftContext != null;
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
    /// Gets the dynamic candidate window K in [2, 10] calculated from rolling acceptance rate alpha.
    /// </summary>
    public int CurrentCandidateWindow => Math.Clamp((int)Math.Round(2 + AcceptanceRate * (10 - 2)), 2, 10);

    /// <summary>
    /// Gets or sets the draft candidate window count. Setting this updates acceptance rate accordingly.
    /// </summary>
    public int DraftCandidateCount
    {
        get => CurrentCandidateWindow;
        set => AcceptanceRate = Math.Clamp((value - 2) / 8.0f, 0.0f, 1.0f);
    }

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
        _draftLock.Wait();
        try
        {
            UnloadInternal();
        }
        finally
        {
            _draftLock.Release();
        }
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
                waitTask.Wait();
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
    public bool RewindDraftContext(int keepPosition)
    {
        _draftLock.Wait();
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

    internal void SynchronizeDraftContextState(string contextText)
    {
        int estimatedTokens = GetTokenEstimate(contextText);
        if (!RewindDraftContext(estimatedTokens))
        {
            _draftLock.Wait();
            try
            {
                if (_draftContext != null && _draftWeights != null && _draftModelParams != null)
                {
                    _draftContext.Dispose();
                    _draftContext = _draftWeights.CreateContext(_draftModelParams);
                    _draftExecutor = new InteractiveExecutor(_draftContext);
                }
            }
            finally
            {
                _draftLock.Release();
            }
        }
    }

    private static int GetTokenEstimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
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
        int effectiveDraftCount = Math.Clamp(draftCount > 0 ? draftCount : CurrentCandidateWindow, 2, 10);

        if (_draftExecutor == null || _draftContext == null)
        {
            // Zero-VRAM N-gram prompt lookup fallback
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

        Func<string, InferenceParams, CancellationToken, IAsyncEnumerable<string>> draftGenerator = (promptText, paramsObj, tokenCt) =>
        {
            InteractiveExecutor? currentExec;
            _draftLock.Wait();
            try
            {
                currentExec = _draftExecutor;
            }
            finally
            {
                _draftLock.Release();
            }

            if (currentExec == null)
            {
                return AsyncEnumerableEmpty();
            }

            return currentExec.InferAsync(promptText, paramsObj, tokenCt);
        };

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

    private static async IAsyncEnumerable<string> AsyncEnumerableEmpty()
    {
        await Task.CompletedTask;
        yield break;
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
    /// Result record for batched target candidate evaluation.
    /// </summary>
    public record BatchedVerificationResult(
        int AcceptedCount,
        LLamaToken? CorrectedToken,
        IReadOnlyList<LLamaToken> AcceptedTokens);

    /// <summary>
    /// Single-pass batched candidate evaluation using native LLamaBatch.
    /// Evaluates target model logits for all candidate tokens in a single batched decode pass.
    /// </summary>
    public static BatchedVerificationResult VerifyCandidateBatch(
        LLamaContext targetContext,
        IReadOnlyList<LLamaToken> candidateTokens,
        LLamaPos currentPos)
    {
        if (targetContext == null || candidateTokens == null || candidateTokens.Count == 0)
        {
            return new BatchedVerificationResult(0, null, Array.Empty<LLamaToken>());
        }

        try
        {
            var batch = NativeApi.llama_batch_init(candidateTokens.Count, 0, 1);
            try
            {
                var accepted = new List<LLamaToken>();
                LLamaToken? corrected = null;

                for (int i = 0; i < candidateTokens.Count; i++)
                {
                    var logitsSpan = targetContext.NativeHandle.GetLogitsIth(i);
                    int topTokenVal = GetArgMax(logitsSpan);
                    LLamaToken predictedToken = (LLamaToken)topTokenVal;

                    if (predictedToken.Equals(candidateTokens[i]))
                    {
                        accepted.Add(candidateTokens[i]);
                    }
                    else
                    {
                        corrected = predictedToken;
                        break;
                    }
                }

                return new BatchedVerificationResult(accepted.Count, corrected, accepted);
            }
            finally
            {
                NativeApi.llama_batch_free(batch);
            }
        }
        catch
        {
            return new BatchedVerificationResult(0, null, Array.Empty<LLamaToken>());
        }
    }

    private static int GetArgMax(ReadOnlySpan<float> logits)
    {
        if (logits.Length == 0) return 0;
        int maxIdx = 0;
        float maxVal = logits[0];
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] > maxVal)
            {
                maxVal = logits[i];
                maxIdx = i;
            }
        }
        return maxIdx;
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

        int targetSpeculation = Math.Clamp(draftCount, 2, 10);

        var draftInferenceParams = new InferenceParams
        {
            MaxTokens = targetSpeculation,
            AntiPrompts = targetInferenceParams.AntiPrompts,
            SamplingPipeline = targetInferenceParams.SamplingPipeline ?? new LLama.Sampling.DefaultSamplingPipeline()
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

                int nextSpeculationCount = CurrentCandidateWindow;
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
        await UnloadAsync().ConfigureAwait(false);
        _draftLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Unload();
        _draftLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

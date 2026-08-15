using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klydis.App.ViewModels;

/// <summary>
/// ViewModel for monitoring system resources such as GPU, CPU, and RAM.
/// </summary>
public partial class SystemMonitorViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;
    private readonly Klydis.Core.Chat.ChatEngine? _chatEngine;
    private bool _refreshing;
    private bool _isDisposed;

    [ObservableProperty]
    private string _gpuName = "Unknown GPU";

    [ObservableProperty]
    private int _vramUsedMb;

    [ObservableProperty]
    private int _vramTotalMb = 8192;

    [ObservableProperty]
    private double _vramFreePercent;

    [ObservableProperty]
    private int _gpuTemperature;

    [ObservableProperty]
    private string _cpuName = "Unknown CPU";

    [ObservableProperty]
    private double _cpuUsagePercent;

    [ObservableProperty]
    private double _ramUsedGb;

    [ObservableProperty]
    private double _ramTotalGb = 16.0;

    [ObservableProperty]
    private int _appRamUsedMb;

    [ObservableProperty]
    private int _gpuUsagePercent;

    [ObservableProperty]
    private double _processCpuUsagePercent;

    [ObservableProperty]
    private ObservableCollection<string> _loadedModels = new();

    [ObservableProperty]
    private ObservableCollection<double> _tokensPerSecondHistory = new();

    [ObservableProperty]
    private double _currentTokensPerSecond;

    [ObservableProperty]
    private double _modelMemoryMb;

    // ---- Session token usage stats (bottom-right status bar) ----
    // "Tokens in" = prompt/context tokens consumed by chat generations;
    // "Tokens out" = tokens generated (counted live as they stream).
    // Internal summarization/RAG generations (IsIsolated) are excluded so these
    // numbers reflect real conversation usage.
    [ObservableProperty]
    private long _totalTokensIn;

    [ObservableProperty]
    private long _totalTokensOut;

    [ObservableProperty]
    private long _lastGenerationTokensIn;

    [ObservableProperty]
    private long _lastGenerationTokensOut;

    [ObservableProperty]
    private string _tokenUsageSummary = "↑ 0 in · ↓ 0 out";

    // ---- Context window ring (bottom-right status bar) ----
    // "Used" = prompt + generated tokens of the most recent chat generation (isolated
    // background work is excluded, matching the token counters). "Remaining" = total context
    // size minus used; the ring depletes as the window fills.
    [ObservableProperty]
    private long _contextWindowUsedTokens;

    [ObservableProperty]
    private long _contextWindowTotalTokens;

    [ObservableProperty]
    private double _contextWindowRemainingPercent = 100.0;

    [ObservableProperty]
    private string _contextWindowSummary = "Context window: no model loaded";

    [ObservableProperty]
    private string _contextWindowSeverity = "Normal";

    private int _currentPromptTokens;
    private int _currentGeneratedTokens;

    [ObservableProperty]
    private string _tokenUsageTooltip = "Session token usage appears here once the model generates.";

    // "Normal" / "Warning" / "Critical" — drives the status bar text color via
    // DataTriggers in MainWindow.xaml, so a maxed-out resource is visible at a glance.
    [ObservableProperty]
    private string _cpuSeverity = "Normal";

    [ObservableProperty]
    private string _gpuSeverity = "Normal";

    [ObservableProperty]
    private string _ramSeverity = "Normal";

    [ObservableProperty]
    private string _vramSeverity = "Normal";

    public SystemMonitorViewModel(
        Klydis.Core.Hardware.SystemProfiler systemProfiler,
        Klydis.Core.Inference.InferenceEngine inferenceEngine,
        Klydis.Core.Chat.ChatEngine? chatEngine = null)
    {
        _systemProfiler = systemProfiler;
        _inferenceEngine = inferenceEngine;
        _chatEngine = chatEngine;
        _inferenceEngine.TokenGenerated += OnTokenGenerated;
        _inferenceEngine.InferenceStarted += OnInferenceStarted;
        _inferenceEngine.InferenceCompleted += OnInferenceCompleted;
        _inferenceEngine.ModelStateChanged += OnModelStateChanged;
        // 2s interval: system monitors don't need 1Hz updates, and each tick previously paid
        // WMI + nvidia-smi probes (SystemProfiler now caches the static half of those).
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        // Establish the correct initial state (a model may already be loaded when this VM is
        // created — the gauge must not sit on the "no model loaded" default).
        UpdateContextWindow();
    }

    /// <summary>
    /// Stops the poll timer and unsubscribes from the singleton engine's events. Without this,
    /// the transient ViewModel kept a live subscription (and a running timer) forever.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _timer.Stop();
        _timer.Tick -= OnTimerTick;

        _inferenceEngine.TokenGenerated -= OnTokenGenerated;
        _inferenceEngine.InferenceStarted -= OnInferenceStarted;
        _inferenceEngine.InferenceCompleted -= OnInferenceCompleted;
        _inferenceEngine.ModelStateChanged -= OnModelStateChanged;
    }

    private void OnModelStateChanged(bool loaded, string? modelPath)
    {
        // The gauge must react to model load/unload IMMEDIATELY (the 2s timer also catches it,
        // but the tooltip otherwise showed the stale "no model loaded" default until the first
        // generation).
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (loaded)
            {
                _ = RefreshContextWindowEstimateAsync();
            }
            else
            {
                UpdateContextWindow();
            }
        });
    }

    private void OnTokenGenerated(string token, float tokensPerSecond)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Live "tokens out" counter: every non-empty streamed token counts (the final
            // event carries an empty token and is only a t/s update).
            if (!string.IsNullOrEmpty(token))
            {
                TotalTokensOut++;
                UpdateTokenUsageDisplay();
            }

            if (tokensPerSecond > 0)
            {
                CurrentTokensPerSecond = Math.Round(tokensPerSecond, 1);
                TokensPerSecondHistory.Add(CurrentTokensPerSecond);
                if (TokensPerSecondHistory.Count > 60)
                {
                    TokensPerSecondHistory.RemoveAt(0);
                }
            }

            if (!string.IsNullOrEmpty(token))
            {
                _currentGeneratedTokens++;
                UpdateContextWindow();
            }
        });
    }

    private void OnInferenceStarted(Klydis.Core.Inference.Telemetry.InferenceTelemetry telemetry)
    {
        // Exclude internal summarization / background generations so the status bar reflects
        // real chat usage rather than context maintenance.
        if (telemetry.IsIsolated) return;

        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Count "tokens in" live at generation start (the prompt is consumed as soon as
            // the request begins), so "in" moves in step with the per-token "out" counter.
            // Previously "in" only accumulated on completion, so a session full of
            // mid-stream failures showed "out" climbing to millions while "in" stayed near
            // zero (the observed in/out swap).
            TotalTokensIn += telemetry.PromptTokenCount;
            UpdateTokenUsageDisplay();

            // Start a fresh context-window reading for this generation: prompt tokens are
            // known up front, generated tokens accumulate via OnTokenGenerated.
            _currentPromptTokens = telemetry.PromptTokenCount;
            _currentGeneratedTokens = 0;
            UpdateContextWindow();
        });
    }

    private void OnInferenceCompleted(Klydis.Core.Inference.Telemetry.InferenceTelemetry telemetry)
    {
        // Exclude internal summarization / background generations so the status bar reflects
        // real chat usage rather than context maintenance.
        if (telemetry.IsIsolated) return;

        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Completion only updates the "last generation" tooltip pair (the exact in/out
            // split for the finished generation). Session totals are already handled live
            // by OnInferenceStarted / OnTokenGenerated.
            LastGenerationTokensIn = telemetry.PromptTokenCount;
            LastGenerationTokensOut = telemetry.GeneratedTokenCount;
            UpdateTokenUsageDisplay();

            // Final exact reading for the finished generation.
            _currentPromptTokens = telemetry.PromptTokenCount;
            _currentGeneratedTokens = telemetry.GeneratedTokenCount;
            UpdateContextWindow();
        });
    }

    private void UpdateContextWindow()
    {
        // Gate on IsModelLoaded FIRST: ContextSize falls back to 32768 when no model is loaded
        // (and only clears its params on unload), so checking ContextSize > 0 alone would show a
        // phantom "32K context" for an unloaded state.
        if (!_inferenceEngine.IsModelLoaded)
        {
            ContextWindowUsedTokens = 0;
            ContextWindowTotalTokens = 0;
            ContextWindowRemainingPercent = 100;
            ContextWindowSummary = "Context window: no model loaded";
            ContextWindowSeverity = "Normal";
            return;
        }

        // Total context can change (model load / ReapplyModelParametersAsync) — read it fresh.
        ContextWindowTotalTokens = _inferenceEngine.ContextSize;
        long used = Math.Max(0, (long)_currentPromptTokens + _currentGeneratedTokens);
        ContextWindowUsedTokens = used;

        if (ContextWindowTotalTokens <= 0)
        {
            ContextWindowRemainingPercent = 100;
            ContextWindowSummary = "Context window: unknown";
            ContextWindowSeverity = "Normal";
            return;
        }

        string modelLabel = string.IsNullOrWhiteSpace(_inferenceEngine.CurrentModelPath)
            ? "model"
            : System.IO.Path.GetFileNameWithoutExtension(_inferenceEngine.CurrentModelPath);

        long remaining = Math.Max(0, ContextWindowTotalTokens - used);
        ContextWindowRemainingPercent = Math.Round(remaining * 100.0 / ContextWindowTotalTokens, 1);
        ContextWindowSummary = $"Context window ({modelLabel}): {FormatTokens(used)} used · {FormatTokens(remaining)} remaining of {FormatTokens(ContextWindowTotalTokens)}";
        ContextWindowSeverity = ContextWindowRemainingPercent < 10 ? "Critical" : ContextWindowRemainingPercent < 30 ? "Warning" : "Normal";
    }

    private static string FormatTokens(long tokens) => tokens >= 1000 ? $"{tokens / 1000.0:F1}K" : tokens.ToString("N0");

    private void UpdateTokenUsageDisplay()
    {
        // Arrows follow the standard chat-app / network convention: ↑ = sent up to the model
        // (input), ↓ = received back from the model (output). The previous "↓ in · ↑ out" read
        // backwards (down-arrow looked like the model's output), which is the "swapped around"
        // the status bar was showing.
        TokenUsageSummary = $"↑ {TotalTokensIn:N0} in · ↓ {TotalTokensOut:N0} out";
        TokenUsageTooltip = "↑ Input = prompt/context tokens sent to the model. ↓ Output = tokens the model generated. " +
                            $"Background (isolated) generations are excluded.\n" +
                            $"Session: {TotalTokensIn:N0} in, {TotalTokensOut:N0} out.\n" +
                            $"Last generation: {LastGenerationTokensIn:N0} in, {LastGenerationTokensOut:N0} out.";
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        RefreshCommand.Execute(null);
    }

    // >=90% used is critical (red), >=70% is a warning (amber); below that is normal.
    private static string ClassifySeverity(double used, double total)
    {
        if (total <= 0) return "Normal";
        var ratio = used / total;
        if (ratio >= 0.9) return "Critical";
        if (ratio >= 0.7) return "Warning";
        return "Normal";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Skip overlapping ticks (a slow WMI/nvidia-smi probe while the previous tick is still
        // running would otherwise stack refresh tasks every interval).
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var profile = await _systemProfiler.GetHardwareProfileAsync();

        if (profile.Gpu != null)
        {
            GpuName = profile.Gpu.Name;
            VramUsedMb = profile.Gpu.UsedVramMb;
            VramTotalMb = profile.Gpu.TotalVramMb;
            VramFreePercent = VramTotalMb > 0 ? 100.0 * (VramTotalMb - VramUsedMb) / VramTotalMb : 0;
            GpuTemperature = profile.Gpu.Temperature;
            GpuUsagePercent = profile.Gpu.GpuUtilPercent;
            VramSeverity = ClassifySeverity(VramUsedMb, VramTotalMb);
            GpuSeverity = ClassifySeverity(GpuUsagePercent, 100.0);
        }

        CpuName = profile.System.CpuName;
        CpuUsagePercent = profile.System.CpuUsagePercent;
        ProcessCpuUsagePercent = profile.System.ProcessCpuUsagePercent;
        RamTotalGb = profile.System.TotalRamGb;
        RamUsedGb = Math.Round(RamTotalGb - profile.System.AvailableRamGb, 2);
        CpuSeverity = ClassifySeverity(CpuUsagePercent, 100.0);
        RamSeverity = ClassifySeverity(RamUsedGb, RamTotalGb);

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        AppRamUsedMb = (int)(process.WorkingSet64 / (1024 * 1024));

        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LoadedModels.Clear();
            if (_inferenceEngine.IsModelLoaded && !string.IsNullOrEmpty(_inferenceEngine.CurrentModelPath))
            {
                LoadedModels.Add(System.IO.Path.GetFileNameWithoutExtension(_inferenceEngine.CurrentModelPath));
                try
                {
                    var fileInfo = new System.IO.FileInfo(_inferenceEngine.CurrentModelPath);
                    ModelMemoryMb = Math.Round(fileInfo.Length / (1024.0 * 1024.0), 1);
                }
                catch
                {
                    ModelMemoryMb = 0;
                }
            }
            else
            {
                ModelMemoryMb = 0;
            }

            // Keep the ring accurate even when no generation is running: catches model load /
            // unload (if the event was missed) and ContextSize changes from
            // ReapplyModelParametersAsync. While idle, the gauge reflects the CURRENT session's
            // context usage (system prompt + existing chat history) so opening an old chat shows
            // its true occupancy immediately — the old behavior only updated on generation, so
            // the ring sat at "0 used" until the user sent a message.
            if (_chatEngine == null || !_chatEngine.IsGenerating)
            {
                _ = RefreshContextWindowEstimateAsync();
            }
            else
            {
                UpdateContextWindow();
            }
        });
    }

    private async Task RefreshContextWindowEstimateAsync()
    {
        try
        {
            long estimate = _chatEngine != null
                ? await _chatEngine.EstimateCurrentContextTokensAsync()
                : 0;
            _currentPromptTokens = (int)estimate;
            _currentGeneratedTokens = 0;
        }
        catch
        {
            // Best-effort estimate; keep the previous reading on failure.
        }
        UpdateContextWindow();
    }
}

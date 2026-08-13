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
public partial class SystemMonitorViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;

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

    public SystemMonitorViewModel(Klydis.Core.Hardware.SystemProfiler systemProfiler, Klydis.Core.Inference.InferenceEngine inferenceEngine)
    {
        _systemProfiler = systemProfiler;
        _inferenceEngine = inferenceEngine;
        _inferenceEngine.TokenGenerated += OnTokenGenerated;
        _inferenceEngine.InferenceStarted += OnInferenceStarted;
        _inferenceEngine.InferenceCompleted += OnInferenceCompleted;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
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
        });
    }

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
        });
    }
}

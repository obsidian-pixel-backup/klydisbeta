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
    private ObservableCollection<string> _loadedModels = new();

    [ObservableProperty]
    private ObservableCollection<double> _tokensPerSecondHistory = new();

    [ObservableProperty]
    private double _currentTokensPerSecond;

    [ObservableProperty]
    private double _modelMemoryMb;

    // "Normal" / "Warning" / "Critical" — drives the status bar text color via
    // DataTriggers in MainWindow.xaml, so a maxed-out resource is visible at a glance.
    [ObservableProperty]
    private string _cpuSeverity = "Normal";

    [ObservableProperty]
    private string _ramSeverity = "Normal";

    [ObservableProperty]
    private string _vramSeverity = "Normal";

    public SystemMonitorViewModel(Klydis.Core.Hardware.SystemProfiler systemProfiler, Klydis.Core.Inference.InferenceEngine inferenceEngine)
    {
        _systemProfiler = systemProfiler;
        _inferenceEngine = inferenceEngine;
        _inferenceEngine.TokenGenerated += OnTokenGenerated;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTokenGenerated(string token, float tokensPerSecond)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            CurrentTokensPerSecond = Math.Round(tokensPerSecond, 2);
            TokensPerSecondHistory.Add(CurrentTokensPerSecond);
            if (TokensPerSecondHistory.Count > 60)
            {
                TokensPerSecondHistory.RemoveAt(0);
            }
        });
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
            VramSeverity = ClassifySeverity(VramUsedMb, VramTotalMb);
        }

        CpuName = profile.System.CpuName;
        CpuUsagePercent = profile.System.CpuUsagePercent;
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

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klydis.App.ViewModels;

public enum ActivePanel
{
    Chat,
    Models,
    Skills,
    Monitor,
    Rag,
    Settings
}

/// <summary>
/// Main ViewModel for the Klydis application.
/// Manages navigation between different views.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private ActivePanel _activePanel;

    [ObservableProperty]
    private bool _isModelLoaded;

    [ObservableProperty]
    private string _appTitle = "Klydis";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _dependencyUpdatesAvailable;

    [ObservableProperty]
    private string _dependencyUpdateSummary = string.Empty;

    [ObservableProperty]
    private string _dependencyUpdateDetails = string.Empty;

    private IReadOnlyList<Klydis.Core.Updates.DependencyUpdateInfo>? _dependencyUpdates;

    public ChatViewModel ChatViewModel { get; }
    public ModelLibraryViewModel ModelLibraryViewModel { get; }
    public SkillLibraryViewModel SkillLibraryViewModel { get; }
    public SystemMonitorViewModel SystemMonitorViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    public RagViewModel RagViewModel { get; }
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;
 
    public MainViewModel(
        ChatViewModel chatViewModel,
        ModelLibraryViewModel modelLibraryViewModel,
        SkillLibraryViewModel skillLibraryViewModel,
        SystemMonitorViewModel systemMonitorViewModel,
        SettingsViewModel settingsViewModel,
        RagViewModel ragViewModel,
        Klydis.Core.Inference.InferenceEngine inferenceEngine)
    {
        ChatViewModel = chatViewModel;
        ModelLibraryViewModel = modelLibraryViewModel;
        SkillLibraryViewModel = skillLibraryViewModel;
        SystemMonitorViewModel = systemMonitorViewModel;
        SettingsViewModel = settingsViewModel;
        RagViewModel = ragViewModel;
        _inferenceEngine = inferenceEngine;

        _inferenceEngine.ModelStateChanged += (isLoaded, path) =>
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsModelLoaded = isLoaded;
                StatusText = isLoaded ? $"Model Loaded: {System.IO.Path.GetFileNameWithoutExtension(path)}" : "Ready";
            });
        };

        IsModelLoaded = _inferenceEngine.IsModelLoaded;
        StatusText = IsModelLoaded ? $"Model Loaded: {System.IO.Path.GetFileNameWithoutExtension(_inferenceEngine.CurrentModelPath)}" : "Ready";
 
        CurrentView = ChatViewModel;
        ActivePanel = ActivePanel.Chat;

        // Developer-only dependency update check (throttled to once per day, never blocks
        // startup). Gated to DEBUG builds: it reads repo-relative .csproj paths and shells out
        // to `dotnet restore`, neither of which exists on an end-user machine (an installed
        // build has no solution, no project files, and no .NET SDK). In Release the status-bar
        // affordance stays unreachable because _dependencyUpdates is never populated.
#if DEBUG
        StartDependencyUpdateCheck();
#endif
    }

    /// <summary>
    /// Kicks off a background NuGet check for newer versions of the app's dependencies and
    /// surfaces a status-bar notification when updates are available.
    /// </summary>
    private async void StartDependencyUpdateCheck()
    {
        try
        {
            var updates = await Task.Run(() =>
                Klydis.Core.Updates.DependencyUpdateChecker.CheckForUpdatesAsync());

            var available = updates
                .Where(u => u.IsUpdateAvailable)
                .OrderBy(u => u.PackageId)
                .ToList();

            if (available.Count == 0) return;

            _dependencyUpdates = available;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DependencyUpdatesAvailable = true;
                DependencyUpdateSummary = available.Count == 1
                    ? "1 dependency update available"
                    : $"{available.Count} dependency updates available";
                DependencyUpdateDetails = string.Join(Environment.NewLine,
                    available.Select(u => $"{u.PackageId}: {u.InstalledVersion} \u2192 {u.LatestVersion}"));
            });
        }
        catch
        {
            // Background check failures are non-fatal; the daily throttle retries next launch.
        }
    }

    /// <summary>
    /// Opens the dependency-update dialog where the user can review the available updates and
    /// apply them (rewrites the pinned versions in the project files and runs dotnet restore).
    /// </summary>
    [RelayCommand]
    private void OpenDependencyUpdate()
    {
        if (_dependencyUpdates == null || _dependencyUpdates.Count == 0) return;

        var window = new Klydis.App.Views.DependencyUpdateWindow(_dependencyUpdates)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();

        // If every update was applied, the next daily check (against the new manifest pins) will
        // find nothing — clear the banner now so it does not linger until the app restarts.
        if (window.UpdatesApplied)
        {
            DependencyUpdatesAvailable = false;
            DependencyUpdateSummary = string.Empty;
            DependencyUpdateDetails = string.Empty;
            _dependencyUpdates = null;
        }
    }

    [RelayCommand]
    private void Navigate(string panelName)
    {
        if (Enum.TryParse<ActivePanel>(panelName, out var panel))
        {
            ActivePanel = panel;
            CurrentView = panel switch
            {
                ActivePanel.Chat => ChatViewModel,
                ActivePanel.Models => ModelLibraryViewModel,
                ActivePanel.Skills => SkillLibraryViewModel,
                ActivePanel.Monitor => SystemMonitorViewModel,
                ActivePanel.Rag => RagViewModel,
                ActivePanel.Settings => SettingsViewModel,
                _ => ChatViewModel
            };
        }
    }
}

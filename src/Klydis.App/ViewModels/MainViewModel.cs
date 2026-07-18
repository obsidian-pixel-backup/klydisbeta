using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klydis.App.ViewModels;

public enum ActivePanel
{
    Chat,
    Models,
    Monitor,
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

    public ChatViewModel ChatViewModel { get; }
    public ModelLibraryViewModel ModelLibraryViewModel { get; }
    public SystemMonitorViewModel SystemMonitorViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;
 
    public MainViewModel(
        ChatViewModel chatViewModel,
        ModelLibraryViewModel modelLibraryViewModel,
        SystemMonitorViewModel systemMonitorViewModel,
        SettingsViewModel settingsViewModel,
        Klydis.Core.Inference.InferenceEngine inferenceEngine)
    {
        ChatViewModel = chatViewModel;
        ModelLibraryViewModel = modelLibraryViewModel;
        SystemMonitorViewModel = systemMonitorViewModel;
        SettingsViewModel = settingsViewModel;
        _inferenceEngine = inferenceEngine;

        _inferenceEngine.ModelStateChanged += (isLoaded, path) =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                IsModelLoaded = isLoaded;
                StatusText = isLoaded ? $"Model Loaded: {System.IO.Path.GetFileNameWithoutExtension(path)}" : "Ready";
            });
        };

        IsModelLoaded = _inferenceEngine.IsModelLoaded;
        StatusText = IsModelLoaded ? $"Model Loaded: {System.IO.Path.GetFileNameWithoutExtension(_inferenceEngine.CurrentModelPath)}" : "Ready";
 
        CurrentView = ChatViewModel;
        ActivePanel = ActivePanel.Chat;
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
                ActivePanel.Monitor => SystemMonitorViewModel,
                ActivePanel.Settings => SettingsViewModel,
                _ => ChatViewModel
            };
        }
    }
}

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
                ActivePanel.Skills => SkillLibraryViewModel,
                ActivePanel.Monitor => SystemMonitorViewModel,
                ActivePanel.Rag => RagViewModel,
                ActivePanel.Settings => SettingsViewModel,
                _ => ChatViewModel
            };
        }
    }
}

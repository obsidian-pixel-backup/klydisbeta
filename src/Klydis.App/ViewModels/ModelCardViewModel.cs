using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Klydis.App.ViewModels;

/// <summary>
/// ViewModel representing a single model card in the library.
/// </summary>
public partial class ModelCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _modelId = string.Empty;

    [ObservableProperty]
    private bool _isVision;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _fileSizeGb = string.Empty;

    [ObservableProperty]
    private string _architecture = string.Empty;

    [ObservableProperty]
    private string _quantType = string.Empty;

    [ObservableProperty]
    private string _parameterSize = string.Empty;

    [ObservableProperty]
    private int _estimatedVramMb;

    [ObservableProperty]
    private int _contextLength;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private DateTime? _lastUsed;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _canFitInVram;

    /// <summary>
    /// Whether the bundled native engine is expected to load this model.
    /// </summary>
    [ObservableProperty]
    private bool _isCompatible = true;

    /// <summary>
    /// Human-readable reason when <see cref="IsCompatible"/> is false (e.g. a tokenizer
    /// pre-type the bundled native backend doesn't support).
    /// </summary>
    [ObservableProperty]
    private string? _compatibilityWarning;

    [ObservableProperty]
    private string? _role;

    public System.Collections.ObjectModel.ObservableCollection<string> AvailableRoles { get; } = new()
    {
        "None",
        "Chat",
        "Code",
        "Instruct",
        "Vision",
        "Researcher",
        "UI Designer"
    };
}

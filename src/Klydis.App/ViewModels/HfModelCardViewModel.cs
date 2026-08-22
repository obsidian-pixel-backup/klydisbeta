using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klydis.App.ViewModels;

/// <summary>
/// ViewModel representing a file inside a HuggingFace repository.
/// </summary>
public partial class HfFileViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _size = string.Empty;

    [ObservableProperty]
    private string _quantType = string.Empty;

    [ObservableProperty]
    private string _repoId = string.Empty;

    [ObservableProperty]
    private bool _canFitInVram = true;

    [ObservableProperty]
    private string _sha256 = string.Empty;
}

/// <summary>
/// ViewModel representing a HuggingFace search result.
/// </summary>
public partial class HfModelCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _repoId = string.Empty;

    [ObservableProperty]
    private bool _isVision;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _modelName = string.Empty;

    [ObservableProperty]
    private string _downloads = string.Empty;

    [ObservableProperty]
    private int _likes;

    [ObservableProperty]
    private ObservableCollection<HfFileViewModel> _ggufFiles = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoadingFiles;

    [ObservableProperty]
    private bool _hasLoadedFiles;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private string[] _tags = [];

    /// <summary>
    /// Callback action to fetch files for this card.
    /// </summary>
    public Func<HfModelCardViewModel, bool, Task>? LoadFilesAction { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !HasLoadedFiles && !IsLoadingFiles && LoadFilesAction != null)
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(LoadFilesCommand.ExecuteAsync(false), operation: nameof(LoadFilesCommand));
        }
    }

    [RelayCommand]
    private async Task LoadFilesAsync(bool forceReload = false)
    {
        if (LoadFilesAction != null)
        {
            await LoadFilesAction(this, forceReload);
        }
    }

    [RelayCommand]
    private async Task RetryLoadFilesAsync()
    {
        await LoadFilesAsync(forceReload: true);
    }
}

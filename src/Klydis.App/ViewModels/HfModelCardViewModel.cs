using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

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
}

/// <summary>
/// ViewModel representing a HuggingFace search result.
/// </summary>
public partial class HfModelCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _repoId = string.Empty;

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
    private string[] _tags = [];
}

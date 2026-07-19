using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading;

namespace Klydis.App.ViewModels;

public partial class ActiveDownloadViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = string.Empty;

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    [RelayCommand]
    private void Cancel()
    {
        CancellationTokenSource.Cancel();
    }
}

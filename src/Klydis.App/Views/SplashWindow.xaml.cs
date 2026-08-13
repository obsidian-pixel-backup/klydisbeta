using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Klydis.App.Views;

/// <summary>
/// Borderless startup splash shown IMMEDIATELY when the app launches. The backend
/// initialization phases (native engine sync, model library scan, message store,
/// RAG index, skills) run behind it and report progress here, so the user sees
/// "everything being loaded" instead of a blank screen for minutes.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashViewModel ViewModel { get; }

    public SplashWindow()
    {
        InitializeComponent();
        ViewModel = new SplashViewModel();
        DataContext = ViewModel;
    }

    /// <summary>Sets the ordered list of startup phases shown on the splash.</summary>
    public void BeginPhases(string[] phaseNames)
    {
        ViewModel.BeginPhases(phaseNames);
    }

    /// <summary>Marks phases before <paramref name="index"/> done, marks <paramref name="index"/> active, and updates the status line.</summary>
    public void SetActivePhase(int index, string statusText)
    {
        ViewModel.SetActivePhase(index, statusText);
    }

    /// <summary>Marks the given phase as completed.</summary>
    public void MarkPhaseComplete(int index)
    {
        ViewModel.MarkPhaseComplete(index);
    }

    /// <summary>Updates only the status line while a phase is in progress (e.g. "Downloading engine update…").</summary>
    public void SetStatus(string statusText)
    {
        ViewModel.StatusText = statusText;
    }

    /// <summary>Marks all phases complete and shows a final status.</summary>
    public void Finish(string statusText)
    {
        ViewModel.Finish(statusText);
    }
}

/// <summary>A single startup phase shown in the splash list. State: 0 = waiting, 1 = active, 2 = done.</summary>
public partial class SplashPhaseItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _state;
}

/// <summary>View model driving the splash progress bar, status line, and phase list.</summary>
public partial class SplashViewModel : ObservableObject
{
    public ObservableCollection<SplashPhaseItem> Phases { get; } = new();

    [ObservableProperty]
    private string _statusText = "Starting Klydis…";

    [ObservableProperty]
    private double _progressValue;

    public void BeginPhases(string[] phaseNames)
    {
        Phases.Clear();
        foreach (var name in phaseNames)
        {
            Phases.Add(new SplashPhaseItem { Name = name });
        }
        ProgressValue = 0;
        StatusText = "Starting Klydis…";
    }

    public void SetActivePhase(int index, string statusText)
    {
        for (int i = 0; i < Phases.Count; i++)
        {
            Phases[i].State = i < index ? 2 : i == index ? 1 : 0;
        }
        ProgressValue = Phases.Count == 0 ? 0 : (double)index / Phases.Count;
        StatusText = statusText;
    }

    public void MarkPhaseComplete(int index)
    {
        if (index >= 0 && index < Phases.Count)
        {
            Phases[index].State = 2;
        }
    }

    public void Finish(string statusText)
    {
        foreach (var phase in Phases)
        {
            phase.State = 2;
        }
        ProgressValue = 1;
        StatusText = statusText;
    }
}

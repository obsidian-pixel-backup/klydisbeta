using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Klydis.Core.Updates;

namespace Klydis.App.Views;

/// <summary>
/// One row in the dependency-update dialog: the package, its installed/latest versions, and the
/// outcome once the user runs the update.
/// </summary>
public sealed partial class DependencyUpdateItem : ObservableObject
{
    public DependencyUpdateItem(DependencyUpdateInfo info)
    {
        PackageId = info.PackageId;
        InstalledVersion = info.InstalledVersion;
        LatestVersion = info.LatestVersion;
    }

    public string PackageId { get; }
    public string InstalledVersion { get; }
    public string LatestVersion { get; }

    public string VersionLine => $"{InstalledVersion} \u2192 {LatestVersion}";

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private Brush _resultBrush = Brushes.Transparent;
}

/// <summary>
/// Review dialog for available dependency updates. Clicking "Update All" rewrites the pinned
/// versions in the repository's project files and runs `dotnet restore`; each row then shows
/// whether its package was updated.
/// </summary>
public partial class DependencyUpdateWindow : Window
{
    private readonly IReadOnlyList<DependencyUpdateInfo> _updates;

    public ObservableCollection<DependencyUpdateItem> Items { get; } = new();

    /// <summary>True when every available update was applied successfully.</summary>
    public bool UpdatesApplied { get; private set; }

    public DependencyUpdateWindow(IReadOnlyList<DependencyUpdateInfo> updates)
    {
        InitializeComponent();
        _updates = updates;

        foreach (var update in updates)
        {
            Items.Add(new DependencyUpdateItem(update));
        }

        StatusText.Text = updates.Count == 1
            ? "1 update ready to apply."
            : $"{updates.Count} updates ready to apply.";
    }

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateAllButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Updating dependencies and restoring packages\u2026";

        try
        {
            var snapshot = _updates;
            var result = await Task.Run(() => DependencyUpdater.UpdateAllAsync(snapshot));

            int succeeded = 0;
            foreach (var r in result.Results)
            {
                var item = Items.FirstOrDefault(i => string.Equals(i.PackageId, r.PackageId, StringComparison.OrdinalIgnoreCase));
                if (item == null) continue;

                if (r.Succeeded)
                {
                    succeeded++;
                    item.ResultText = "\u2713 Updated";
                    item.ResultBrush = (Brush)FindResource("SuccessBrush");
                }
                else
                {
                    item.ResultText = "\u2717 Failed";
                    item.ResultBrush = (Brush)FindResource("ErrorBrush");
                }
            }

            UpdatesApplied = succeeded == _updates.Count;

            var sb = new StringBuilder();
            sb.Append(succeeded == _updates.Count
                ? $"Updated {succeeded} of {_updates.Count} dependencies."
                : $"Updated {succeeded} of {_updates.Count} dependencies \u2014 review the failed rows above.");

            if (result.RestoreExitCode != null)
            {
                sb.Append(result.RestoreExitCode == 0
                    ? " Packages restored successfully."
                    : " dotnet restore FAILED \u2014 see the output below.");
            }
            else if (result.RestoreOutput != null)
            {
                sb.Append(" Restore did not run.");
            }

            sb.Append(" Restart Klydis to apply the new versions.");
            StatusText.Text = sb.ToString();

            if (!string.IsNullOrWhiteSpace(result.RestoreOutput))
            {
                RestoreOutputText.Text = result.RestoreOutput.Trim();
                RestoreOutputPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update failed: {ex.Message}";
        }
        finally
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            UpdateAllButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}

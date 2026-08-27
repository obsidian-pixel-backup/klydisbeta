using System.Windows;
using System.Windows.Controls;
using MdXaml;

namespace Klydis.App.Views;

/// <summary>
/// Interaction logic for ModelLibraryView.xaml
/// </summary>
public partial class ModelLibraryView : UserControl
{
    public ModelLibraryView()
    {
        InitializeComponent();
    }

    private void MarkdownViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is MarkdownScrollViewer viewer)
        {
            Helpers.MarkdownViewerStyler.Apply(viewer);
        }
    }
}

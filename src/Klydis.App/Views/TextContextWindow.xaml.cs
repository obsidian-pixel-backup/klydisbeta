using System.Windows;

namespace Klydis.App.Views;

public partial class TextContextWindow : Window
{
    public string ContextTitle => TitleInput.Text.Trim();
    public string ContextText => ContentInput.Text;

    public TextContextWindow()
    {
        InitializeComponent();
        ContentInput.Focus();
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ContentInput.Text))
        {
            MessageBox.Show("Please enter or paste some text/code context before attaching.", "Empty Context", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

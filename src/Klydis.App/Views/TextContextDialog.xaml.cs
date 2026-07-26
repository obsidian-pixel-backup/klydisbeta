using System.Windows;

namespace Klydis.App.Views;

public partial class TextContextDialog : Window
{
    public string ContextTitle => TitleInput.Text.Trim();
    public string ContextText => ContentInput.Text;

    public TextContextDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => ContentInput.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ContentInput.Text))
        {
            MessageBox.Show("Please enter context content.", "Empty Context", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

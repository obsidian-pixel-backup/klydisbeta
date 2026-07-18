using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using Klydis.App.ViewModels;

namespace Klydis.App.Views;

/// <summary>
/// Interaction logic for ChatView.xaml
/// </summary>
public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ChatViewModel oldVm)
        {
            oldVm.Messages.CollectionChanged -= Messages_CollectionChanged;
        }

        if (e.NewValue is ChatViewModel newVm)
        {
            newVm.Messages.CollectionChanged += Messages_CollectionChanged;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (MessagesList.Items.Count > 0)
                {
                    var lastItem = MessagesList.Items[MessagesList.Items.Count - 1];
                    MessagesList.ScrollIntoView(lastItem);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                return;
            }
            else
            {
                e.Handled = true;
                if (DataContext is ChatViewModel vm && vm.SendMessageCommand.CanExecute(null))
                {
                    vm.SendMessageCommand.Execute(null);
                }
            }
        }
    }
}

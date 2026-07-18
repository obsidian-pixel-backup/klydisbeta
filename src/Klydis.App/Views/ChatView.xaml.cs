using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Klydis.App.ViewModels;

namespace Klydis.App.Views;

/// <summary>
/// Interaction logic for ChatView.xaml
/// </summary>
public partial class ChatView : UserControl
{
    private ScrollViewer? _scrollViewer;

    public ChatView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => InputTextBox.Focus();
        PreviewMouseDown += ChatView_PreviewMouseDown;
    }

    private void ChatView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking anywhere outside a text box while a title edit is open commits it.
        // LostFocus alone is not enough: clicks on non-focusable chrome never move focus.
        if (DataContext is ChatViewModel vm && vm.SelectedSession?.IsEditingTitle == true
            && !HasTextBoxAncestor(e.OriginalSource as System.Windows.DependencyObject))
        {
            vm.CommitEditTitleCommand.Execute(vm.SelectedSession);
        }
    }

    private static bool HasTextBoxAncestor(System.Windows.DependencyObject? node)
    {
        while (node != null)
        {
            if (node is TextBox) return true;
            node = node is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ChatViewModel oldVm)
        {
            oldVm.Messages.CollectionChanged -= Messages_CollectionChanged;
            foreach (var msg in oldVm.Messages)
            {
                msg.PropertyChanged -= Message_PropertyChanged;
            }
        }

        if (e.NewValue is ChatViewModel newVm)
        {
            newVm.Messages.CollectionChanged += Messages_CollectionChanged;
            foreach (var msg in newVm.Messages)
            {
                msg.PropertyChanged += Message_PropertyChanged;
            }
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (ChatMessageViewModel msg in e.NewItems)
            {
                msg.PropertyChanged += Message_PropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (ChatMessageViewModel msg in e.OldItems)
            {
                msg.PropertyChanged -= Message_PropertyChanged;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollToBottom(force: true);
        }
    }

    private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Follow streaming content growth, but only while the user is already at the bottom.
        if (e.PropertyName == nameof(ChatMessageViewModel.Content))
        {
            ScrollToBottom(force: false);
        }
    }

    private void ScrollToBottom(bool force)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _scrollViewer ??= FindScrollViewer(MessagesList);
            if (_scrollViewer == null)
            {
                return;
            }

            bool nearBottom = _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset < 120;
            if (force || nearBottom)
            {
                _scrollViewer.ScrollToEnd();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(System.Windows.DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private void HeaderTitleEdit_IsVisibleChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.InvokeAsync(() =>
            {
                HeaderTitleEdit.Focus();
                HeaderTitleEdit.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void HeaderTitleEdit_LostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        // Commit is a no-op if editing was already ended by Enter or Esc.
        if (DataContext is ChatViewModel vm && vm.SelectedSession != null)
        {
            vm.CommitEditTitleCommand.Execute(vm.SelectedSession);
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

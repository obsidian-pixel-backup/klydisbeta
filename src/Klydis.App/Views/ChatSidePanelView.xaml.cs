using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Klydis.App.ViewModels;
using MdXaml;

namespace Klydis.App.Views;

/// <summary>
/// Interaction logic for ChatSidePanelView.xaml. The panel's DataContext is the owning
/// ChatViewModel; all panel state is reached through <c>SidePanel</c>. Code-behind only
/// handles the parts XAML cannot: WebBrowser navigation, log auto-scroll, MdXaml
/// dark-theme table styling, and the queued-messages drag-and-drop reorder.
/// </summary>
public partial class ChatSidePanelView : UserControl
{
    private ChatSidePanelViewModel? _panel;
    private Action<string>? _htmlHandler;
    private bool _userScrolledLogUp;

    public ChatSidePanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachPanel();
        // ScrollChanged is a routed event declared on ScrollViewer; the ListBox only exposes
        // it through the bubbling route, so it must be subscribed in code rather than in XAML.
        TerminalList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(TerminalList_ScrollChanged));
    }

    // ----------------------------------------------------------------------------------------
    // Queued-messages drag-and-drop reorder
    // ----------------------------------------------------------------------------------------
    private QueuedMessageViewModel? _draggedQueueItem;
    private Point _dragStartPoint;

    private void QueuedMessagesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        // Only start a drag from the item body — never from buttons or the edit TextBox.
        if (FindVisualAncestor<Button>(source) != null) return;
        if (FindVisualAncestor<TextBox>(source) != null) return;

        if (FindVisualAncestor<ListBoxItem>(source)?.DataContext is QueuedMessageViewModel vm && !vm.IsEditing)
        {
            _draggedQueueItem = vm;
            _dragStartPoint = e.GetPosition(null);
        }
    }

    private void QueuedMessagesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedQueueItem == null || e.LeftButton != MouseButtonState.Pressed) return;

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Reordering edits the real processing order — surface it in Manual mode so the view
        // matches what will actually be processed next.
        if (DataContext is ChatViewModel vm &&
            vm.SelectedQueueSortOption?.Mode != ChatViewModel.QueueSortMode.Manual)
        {
            vm.SelectedQueueSortOption = vm.QueueSortOptions[0];
        }

        // Switching sort mode rebuilds the collection (new VM instances), so re-resolve the
        // dragged item by its stable Id instead of relying on the mouse-down instance.
        var current = QueuedMessagesList.Items
            .OfType<QueuedMessageViewModel>()
            .FirstOrDefault(i => i.Id == _draggedQueueItem.Id);
        if (current == null)
        {
            _draggedQueueItem = null;
            return;
        }
        _draggedQueueItem = current;

        var data = new DataObject(typeof(QueuedMessageViewModel), _draggedQueueItem);
        DragDrop.DoDragDrop(QueuedMessagesList, data, DragDropEffects.Move);
    }

    private void QueuedMessagesList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedQueueItem == null || !e.Data.GetDataPresent(typeof(QueuedMessageViewModel)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        ClearDropIndicator();

        int itemCount = QueuedMessagesList.Items.Count;
        if (itemCount == 0) return;

        int slot = GetDropSlotIndex(e.GetPosition(QueuedMessagesList));
        if (slot >= itemCount)
        {
            // Dropping after the last item: underline its bottom edge.
            if (QueuedMessagesList.ItemContainerGenerator.ContainerFromIndex(itemCount - 1) is ListBoxItem last &&
                last.DataContext != _draggedQueueItem)
            {
                last.Tag = "drop-after";
            }
        }
        else if (QueuedMessagesList.ItemContainerGenerator.ContainerFromIndex(slot) is ListBoxItem target &&
                 target.DataContext != _draggedQueueItem)
        {
            target.Tag = "drop-before";
        }
    }

    private void QueuedMessagesList_DragLeave(object sender, DragEventArgs e) => ClearDropIndicator();

    private void QueuedMessagesList_Drop(object sender, DragEventArgs e)
    {
        ClearDropIndicator();
        e.Handled = true;

        if (_draggedQueueItem == null || !e.Data.GetDataPresent(typeof(QueuedMessageViewModel))) return;
        if (DataContext is not ChatViewModel vm) return;

        var dragged = QueuedMessagesList.Items
            .OfType<QueuedMessageViewModel>()
            .FirstOrDefault(i => i.Id == _draggedQueueItem.Id);
        if (dragged == null) return;

        int currentIndex = QueuedMessagesList.Items.IndexOf(dragged);
        int targetIndex = GetDropSlotIndex(e.GetPosition(QueuedMessagesList));

        // The dragged item still occupies a slot in the displayed list; shift the target down
        // by one when moving it forward so the drop lands where the user sees it.
        if (currentIndex >= 0 && currentIndex < targetIndex)
        {
            targetIndex--;
        }

        vm.MoveQueuedItem(dragged, targetIndex);
        _draggedQueueItem = null;
    }

    /// <summary>Slot index (0..Count) for a point over the list: the item whose top half the
    /// point is in, or Count when below the last item.</summary>
    private int GetDropSlotIndex(Point posInList)
    {
        int count = QueuedMessagesList.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (QueuedMessagesList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container) continue;
            Point topLeft = container.TranslatePoint(new Point(0, 0), QueuedMessagesList);
            if (posInList.Y < topLeft.Y + container.ActualHeight / 2) return i;
        }
        return count;
    }

    private void ClearDropIndicator()
    {
        foreach (var item in QueuedMessagesList.Items)
        {
            if (QueuedMessagesList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            {
                container.ClearValue(FrameworkElement.TagProperty);
            }
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachPanel();
        if (DataContext is ChatViewModel vm)
        {
            _panel = vm.SidePanel;
            _htmlHandler = html => PreviewBrowser?.NavigateToString(html);
            _panel.HtmlPreviewRequested += _htmlHandler;
            _panel.PropertyChanged += OnPanelPropertyChanged;
            // New commands stream in while the model works — keep the transcript pinned to
            // the latest entry unless the user has scrolled up to read.
            _panel.TerminalEntries.CollectionChanged += OnTerminalEntriesChanged;
        }
    }

    private void DetachPanel()
    {
        if (_panel != null)
        {
            if (_htmlHandler != null)
            {
                _panel.HtmlPreviewRequested -= _htmlHandler;
            }
            _panel.PropertyChanged -= OnPanelPropertyChanged;
            _panel.TerminalEntries.CollectionChanged -= OnTerminalEntriesChanged;
            _panel = null;
            _htmlHandler = null;
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatSidePanelViewModel.TerminalStatusText))
        {
            AutoScrollTerminalToEnd();
        }
    }

    private void OnTerminalEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        AutoScrollTerminalToEnd();
    }

    private void TerminalList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Remember whether the user has scrolled up to read older entries; a rebuild of the
        // transcript must not yank them back to the bottom while they are reading.
        _userScrolledLogUp = e.ExtentHeight > e.ViewportHeight && e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 24;
    }

    private void AutoScrollTerminalToEnd()
    {
        if (TerminalList == null || _userScrolledLogUp) return;
        TerminalList.ScrollIntoView(TerminalList.Items[TerminalList.Items.Count - 1]);
    }

    private void MarkdownViewer_Loaded(object sender, RoutedEventArgs e)
    {
        // Same styling pass as ChatView (shared helper + Themes/MarkdownStyles.xaml):
        // MdXaml's defaults are light-theme hardcoded and its code blocks are AvalonEdit
        // controls, so apply the app's markdown styles, re-parse, and rewrite code blocks.
        if (sender is MarkdownScrollViewer viewer)
        {
            Helpers.MarkdownViewerStyler.Apply(viewer);
        }
    }
}

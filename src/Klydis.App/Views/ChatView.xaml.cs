using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Klydis.App.ViewModels;
using MdXaml;

namespace Klydis.App.Views;

/// <summary>
/// Interaction logic for ChatView.xaml
/// </summary>
public partial class ChatView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private bool _shouldAutoScrollToBottom = true;
    private ChatSidePanelViewModel? _sidePanel;

    // Velocity-based (inertia) smooth scroll, driven by CompositionTarget.Rendering.
    // A real mouse wheel fires a burst of several notches within ~100-200ms; restarting
    // a fixed-duration DoubleAnimation on every notch made those bursts fight each other
    // and stutter. Accumulating an impulse into one velocity value that decays every
    // rendered frame absorbs a whole burst as one continuous motion instead.
    private double _scrollVelocity;
    private DateTime _lastScrollRenderTime;
    private bool _scrollAnimating;

    public ChatView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        MessagesList.Loaded += OnMessagesListLoaded;
        PreviewMouseDown += ChatView_PreviewMouseDown;
    }

    private void OnMessagesListLoaded(object sender, RoutedEventArgs e)
    {
        GetMainScrollViewer();
        UpdateScrollToBottomButtonVisibility();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputTextBox.Focus();
        GetMainScrollViewer();
        _shouldAutoScrollToBottom = true;
        ScrollToBottom(force: true);
    }

    private void ChatSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // PlacementTarget is set here rather than via an ElementName binding in XAML:
        // Popup content lives in a separate visual tree, where ElementName lookups are
        // unreliable. The button is guaranteed non-null (the click originates from it).
        ChatSettingsPopup.PlacementTarget = ChatSettingsButton;
        ChatSettingsPopup.IsOpen = !ChatSettingsPopup.IsOpen;
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
            oldVm.PropertyChanged -= Vm_PropertyChanged;
            oldVm.Messages.CollectionChanged -= Messages_CollectionChanged;
            foreach (var msg in oldVm.Messages)
            {
                msg.PropertyChanged -= Message_PropertyChanged;
            }
            DetachSidePanel(oldVm);
        }

        if (e.NewValue is ChatViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
            newVm.Messages.CollectionChanged += Messages_CollectionChanged;
            foreach (var msg in newVm.Messages)
            {
                msg.PropertyChanged += Message_PropertyChanged;
            }
            AttachSidePanel(newVm);
            _shouldAutoScrollToBottom = true;
            ScrollToBottom(force: true);
        }
    }

    // The right-side panel's column width and splitter follow SidePanel.IsPanelOpen; a
    // collapsed (width 0) column would otherwise still reserve 360px of layout space.
    private void AttachSidePanel(ChatViewModel vm)
    {
        _sidePanel = vm.SidePanel;
        _sidePanel.PropertyChanged += SidePanel_PropertyChanged;
        UpdateSidePanelVisibility();
        UpdateSessionSidebarVisibility();
    }

    private void DetachSidePanel(ChatViewModel vm)
    {
        if (_sidePanel != null)
        {
            _sidePanel.PropertyChanged -= SidePanel_PropertyChanged;
            _sidePanel = null;
        }
    }

    private void SidePanel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatSidePanelViewModel.IsPanelOpen))
        {
            UpdateSidePanelVisibility();
        }
    }

    private void UpdateSidePanelVisibility()
    {
        bool open = _sidePanel?.IsPanelOpen == true;
        // MinWidth must follow too: WPF clamps a fixed column's final size to its MinWidth,
        // so a 0-width column with MinWidth=280 would still occupy 280px of layout space.
        SidePanelColumn.MinWidth = open ? 280 : 0;
        SidePanelColumn.Width = open ? new GridLength(360) : new GridLength(0);
        SidePanelHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePanelSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // Left session-sidebar collapse: same pattern as the right panel — the column's width
    // and MinWidth both drop to 0 when the chat list is hidden, and the splitter disappears.
    private void UpdateSessionSidebarVisibility()
    {
        bool open = DataContext is ChatViewModel vm && vm.IsSessionSidebarOpen;
        SessionSidebarColumn.MinWidth = open ? 150 : 0;
        SessionSidebarColumn.Width = open ? new GridLength(250) : new GridLength(0);
        SessionSidebarSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.SelectedSession))
        {
            _shouldAutoScrollToBottom = true;
            ScrollToBottom(force: true);
        }
        else if (e.PropertyName == nameof(ChatViewModel.IsSessionSidebarOpen))
        {
            UpdateSessionSidebarVisibility();
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

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _shouldAutoScrollToBottom = true;
        }

        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
        {
            if (!_shouldAutoScrollToBottom)
            {
                SetUnreadBadge(true);
                UpdateScrollToBottomButtonVisibility();
            }
            else
            {
                ScrollToBottom(force: true);
            }
        }
    }

    private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Follow streaming content growth, but only while auto-scroll is active or near bottom.
        if (e.PropertyName == nameof(ChatMessageViewModel.Content))
        {
            if (!_shouldAutoScrollToBottom)
            {
                SetUnreadBadge(true);
                UpdateScrollToBottomButtonVisibility();
            }
            else
            {
                ScrollToBottom(force: false);
            }
        }
    }

    private DateTime _lastScrollTime = DateTime.MinValue;
    private bool _scrollPending = false;

    private void ScrollToBottom(bool force)
    {
        if (force)
        {
            _shouldAutoScrollToBottom = true;
            SetUnreadBadge(false);
        }

        if (!force && !_shouldAutoScrollToBottom)
        {
            return;
        }

        if (!force && (DateTime.Now - _lastScrollTime).TotalMilliseconds < 50)
        {
            if (!_scrollPending)
            {
                _scrollPending = true;
                Dispatcher.InvokeAsync(async () => 
                {
                    await Task.Delay(50);
                    _scrollPending = false;
                    ScrollToBottom(false);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            return;
        }

        _lastScrollTime = DateTime.Now;
        Dispatcher.InvokeAsync(() =>
        {
            var mainSv = GetMainScrollViewer();
            if (mainSv == null)
            {
                return;
            }

            double distanceToBottom = mainSv.ScrollableHeight - mainSv.VerticalOffset;
            bool nearBottom = distanceToBottom < 120;

            if (force || nearBottom || _shouldAutoScrollToBottom)
            {
                StopScrollAnimation();
                mainSv.ScrollToEnd();

                Dispatcher.InvokeAsync(() =>
                {
                    GetMainScrollViewer()?.ScrollToEnd();
                    UpdateScrollToBottomButtonVisibility();
                }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private ScrollViewer? GetMainScrollViewer()
    {
        if (_scrollViewer != null) return _scrollViewer;

        _scrollViewer = FindMainScrollViewer(MessagesList);
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        }
        return _scrollViewer;
    }

    private void EnsureScrollViewerAttached()
    {
        GetMainScrollViewer();
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var mainSv = GetMainScrollViewer();
        if (mainSv == null) return;

        // Ignore ScrollChanged events bubbling up from inner ScrollViewers (e.g. code blocks, markdown containers)
        if (e.OriginalSource != mainSv && e.Source != mainSv)
        {
            return;
        }

        if (_shouldAutoScrollToBottom && e.ExtentHeightChange > 0)
        {
            mainSv.ScrollToEnd();
        }

        double distanceToBottom = mainSv.ScrollableHeight - mainSv.VerticalOffset;

        if (distanceToBottom <= 15)
        {
            _shouldAutoScrollToBottom = true;
            SetUnreadBadge(false);
        }

        UpdateScrollToBottomButtonVisibility();
    }

    private void UpdateScrollToBottomButtonVisibility()
    {
        if (ScrollToBottomButton == null) return;

        var mainSv = GetMainScrollViewer();
        if (mainSv == null)
        {
            ScrollToBottomButton.Visibility = Visibility.Collapsed;
            return;
        }

        double distanceToBottom = mainSv.ScrollableHeight - mainSv.VerticalOffset;
        bool showButton = mainSv.ScrollableHeight > 0 && (distanceToBottom > 30 || !_shouldAutoScrollToBottom);
        if (distanceToBottom <= 15)
        {
            showButton = false;
        }

        ScrollToBottomButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetUnreadBadge(bool visible)
    {
        if (ScrollToBottomButton?.Template?.FindName("UnreadBadge", ScrollToBottomButton) is UIElement badge)
        {
            badge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        _shouldAutoScrollToBottom = true;
        SetUnreadBadge(false);
        ScrollToBottom(force: true);
        UpdateScrollToBottomButtonVisibility();
    }

    private static ScrollViewer? FindMainScrollViewer(System.Windows.DependencyObject root)
    {
        if (root == null) return null;
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBoxItem || child is ItemsPresenter)
            {
                continue;
            }

            var result = FindMainScrollViewer(child);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the INNERMOST nested ScrollViewer (other than the main message list) under the
    /// pointer that can actually scroll in the wheel's direction. Walks the entire ancestor
    /// chain rather than stopping at the first ScrollViewer. Returns null when no nested
    /// viewer can scroll (the wheel then falls through to the main list).
    /// </summary>
    private ScrollViewer? FindInnermostScrollableNestedViewer(DependencyObject? element, int delta)
    {
        var mainSv = GetMainScrollViewer();
        while (element != null && element != MessagesList)
        {
            if (element is ScrollViewer sv && sv != mainSv && sv.ScrollableHeight > 0)
            {
                bool canScrollUp = delta > 0 && sv.VerticalOffset > 0;
                bool canScrollDown = delta < 0 && sv.VerticalOffset < sv.ScrollableHeight;
                if (canScrollUp || canScrollDown)
                {
                    return sv;
                }
            }

            // Markdown text scrolls in the ScrollViewer that HOSTS the FlowDocument, which is
            // a template child of the MarkdownScrollViewer and therefore NOT on the ancestor
            // chain: the walk jumps FlowDocument -> FlowDocumentScrollViewer and would miss it,
            // leaving the think bubble / tool markdown unscrollable (the "scrolling deadzone").
            // Descend into the viewer's template to find that host.
            if (element is FlowDocumentScrollViewer documentViewer)
            {
                var host = Helpers.MarkdownViewerStyler.FindScrollableScrollViewerInTree(documentViewer, delta, mainSv);
                if (host != null)
                {
                    return host;
                }
            }

            element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void MessagesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var mainSv = GetMainScrollViewer();
        if (mainSv == null)
        {
            return;
        }

        // Wheel over a nested scrollable area (thinking panel, tool output): scroll it
        // DIRECTLY here, in the preview phase, and mark the event handled. Without this,
        // markdown content is unscrollable: its scroll host is a template child of the
        // MarkdownScrollViewer and never receives the wheel event (the "scrolling deadzone");
        // FindInnermostScrollableNestedViewer descends into that template to find the host.
        // The viewer's own preview handler (MarkdownViewerStyler) covers standalone viewers
        // such as the artifact preview, which have no parent interceptor.
        var nestedTarget = FindInnermostScrollableNestedViewer(e.OriginalSource as DependencyObject, e.Delta);
        if (nestedTarget != null)
        {
            double line = Math.Max(48.0, nestedTarget.ViewportHeight * 0.8);
            if (e.Delta > 0)
            {
                nestedTarget.ScrollToVerticalOffset(Math.Max(0, nestedTarget.VerticalOffset - line));
            }
            else
            {
                nestedTarget.ScrollToVerticalOffset(Math.Min(nestedTarget.ScrollableHeight, nestedTarget.VerticalOffset + line));
            }
            e.Handled = true;
            return;
        }

        if (e.Delta > 0)
        {
            // User scrolled UP
            _shouldAutoScrollToBottom = false;
            UpdateScrollToBottomButtonVisibility();
        }

        _scrollVelocity = Math.Clamp(_scrollVelocity - e.Delta * 6.0, -6000, 6000);

        if (!_scrollAnimating)
        {
            _scrollAnimating = true;
            _lastScrollRenderTime = DateTime.Now;
            CompositionTarget.Rendering += OnScrollRendering;
        }

        e.Handled = true;
    }

    private void OnScrollRendering(object? sender, EventArgs e)
    {
        var mainSv = GetMainScrollViewer();
        if (mainSv == null)
        {
            StopScrollAnimation();
            return;
        }

        var now = DateTime.Now;
        double dt = Math.Min((now - _lastScrollRenderTime).TotalSeconds, 0.05);
        _lastScrollRenderTime = now;

        double max = mainSv.ScrollableHeight;
        double newOffset = mainSv.VerticalOffset + _scrollVelocity * dt;

        if (newOffset < 0)
        {
            newOffset = 0;
            _scrollVelocity = 0;
        }
        else if (newOffset > max)
        {
            newOffset = max;
            _scrollVelocity = 0;
        }

        mainSv.ScrollToVerticalOffset(newOffset);
        UpdateScrollToBottomButtonVisibility();

        // Exponential friction: velocity decays to ~5% of itself every second.
        _scrollVelocity *= Math.Pow(0.05, dt);

        if (Math.Abs(_scrollVelocity) < 4)
        {
            StopScrollAnimation();
        }
    }

    private void StopScrollAnimation()
    {
        if (_scrollAnimating)
        {
            CompositionTarget.Rendering -= OnScrollRendering;
            _scrollAnimating = false;
        }
        _scrollVelocity = 0;
        UpdateScrollToBottomButtonVisibility();
    }

    private void MarkdownViewer_Loaded(object sender, RoutedEventArgs e)
    {
        // All markdown theming is centralized in MarkdownViewerStyler (engine styles,
        // re-parse for content bound before Loaded, AvalonEdit code-block rewrite, and the
        // streamed-content watch); the shared Md* styles live in Themes/MarkdownStyles.xaml.
        if (sender is MarkdownScrollViewer viewer)
        {
            Helpers.MarkdownViewerStyler.Apply(viewer);
        }
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

    private void AddContextButton_Click(object sender, RoutedEventArgs e)
    {
        if (AddContextButton.ContextMenu != null)
        {
            AddContextButton.ContextMenu.PlacementTarget = AddContextButton;
            AddContextButton.ContextMenu.IsOpen = true;
        }
    }

    private void InputTextBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void InputTextBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && DataContext is ChatViewModel vm)
            {
                foreach (var file in files)
                {
                    vm.AddAttachmentFromPath(file);
                }
            }
            e.Handled = true;
        }
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null && DataContext is ChatViewModel vm)
                {
                    vm.AddAttachmentFromImage(img);
                    e.Handled = true;
                    return;
                }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files != null && files.Count > 0 && DataContext is ChatViewModel vm)
                {
                    foreach (var file in files)
                    {
                        if (file != null)
                        {
                            vm.AddAttachmentFromPath(file);
                        }
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                return;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                if (DataContext is ChatViewModel vm)
                {
                    if (vm.ForceSendMessageCommand.CanExecute(null))
                    {
                        vm.ForceSendMessageCommand.Execute(null);
                    }
                }
            }
            else
            {
                e.Handled = true;
                if (DataContext is ChatViewModel vm)
                {
                    if (vm.IsGenerating || vm.IsModelLoading)
                    {
                        if (vm.EnqueueMessageCommand.CanExecute(null))
                        {
                            vm.EnqueueMessageCommand.Execute(null);
                        }
                    }
                    else
                    {
                        if (vm.SendMessageCommand.CanExecute(null))
                        {
                            vm.SendMessageCommand.Execute(null);
                        }
                    }
                }
            }
        }
    }
}

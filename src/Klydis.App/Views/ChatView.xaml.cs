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
        }

        if (e.NewValue is ChatViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
            newVm.Messages.CollectionChanged += Messages_CollectionChanged;
            foreach (var msg in newVm.Messages)
            {
                msg.PropertyChanged += Message_PropertyChanged;
            }
            _shouldAutoScrollToBottom = true;
            ScrollToBottom(force: true);
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.SelectedSession))
        {
            _shouldAutoScrollToBottom = true;
            ScrollToBottom(force: true);
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

    private ScrollViewer? FindNestedScrollViewer(DependencyObject? element)
    {
        var mainSv = GetMainScrollViewer();
        while (element != null && element != MessagesList)
        {
            if (element is ScrollViewer sv && sv != mainSv)
            {
                return sv;
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

        var innerViewer = FindNestedScrollViewer(e.OriginalSource as DependencyObject);
        if (innerViewer != null && innerViewer.ScrollableHeight > 0)
        {
            bool canScrollUp = e.Delta > 0 && innerViewer.VerticalOffset > 0;
            bool canScrollDown = e.Delta < 0 && innerViewer.VerticalOffset < innerViewer.ScrollableHeight;
            if (canScrollUp || canScrollDown)
            {
                // Allow inner ScrollViewer (tool output, code block, thinking panel) to handle scrolling
                return;
            }
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
        // MdXaml's built-in renderer hardcodes its own colors (light table
        // striping, off-theme heading tint) unless these Engine properties are
        // set; MdXaml.Markdown applies them unconditionally when non-null
        // (see MdXaml.Markdown.TableEvalutor and the heading/paragraph builders).
        if (sender is not MarkdownScrollViewer viewer)
        {
            return;
        }

        Style Res(string key) => (Style)viewer.FindResource(key);

        if (viewer.FindResource("TextPrimaryBrush") is Brush textBrush)
        {
            TextElement.SetForeground(viewer, textBrush);
            if (viewer.Document != null)
            {
                viewer.Document.Foreground = textBrush;
            }
        }

        var engine = viewer.Engine;
        engine.TableStyle = Res("MdTableStyle");
        engine.TableHeaderStyle = Res("MdTableHeaderStyle");
        engine.TableBodyStyle = Res("MdTableBodyStyle");
        engine.Heading1Style = Res("MdHeading1Style");
        engine.Heading2Style = Res("MdHeading2Style");
        engine.Heading3Style = Res("MdHeading3Style");
        engine.Heading4Style = Res("MdHeadingMinorStyle");
        engine.Heading5Style = Res("MdHeadingMinorStyle");
        engine.Heading6Style = Res("MdHeadingMinorStyle");
        engine.NormalParagraphStyle = Res("MdParagraphStyle");
        engine.CodeStyle = Res("MdInlineCodeStyle");
        engine.CodeBlockStyle = Res("MdCodeBlockStyle");
        engine.BlockquoteStyle = Res("MdBlockquoteStyle");
        engine.LinkStyle = Res("MdLinkStyle");

        // MdXaml parses Markdown -> FlowDocument once, at the moment the Markdown
        // property is first set, baking these styles in as local values at that
        // instant. For content bound in one shot (returning to an already-loaded
        // session, e.g. after navigating to Settings and back) that first parse
        // races Loaded and usually wins, so it runs with the Engine styles still
        // null - reverting to MdXaml's own illegible-on-dark defaults. Streamed
        // messages don't hit this, since Loaded fires while content is still
        // empty. Forcing one re-parse here, now that the styles are guaranteed
        // set, makes the result deterministic either way.
        var content = viewer.Markdown;
        if (!string.IsNullOrEmpty(content))
        {
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, string.Empty);
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, content);
        }

        if (viewer.Document != null && viewer.FindResource("TextPrimaryBrush") is Brush finalBrush)
        {
            viewer.Document.Foreground = finalBrush;
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

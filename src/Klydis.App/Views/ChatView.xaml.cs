using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

    private DateTime _lastScrollTime = DateTime.MinValue;
    private bool _scrollPending = false;

    private void ScrollToBottom(bool force)
    {
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
            _scrollViewer ??= FindScrollViewer(MessagesList);
            if (_scrollViewer == null)
            {
                return;
            }

            bool nearBottom = _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset < 120;
            if (force || nearBottom)
            {
                // Stop any in-flight wheel momentum first, otherwise its next frame
                // tick fires moments later and yanks the view back off the end.
                StopScrollAnimation();
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

    private ScrollViewer? FindNestedScrollViewer(DependencyObject? element)
    {
        _scrollViewer ??= FindScrollViewer(MessagesList);
        while (element != null && element != MessagesList)
        {
            if (element is ScrollViewer sv && sv != _scrollViewer)
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
        _scrollViewer ??= FindScrollViewer(MessagesList);
        if (_scrollViewer == null)
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
        if (_scrollViewer == null)
        {
            StopScrollAnimation();
            return;
        }

        var now = DateTime.Now;
        double dt = Math.Min((now - _lastScrollRenderTime).TotalSeconds, 0.05);
        _lastScrollRenderTime = now;

        double max = _scrollViewer.ScrollableHeight;
        double newOffset = _scrollViewer.VerticalOffset + _scrollVelocity * dt;

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

        _scrollViewer.ScrollToVerticalOffset(newOffset);

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

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Klydis.App.ViewModels;
using MdXaml;

namespace Klydis.App.Views;

/// <summary>
/// Interaction logic for ChatSidePanelView.xaml. The panel's DataContext is the owning
/// ChatViewModel; all panel state is reached through <c>SidePanel</c>. Code-behind only
/// handles the parts XAML cannot: WebBrowser navigation, log auto-scroll, and MdXaml
/// dark-theme table styling.
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
        // ScrollChanged is a routed event declared on ScrollViewer; TextBox only exposes it
        // through the bubbling route, so it must be subscribed in code rather than in XAML.
        LogBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogBox_ScrollChanged));
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
            _panel = null;
            _htmlHandler = null;
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatSidePanelViewModel.LogContent))
        {
            AutoScrollLogToEnd();
        }
    }

    private void LogBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Remember whether the user has scrolled up to read older lines; a new log tail must
        // not yank them back to the bottom while they are reading.
        _userScrolledLogUp = e.ExtentHeight > e.ViewportHeight && e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 24;
    }

    private void AutoScrollLogToEnd()
    {
        if (LogBox == null || _userScrolledLogUp) return;
        LogBox.ScrollToEnd();
    }

    private void MarkdownViewer_Loaded(object sender, RoutedEventArgs e)
    {
        // Same dark-theme styling pass as ChatView.MarkdownViewer_Loaded: MdXaml's built-in
        // renderer hardcodes light table striping and off-theme tints unless the Engine styles
        // are assigned, so apply the app's markdown styles once, then force a re-parse so the
        // styles are baked in even when the content was already set before Loaded.
        if (sender is not MarkdownScrollViewer viewer)
        {
            return;
        }

        Style Res(string key) => (Style)viewer.FindResource(key);

        var foreground = TextElement.GetForeground(viewer);
        if (foreground == null && viewer.FindResource("TextPrimaryBrush") is Brush textBrush)
        {
            foreground = textBrush;
        }
        if (foreground != null)
        {
            TextElement.SetForeground(viewer, foreground);
            if (viewer.Document != null)
            {
                viewer.Document.Foreground = foreground;
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

        var content = viewer.Markdown;
        if (!string.IsNullOrEmpty(content))
        {
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, string.Empty);
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, content);
        }

        if (viewer.Document != null && TextElement.GetForeground(viewer) is Brush finalBrush)
        {
            viewer.Document.Foreground = finalBrush;
        }
    }
}

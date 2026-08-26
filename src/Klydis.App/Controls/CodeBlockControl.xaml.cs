using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Klydis.App.Helpers;

namespace Klydis.App.Controls;

/// <summary>
/// A sleek, theme-adaptive code block control with language badge, copy button,
/// syntax highlighting, and horizontal scrolling.
/// </summary>
public partial class CodeBlockControl : UserControl
{
    public static readonly DependencyProperty CodeLanguageProperty = DependencyProperty.Register(
        nameof(CodeLanguage),
        typeof(string),
        typeof(CodeBlockControl),
        new PropertyMetadata(string.Empty, OnCodeLanguagePropertyChanged));

    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code),
        typeof(string),
        typeof(CodeBlockControl),
        new PropertyMetadata(string.Empty, OnCodePropertyChanged));

    public string CodeLanguage
    {
        get => (string)GetValue(CodeLanguageProperty);
        set => SetValue(CodeLanguageProperty, value);
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    private int _copyFeedbackCounter;

    public CodeBlockControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public CodeBlockControl(string code, string? codeLanguage) : this()
    {
        CodeLanguage = codeLanguage ?? string.Empty;
        Code = code ?? string.Empty;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateContent();
    }

    private static void OnCodeLanguagePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeBlockControl control)
        {
            control.UpdateContent();
        }
    }

    private static void OnCodePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CodeBlockControl control)
        {
            control.UpdateContent();
        }
    }

    public void UpdateContent()
    {
        // 1. Language badge formatting
        string rawLang = (CodeLanguage ?? string.Empty).Trim().ToLowerInvariant();
        LanguageLabel.Text = FormatLanguageName(rawLang);

        // 2. Syntax highlighting & code text
        string code = Code ?? string.Empty;
        bool isDark = IsDarkThemeActive();

        CodeParagraph.Inlines.Clear();
        var inlines = CodeSyntaxHighlighter.BuildInlines(code, rawLang, isDark);
        foreach (var inline in inlines)
        {
            CodeParagraph.Inlines.Add(inline);
        }
    }

    private static string FormatLanguageName(string lang) => lang switch
    {
        "powershell" or "pwsh" or "ps" or "ps1" => "POWERSHELL",
        "csharp" or "cs" or "c#" or "dotnet" => "C#",
        "cpp" or "c++" => "C++",
        "c" => "C",
        "python" or "py" => "PYTHON",
        "javascript" or "js" or "node" => "JAVASCRIPT",
        "typescript" or "ts" => "TYPESCRIPT",
        "jsx" or "react" => "REACT JSX",
        "tsx" => "REACT TSX",
        "json" or "jsonc" => "JSON",
        "xml" or "xaml" or "svg" => "XML / XAML",
        "html" or "htm" => "HTML",
        "css" or "scss" or "sass" or "less" => "CSS",
        "sql" or "tsql" or "mysql" or "pgsql" => "SQL",
        "bash" or "sh" or "zsh" or "shell" => "BASH",
        "cmd" or "bat" or "batch" => "BATCH",
        "rust" or "rs" => "RUST",
        "go" or "golang" => "GO",
        "java" => "JAVA",
        "kotlin" or "kt" => "KOTLIN",
        "yaml" or "yml" => "YAML",
        "toml" or "ini" => "CONFIG",
        "markdown" or "md" => "MARKDOWN",
        "" => "CODE",
        _ => lang.ToUpperInvariant()
    };

    private bool IsDarkThemeActive()
    {
        if (TryFindResource("TextPrimaryBrush") is SolidColorBrush brush)
        {
            var c = brush.Color;
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return lum > 0.45;
        }
        return true;
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Code))
        {
            return;
        }

        try
        {
            Clipboard.SetText(Code);

            // Visual feedback
            CopyIcon.Visibility = Visibility.Collapsed;
            CheckIcon.Visibility = Visibility.Visible;
            CopyText.Text = "Copied!";
            if (TryFindResource("SuccessBrush") is Brush successBrush)
            {
                CopyText.Foreground = successBrush;
            }

            int currentId = ++_copyFeedbackCounter;
            await Task.Delay(1500);

            if (_copyFeedbackCounter == currentId)
            {
                CheckIcon.Visibility = Visibility.Collapsed;
                CopyIcon.Visibility = Visibility.Visible;
                CopyText.Text = "Copy";
                if (TryFindResource("TextSecondaryBrush") is Brush textSecondaryBrush)
                {
                    CopyText.Foreground = textSecondaryBrush;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Code block clipboard copy failed: {ex.Message}");
        }
    }

    private void CodeScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (e.Delta > 0)
            {
                CodeScrollViewer.LineLeft();
            }
            else
            {
                CodeScrollViewer.LineRight();
            }
            e.Handled = true;
            return;
        }

        if (sender is UIElement element)
        {
            e.Handled = false;
            var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            var parent = VisualTreeHelper.GetParent(element) as UIElement;
            parent?.RaiseEvent(eventArg);
        }
    }
}

using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klydis.App.ViewModels;

public partial class ToolCallViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _commandText = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string _status = "pending"; // pending, running, done, failed

    [ObservableProperty]
    private string _output = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>
/// ViewModel for a single chat message bubble.
/// </summary>
public partial class ChatMessageViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrushKey))]
    private string _role = "user";

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _renderedContent = string.Empty;

    private DateTime _lastRenderUpdate = DateTime.MinValue;
    private bool _renderPending = false;

    partial void OnContentChanged(string? oldValue, string newValue)
    {
        Action doUpdate = () =>
        {
            RenderedContent = FormatMarkdown(newValue, IsStreaming);
        };

        if (!IsStreaming)
        {
            if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(doUpdate);
            }
            else
            {
                doUpdate();
            }
            return;
        }

        var now = DateTime.Now;
        if ((now - _lastRenderUpdate).TotalMilliseconds >= 50)
        {
            _lastRenderUpdate = now;
            if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(doUpdate);
            }
            else
            {
                doUpdate();
            }
        }
        else if (!_renderPending)
        {
            _renderPending = true;
            Action updateAction = async () =>
            {
                await Task.Delay(50);
                _renderPending = false;
                if (IsStreaming)
                {
                    _lastRenderUpdate = DateTime.Now;
                    var textToRender = Content;
                    Action applyRender = () => RenderedContent = FormatMarkdown(textToRender, IsStreaming);

                    if (System.Windows.Application.Current?.Dispatcher != null)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(applyRender, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    else
                    {
                        applyRender();
                    }
                }
            };

            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(updateAction, System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                Task.Run(updateAction);
            }
        }
    }

    partial void OnIsStreamingChanged(bool oldValue, bool newValue)
    {
        if (!newValue)
        {
            _renderPending = false;
            RenderedContent = FormatMarkdown(Content, false);
        }
    }

    private static string FormatMarkdown(string? text, bool isStreaming = false)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string formatted = text;

        // Ensure fenced code blocks have leading blank lines so MdXaml parses them as code blocks
        formatted = System.Text.RegularExpressions.Regex.Replace(
            formatted,
            @"(?<=[^\n])\n(```[a-zA-Z0-9_-]*)",
            "\n\n$1");

        // Ensure Markdown tables have leading and trailing blank lines (\n\n) so MdXaml engine parses them as FlowDocument Tables
        formatted = System.Text.RegularExpressions.Regex.Replace(
            formatted, 
            @"(?<!\n\n)(\n\|[^\n]+\|\n\|[\s:\-\|]+\|\n(?:\|[^\n]+\|\n)+)", 
            "\n\n$1\n\n");

        // During streaming, if there is an unclosed code fence, close it temporarily so the preview renders as a code block
        if (isStreaming)
        {
            int fenceCount = System.Text.RegularExpressions.Regex.Matches(formatted, @"```").Count;
            if (fenceCount % 2 != 0)
            {
                formatted += "\n```";
            }
        }

        return formatted;
    }

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;

    [ObservableProperty]
    private int _tokenCount;

    [ObservableProperty]
    private string _thinkingContent = string.Empty;

    [ObservableProperty]
    private bool _isThinkingExpanded;

    [ObservableProperty]
    private bool _hasToolCalls;

    [ObservableProperty]
    private bool _isCopied;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    public ObservableCollection<ToolCallViewModel> ToolCalls { get; } = new();

    public ObservableCollection<AttachmentItemViewModel> Attachments { get; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Content;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    public string BackgroundBrushKey => Role switch
    {
        "user" => "UserBubbleBackgroundBrush",
        "assistant" => "AssistantBubbleBackgroundBrush",
        "tool" => "ToolBubbleBackgroundBrush",
        "system" => "SystemBubbleBackgroundBrush",
        "error" => "ErrorBubbleBackgroundBrush",
        _ => "AssistantBubbleBackgroundBrush"
    };

    [RelayCommand]
    private async Task CopyToClipboardAsync()
    {
        if (string.IsNullOrEmpty(Content)) return;

        try
        {
            System.Windows.Clipboard.SetText(Content);
            IsCopied = true;
            await Task.Delay(1500);
            IsCopied = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard copy failed: {ex.Message}");
        }
    }
}

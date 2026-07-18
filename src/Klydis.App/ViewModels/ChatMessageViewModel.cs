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

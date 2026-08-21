using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.Core.Chat;

namespace Klydis.App.ViewModels;

/// <summary>
/// ViewModel wrapping a queued message for WPF UI representation.
/// </summary>
public partial class QueuedMessageViewModel : ObservableObject
{
    public Guid Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>Explicit processing position (0 = first) — the order shown in Manual mode and used by the sequencer.</summary>
    public int Position { get; init; }

    public ObservableCollection<AttachmentItemViewModel> Attachments { get; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    public string DisplayText => !string.IsNullOrWhiteSpace(Content) 
        ? Content 
        : (HasAttachments ? $"[Attached {Attachments.Count} item(s)]" : string.Empty);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBadgeText))]
    private QueuedMessageMode _mode;

    [ObservableProperty]
    private QueuedMessageStatus _status;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    public string ModeBadgeText => Mode == QueuedMessageMode.Steer ? "Steer" : "Direct Send";

    public QueuedMessageViewModel(QueuedMessage model)
    {
        Id = model.Id;
        SessionId = model.SessionId;
        Content = model.Content;
        Mode = model.Mode;
        Status = model.Status;
        CreatedAt = model.CreatedAt.ToLocalTime();
        Position = model.Position;

        if (model.Attachments != null && model.Attachments.Count > 0)
        {
            foreach (var att in model.Attachments)
            {
                Attachments.Add(AttachmentItemViewModel.FromQueuedAttachment(att));
            }
        }
    }

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
}

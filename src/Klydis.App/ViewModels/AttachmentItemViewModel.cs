using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.Core.Chat;

namespace Klydis.App.ViewModels;

public enum AttachmentType
{
    File,
    Image,
    Screenshot,
    Audio,
    TextContext
}

public partial class AttachmentItemViewModel : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private AttachmentType _type = AttachmentType.File;

    [ObservableProperty]
    private string _sizeDisplay = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private string _iconSymbol = "📄";

    public Action<AttachmentItemViewModel>? OnRemoveRequested { get; set; }

    [RelayCommand]
    private void Remove()
    {
        OnRemoveRequested?.Invoke(this);
    }

    public QueuedMessageAttachment ToQueuedAttachment()
    {
        return new QueuedMessageAttachment
        {
            Id = Id,
            FileName = FileName,
            FilePath = FilePath,
            Type = Type.ToString(),
            SizeDisplay = SizeDisplay,
            Content = Content
        };
    }

    public static AttachmentItemViewModel FromQueuedAttachment(QueuedMessageAttachment qa)
    {
        if (!Enum.TryParse<AttachmentType>(qa.Type, true, out var type))
        {
            type = AttachmentType.File;
        }

        string icon = type switch
        {
            AttachmentType.Image => "🖼️",
            AttachmentType.Screenshot => "📷",
            AttachmentType.Audio => "🎙️",
            AttachmentType.TextContext => "📝",
            _ => "📄"
        };

        BitmapSource? thumb = null;
        if ((type == AttachmentType.Image || type == AttachmentType.Screenshot) && !string.IsNullOrEmpty(qa.FilePath) && File.Exists(qa.FilePath))
        {
            thumb = LoadThumbnail(qa.FilePath);
        }

        return new AttachmentItemViewModel
        {
            FileName = qa.FileName,
            FilePath = qa.FilePath,
            Type = type,
            SizeDisplay = qa.SizeDisplay,
            Content = qa.Content,
            IconSymbol = icon,
            Thumbnail = thumb
        };
    }

    public static AttachmentItemViewModel FromFile(string path)
    {
        var fileInfo = new FileInfo(path);
        string ext = fileInfo.Extension.ToLowerInvariant();
        
        AttachmentType type = AttachmentType.File;
        string icon = "📄";

        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif")
        {
            type = AttachmentType.Image;
            icon = "🖼️";
        }
        else if (ext is ".wav" or ".mp3" or ".m4a" or ".ogg" or ".flac" or ".aac")
        {
            type = AttachmentType.Audio;
            icon = "🎙️";
        }
        else if (ext is ".cs" or ".py" or ".js" or ".ts" or ".html" or ".css" or ".cpp" or ".c" or ".h" or ".json" or ".xml" or ".md" or ".txt" or ".sql" or ".sh" or ".bat" or ".ps1")
        {
            type = AttachmentType.File;
            icon = "💻";
        }

        BitmapSource? thumb = null;
        if (type == AttachmentType.Image)
        {
            thumb = LoadThumbnail(path);
        }

        string sizeStr = FormatFileSize(fileInfo.Length);
        string fileContent = string.Empty;

        // If small text file, pre-read content for context window injection
        if (type != AttachmentType.Image && type != AttachmentType.Audio && fileInfo.Length < 250 * 1024)
        {
            try
            {
                fileContent = File.ReadAllText(path);
            }
            catch { }
        }

        return new AttachmentItemViewModel
        {
            FileName = fileInfo.Name,
            FilePath = path,
            Type = type,
            SizeDisplay = sizeStr,
            Thumbnail = thumb,
            IconSymbol = icon,
            Content = fileContent
        };
    }

    public static AttachmentItemViewModel FromScreenshot(string path, BitmapSource bitmap)
    {
        var fileInfo = new FileInfo(path);
        return new AttachmentItemViewModel
        {
            FileName = fileInfo.Name,
            FilePath = path,
            Type = AttachmentType.Screenshot,
            SizeDisplay = FormatFileSize(fileInfo.Length),
            Thumbnail = bitmap,
            IconSymbol = "📷"
        };
    }

    public static AttachmentItemViewModel FromTextContext(string title, string text)
    {
        return new AttachmentItemViewModel
        {
            FileName = string.IsNullOrWhiteSpace(title) ? "Context Snippet" : title,
            FilePath = string.Empty,
            Type = AttachmentType.TextContext,
            SizeDisplay = $"{text.Length} chars",
            Content = text,
            IconSymbol = "📝"
        };
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = 120;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

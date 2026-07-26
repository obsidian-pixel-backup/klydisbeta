using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private AttachmentType _type = AttachmentType.File;

    [ObservableProperty]
    private string? _content;

    [ObservableProperty]
    private string _sizeDisplay = string.Empty;

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

    public static AttachmentItemViewModel FromFile(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        string ext = fileInfo.Extension.ToLowerInvariant();
        AttachmentType type = AttachmentType.File;
        string icon = "📄";
        BitmapSource? thumb = null;
        string content = string.Empty;

        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif")
        {
            type = AttachmentType.Image;
            icon = "🖼️";
            thumb = LoadThumbnail(filePath);
        }
        else if (ext is ".mp3" or ".wav" or ".m4a" or ".ogg" or ".flac" or ".aac" or ".wma")
        {
            type = AttachmentType.Audio;
            icon = "🎙️";
        }
        else if (ext is ".cs" or ".py" or ".js" or ".ts" or ".html" or ".css" or ".json" or ".md" or ".txt" or ".xml" or ".yaml" or ".yml" or ".sql" or ".cpp" or ".h" or ".c" or ".java" or ".sh" or ".ps1" or ".bat")
        {
            type = AttachmentType.File;
            icon = "💻";
            try
            {
                if (fileInfo.Length < 500_000) // limit inline reading to 500KB
                {
                    content = File.ReadAllText(filePath);
                }
            }
            catch { }
        }

        string sizeStr;
        if (fileInfo.Length < 1024)
            sizeStr = $"{fileInfo.Length} B";
        else if (fileInfo.Length < 1024 * 1024)
            sizeStr = $"{fileInfo.Length / 1024.0:F1} KB";
        else
            sizeStr = $"{fileInfo.Length / (1024.0 * 1024.0):F1} MB";

        return new AttachmentItemViewModel
        {
            FileName = fileInfo.Name,
            FilePath = filePath,
            Type = type,
            SizeDisplay = sizeStr,
            IconSymbol = icon,
            Thumbnail = thumb,
            Content = content
        };
    }

    public static AttachmentItemViewModel FromImage(BitmapSource bitmap, string label = "Screenshot")
    {
        return new AttachmentItemViewModel
        {
            FileName = $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            Type = label.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) ? AttachmentType.Screenshot : AttachmentType.Image,
            SizeDisplay = $"{bitmap.PixelWidth}×{bitmap.PixelHeight}",
            IconSymbol = "📷",
            Thumbnail = bitmap
        };
    }

    public static AttachmentItemViewModel FromTextContext(string title, string textContent)
    {
        int lineCount = textContent.Split('\n').Length;
        return new AttachmentItemViewModel
        {
            FileName = string.IsNullOrWhiteSpace(title) ? "Context Snippet" : title,
            Type = AttachmentType.TextContext,
            Content = textContent,
            SizeDisplay = $"{lineCount} lines ({textContent.Length} chars)",
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
            bitmap.DecodePixelWidth = 160; // downscale thumbnail
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
}

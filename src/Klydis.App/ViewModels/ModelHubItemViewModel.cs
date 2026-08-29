using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klydis.App.ViewModels;

/// <summary>
/// Unified ViewModel representing a model item in the modern Model Hub (remote or local).
/// </summary>
public partial class ModelHubItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _repoId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _modelName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _author = string.Empty;

    /// <summary>
    /// What a screen reader announces for this row. Without it the list item falls back to
    /// object.ToString() and every model reads as the type name.
    /// </summary>
    public string AutomationName =>
        string.IsNullOrWhiteSpace(Author) ? ModelName : $"{ModelName} by {Author}";

    [ObservableProperty]
    private bool _isLocal;

    [ObservableProperty]
    private string _localModelId = string.Empty;

    [ObservableProperty]
    private string _localFilePath = string.Empty;

    [ObservableProperty]
    private string _localFileSize = string.Empty;

    [ObservableProperty]
    private string _avatarBackground = "#3B82F6";

    [ObservableProperty]
    private string _avatarForeground = "#FFFFFF";

    [ObservableProperty]
    private string _avatarLetter = "M";

    [ObservableProperty]
    private string _dotColor = "#3B82F6";

    [ObservableProperty]
    private bool _isDotActive = true;

    [ObservableProperty]
    private int _likes;

    [ObservableProperty]
    private int _downloads;

    [ObservableProperty]
    private DateTimeOffset _lastModified = DateTimeOffset.UtcNow;

    [ObservableProperty]
    private string _timeAgo = string.Empty;

    [ObservableProperty]
    private string _releaseDate = string.Empty;

    [ObservableProperty]
    private string _parameterSize = "Unknown";

    [ObservableProperty]
    private string _architecture = "Transformers";

    [ObservableProperty]
    private string _contextLength = "32K";

    [ObservableProperty]
    private string _license = "mit";

    [ObservableProperty]
    private string _pipelineTag = string.Empty;

    [ObservableProperty]
    private string[] _tags = [];

    [ObservableProperty]
    private bool _isVision;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private bool _isCode;

    [ObservableProperty]
    private bool _isConversational = true;

    [ObservableProperty]
    private bool _isEmbedding;

    [ObservableProperty]
    private ObservableCollection<string> _capabilities = new();

    [ObservableProperty]
    private ObservableCollection<HfFileViewModel> _ggufFiles = new();

    [ObservableProperty]
    private HfFileViewModel? _selectedFile;

    [ObservableProperty]
    private string _readmeMarkdown = string.Empty;

    [ObservableProperty]
    private bool _isLoadingReadme;

    [ObservableProperty]
    private bool _isLoadingFiles;

    [ObservableProperty]
    private bool _hasLoadedFiles;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private ActiveDownloadViewModel? _activeDownload;

    [ObservableProperty]
    private string _role = "None";

    [ObservableProperty]
    private bool _isCompatible = true;

    [ObservableProperty]
    private string? _compatibilityWarning;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    public void CancelDownload()
    {
        ActiveDownload?.CancellationTokenSource.Cancel();
    }

    public Func<ModelHubItemViewModel, bool, Task>? LoadFilesAction { get; set; }
    public Func<ModelHubItemViewModel, Task>? LoadReadmeAction { get; set; }

    public static ModelHubItemViewModel FromHfModel(Klydis.Core.Models.HfModelInfo info, int totalVramMb = 0)
    {
        var item = new ModelHubItemViewModel
        {
            RepoId = info.RepoId,
            Author = info.Author,
            ModelName = info.ModelName,
            Downloads = info.Downloads,
            Likes = info.Likes,
            LastModified = info.LastModified,
            Tags = info.Tags ?? [],
            PipelineTag = info.PipelineTag ?? string.Empty,
            IsLocal = false
        };

        item.ComputeDerivedAttributes();
        return item;
    }

    public static ModelHubItemViewModel FromLocalModel(Klydis.Core.Models.ModelInfo model, Klydis.Core.Hardware.GpuInfo? gpuInfo)
    {
        var estimatedVramMb = (int)(model.EstimatedVramMb ?? 0L);
        var compat = Klydis.Core.Inference.GgufCompatibilityAdapter.Evaluate(model.FilePath);
        bool isCompatible = compat.IsSupported;
        string? compatWarning = isCompatible ? null : (compat.WarningMessage ?? "Model is not compatible with native engine.");

        string author = "local";
        string modelName = model.DisplayName;
        if (model.DisplayName.Contains('/'))
        {
            var parts = model.DisplayName.Split('/');
            author = parts[0];
            modelName = parts[1];
        }

        var item = new ModelHubItemViewModel
        {
            RepoId = model.DisplayName,
            ModelName = modelName,
            Author = author,
            LocalModelId = model.Id,
            LocalFilePath = model.FilePath,
            LocalFileSize = (model.FileSizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
            ParameterSize = model.ParameterCount?.ToString("F1", CultureInfo.InvariantCulture) ?? "Unknown",
            Architecture = model.Architecture ?? "Transformers",
            ContextLength = model.ContextLength.HasValue ? FormatContextLength((int)model.ContextLength.Value) : "32K",
            Role = model.Role ?? "None",
            IsLocal = true,
            IsCompatible = isCompatible,
            CompatibilityWarning = compatWarning,
            LastModified = model.InstalledAt != default ? model.InstalledAt : DateTime.UtcNow
        };

        item.ComputeDerivedAttributes();

        // Add file entry for local file
        var fileVm = new HfFileViewModel
        {
            FileName = model.FileName,
            Size = item.LocalFileSize,
            QuantType = model.QuantizationType ?? "GGUF",
            RepoId = model.DisplayName,
            CanFitInVram = gpuInfo == null || estimatedVramMb <= gpuInfo.TotalVramMb
        };
        item.GgufFiles.Add(fileVm);
        item.SelectedFile = fileVm;
        item.HasLoadedFiles = true;

        item.ReadmeMarkdown = BuildLocalReadmeMarkdown(model);

        return item;
    }

    public void ComputeDerivedAttributes()
    {
        // Avatar color and letter
        AssignAvatarTheme();

        // Time ago
        TimeAgo = FormatRelativeTime(LastModified);
        ReleaseDate = LastModified.ToString("MMM yyyy", CultureInfo.InvariantCulture);

        // Tags and capabilities
        string fullText = $"{RepoId} {string.Join(" ", Tags)} {PipelineTag}".ToLowerInvariant();

        IsVision = fullText.Contains("vision") || fullText.Contains("vl") || fullText.Contains("llava") ||
                   fullText.Contains("pixtral") || fullText.Contains("image") || fullText.Contains("multimodal");

        IsThinking = fullText.Contains("think") || fullText.Contains("-r1") || fullText.Contains("reasoning") ||
                     fullText.Contains("chain-of-thought");

        IsCode = fullText.Contains("code") || fullText.Contains("coder") || fullText.Contains("dev") || fullText.Contains("instruct");

        IsEmbedding = fullText.Contains("embed") || fullText.Contains("bge") || fullText.Contains("bert") || PipelineTag.Contains("feature-extraction");

        IsConversational = !IsEmbedding;

        // Capabilities collection for pill rendering
        Capabilities.Clear();
        if (IsEmbedding)
        {
            Capabilities.Add("Feature Extraction");
            Capabilities.Add("Embedding");
        }
        else
        {
            if (IsCode) Capabilities.Add("Coding Agent");
            if (IsVision) Capabilities.Add("Vision");
            if (IsThinking) Capabilities.Add("Reasoning");
            Capabilities.Add("Text Generation");
            Capabilities.Add("Conversational");
        }

        // Extract Parameter size if unknown
        if (ParameterSize == "Unknown" || string.IsNullOrEmpty(ParameterSize))
        {
            var pSize = Klydis.Core.Models.HuggingFaceClient.ExtractParameterSize(RepoId, Tags);
            if (pSize.HasValue)
            {
                ParameterSize = $"{pSize.Value:0.#}B";
            }
            else
            {
                var match = Regex.Match(RepoId, @"(?i)(\d+(?:\.\d+)?[BMK])");
                if (match.Success)
                {
                    ParameterSize = match.Groups[1].Value.ToUpperInvariant();
                }
            }
        }

        // Extract Architecture
        if (Architecture == "Transformers" || string.IsNullOrEmpty(Architecture))
        {
            if (fullText.Contains("qwen")) Architecture = "Qwen";
            else if (fullText.Contains("llama")) Architecture = "Llama";
            else if (fullText.Contains("gemma")) Architecture = "Gemma";
            else if (fullText.Contains("deepseek")) Architecture = "DeepSeek";
            else if (fullText.Contains("mistral") || fullText.Contains("mixtral")) Architecture = "Mistral";
            else if (fullText.Contains("phi")) Architecture = "Phi";
            else if (fullText.Contains("nemotron")) Architecture = "Nemotron";
            else Architecture = "Transformers";
        }

        // License
        License = ExtractLicense(Tags, RepoId);
    }

    private void AssignAvatarTheme()
    {
        string auth = Author.ToLowerInvariant();
        string repo = RepoId.ToLowerInvariant();

        if (auth.Contains("unsloth"))
        {
            AvatarBackground = "#2563EB"; // Royal Blue
            AvatarLetter = "U";
            DotColor = "#3B82F6";
        }
        else if (auth.Contains("ornith"))
        {
            AvatarBackground = "#059669"; // Emerald
            AvatarLetter = "O";
            DotColor = "#10B981";
        }
        else if (auth.Contains("mixedbread"))
        {
            AvatarBackground = "#D97706"; // Amber / Orange
            AvatarLetter = "M";
            DotColor = "#F59E0B";
        }
        else if (auth.Contains("davidau"))
        {
            AvatarBackground = "#B45309"; // Bronze
            AvatarLetter = "D";
            DotColor = "#F59E0B";
        }
        else if (auth.Contains("hauhaucs"))
        {
            AvatarBackground = "#D97706"; // Amber
            AvatarLetter = "H";
            DotColor = "#F59E0B";
        }
        else if (auth.Contains("lmstudio"))
        {
            AvatarBackground = "#EA580C"; // Rust Orange
            AvatarLetter = "L";
            DotColor = "#FB923C";
        }
        else if (auth.Contains("andrez") || auth.Contains("deepseek"))
        {
            AvatarBackground = "#0D9488"; // Teal
            AvatarLetter = "A";
            DotColor = "#14B8A6";
        }
        else if (auth.Contains("nvidia") || auth.Contains("nemotron"))
        {
            AvatarBackground = "#16A34A"; // Nvidia Green
            AvatarLetter = "N";
            DotColor = "#22C55E";
        }
        else if (auth.Contains("google") || repo.Contains("gemma"))
        {
            AvatarBackground = "#2563EB"; // Blue
            AvatarLetter = "G";
            DotColor = "#60A5FA";
        }
        else if (auth.Contains("meta") || repo.Contains("llama"))
        {
            AvatarBackground = "#0284C7"; // Sky Blue
            AvatarLetter = "M";
            DotColor = "#38BDF8";
        }
        else if (auth.Contains("bartowski"))
        {
            AvatarBackground = "#7C3AED"; // Violet
            AvatarLetter = "B";
            DotColor = "#A78BFA";
        }
        else if (auth.Contains("qwen") || repo.Contains("qwen"))
        {
            AvatarBackground = "#6366F1"; // Indigo
            AvatarLetter = "Q";
            DotColor = "#818CF8";
        }
        else
        {
            // Deterministic palette selection
            string[] palette = ["#059669", "#0D9488", "#2563EB", "#7C3AED", "#D97706", "#EA580C", "#DC2626", "#4F46E5"];
            int hash = Math.Abs(Author.GetHashCode());
            AvatarBackground = palette[hash % palette.Length];
            AvatarLetter = !string.IsNullOrEmpty(Author) ? Author[0].ToString().ToUpperInvariant() : "M";
            DotColor = AvatarBackground;
        }
    }

    private static string FormatRelativeTime(DateTimeOffset dt)
    {
        var span = DateTimeOffset.UtcNow - dt;
        if (span.TotalDays < 1) return "today";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }

    private static string FormatContextLength(int length)
    {
        if (length >= 1024)
        {
            return $"{(length / 1024)}K";
        }
        return length.ToString();
    }

    private static string ExtractLicense(string[] tags, string repoId)
    {
        if (tags != null)
        {
            foreach (var t in tags)
            {
                if (t.StartsWith("license:", StringComparison.OrdinalIgnoreCase))
                {
                    return t.Substring("license:".Length).Trim();
                }
            }
        }

        string full = $"{repoId} {string.Join(" ", tags ?? [])}".ToLowerInvariant();
        if (full.Contains("mit")) return "mit";
        if (full.Contains("apache")) return "apache-2.0";
        if (full.Contains("gpl")) return "gpl-3.0";
        if (full.Contains("llama")) return "llama3";
        if (full.Contains("openrail")) return "openrail";
        return "mit";
    }

    private static string BuildLocalReadmeMarkdown(Klydis.Core.Models.ModelInfo model)
    {
        return $@"# {model.DisplayName}

Local inference model installed in your local library.

### Details & Specifications
- **Architecture**: `{model.Architecture ?? "Transformers"}`
- **Quantization**: `{model.QuantizationType ?? "GGUF"}`
- **Parameter Size**: `{model.ParameterCount?.ToString("F1") ?? "Unknown"}B`
- **Context Length**: `{model.ContextLength ?? 8192} tokens`
- **File Path**: `{model.FilePath}`
- **File Size**: `{(model.FileSizeBytes / (1024.0 * 1024.0 * 1024.0)):F2} GB`

### Role Allocation
Assigned role: **{model.Role ?? "General"}**
";
    }
}

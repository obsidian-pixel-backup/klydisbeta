using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.Core.Chat;

namespace Klydis.App.ViewModels;

public partial class SessionInfo : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _title = "New Chat";

    [ObservableProperty]
    private string _lastMessagePreview = "";

    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isEditingTitle;
}

/// <summary>
/// ViewModel for the Chat interface.
/// </summary>
public partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly ChatEngine? _chatEngine;
    private CancellationTokenSource? _generationCts;
    private CancellationTokenSource? _modelLoadCts;
    private string? _generatingSessionId;
    private long _modelLoadSequenceId = 0;
    private bool _userExplicitlyUnloaded;
    private bool _isProcessingQueue;
    private EventHandler? _queueChangedHandler;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _isModelLoading;

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    private double _tokensPerSecond;

    [ObservableProperty]
    private string _selectedModelId = string.Empty;

    [ObservableProperty]
    private RiskLevel _selectedRiskLevel;

    [ObservableProperty]
    private string _sessionTitle = "New Chat";

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    private TaskCompletionSource<bool>? _approvalTcs;

    [ObservableProperty]
    private bool _isApprovalPending;

    [ObservableProperty]
    private string _pendingApprovalTitle = "Tool Approval Requested";

    [ObservableProperty]
    private string _pendingApprovalMessage = string.Empty;

    [ObservableProperty]
    private string _pendingToolName = string.Empty;

    [ObservableProperty]
    private string _pendingToolArguments = string.Empty;

    [ObservableProperty]
    private bool _isAlertPending;

    [ObservableProperty]
    private string _alertTitle = "Notice";

    [ObservableProperty]
    private string _alertMessage = string.Empty;

    [ObservableProperty]
    private QueuedMessageMode _selectedQueueMode = QueuedMessageMode.Steer;

    [ObservableProperty]
    private bool _hasQueuedMessages;

    [ObservableProperty]
    private bool _hasPendingAttachments;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public ObservableCollection<RiskLevel> AvailableRiskLevels { get; } = new();
    public ObservableCollection<QueuedMessageViewModel> QueuedMessages { get; } = new();
    public ObservableCollection<AttachmentItemViewModel> PendingAttachments { get; } = new();

    private readonly Klydis.Core.Models.ModelRegistry _registry;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;

    private readonly Klydis.Core.Hardware.GpuProfiler _gpuProfiler;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Hardware.OffloadStrategy _offloadStrategy;
    private readonly Klydis.Core.Memory.MessageStore _messageStore;
    private readonly ToolExecutor _toolExecutor;
    private readonly ModelMessageQueue? _messageQueue;
    private readonly Klydis.Core.Skills.DynamicSkillSelector? _skillSelector;
    private readonly SemaphoreSlim _modelLoadGate = new(1, 1);

    public ChatViewModel(
        ChatEngine chatEngine,
        Klydis.Core.Models.ModelRegistry registry,
        Klydis.Core.Inference.InferenceEngine inferenceEngine,
        Klydis.Core.Hardware.GpuProfiler gpuProfiler,
        Klydis.Core.Hardware.SystemProfiler systemProfiler,
        Klydis.Core.Hardware.OffloadStrategy offloadStrategy,
        Klydis.Core.Memory.MessageStore messageStore,
        ToolExecutor toolExecutor,
        ModelMessageQueue? messageQueue = null,
        Klydis.Core.Skills.DynamicSkillSelector? skillSelector = null)
    {
        _chatEngine = chatEngine;
        _registry = registry;
        _inferenceEngine = inferenceEngine;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        _messageStore = messageStore;
        _toolExecutor = toolExecutor;
        _messageQueue = messageQueue;
        _skillSelector = skillSelector;

        if (_messageQueue != null)
        {
            _queueChangedHandler = (s, e) => RefreshQueueUI();
            _messageQueue.QueueChanged += _queueChangedHandler;
        }

        _toolExecutor.ToolApprovalHandlerAsync = ShowToolApprovalDialogAsync;

        AvailableRiskLevels.Add(RiskLevel.Safe);
        AvailableRiskLevels.Add(RiskLevel.Standard);
        AvailableRiskLevels.Add(RiskLevel.AutoPilot);
        SelectedRiskLevel = _toolExecutor.CurrentRiskLevel;
        
        PendingAttachments.CollectionChanged += (_, _) => HasPendingAttachments = PendingAttachments.Count > 0;

        RefreshModels();
        _registry.RegistryChanged += OnRegistryChanged;
        _inferenceEngine.ModelStateChanged += OnModelStateChanged;
        _ = InitializeSessionsAsync();
    }

    public void Dispose()
    {
        if (_messageQueue != null && _queueChangedHandler != null)
        {
            _messageQueue.QueueChanged -= _queueChangedHandler;
            _queueChangedHandler = null;
        }
        _registry.RegistryChanged -= OnRegistryChanged;
        _inferenceEngine.ModelStateChanged -= OnModelStateChanged;
        GC.SuppressFinalize(this);
    }

    private void OnModelStateChanged(bool isLoaded, string? modelPath)
    {
        Action updateUi = () =>
        {
            IsModelReady = isLoaded;
            if (isLoaded && !string.IsNullOrEmpty(modelPath))
            {
                var modelInfo = _registry.GetAllModels().FirstOrDefault(m => m.FilePath == modelPath);
                if (modelInfo != null && SelectedModelId != modelInfo.DisplayName)
                {
                    SelectedModelId = modelInfo.DisplayName;
                }
            }
            else if (!isLoaded && !_userExplicitlyUnloaded)
            {
                IsModelReady = false;
            }
        };

        if (System.Windows.Application.Current != null)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(updateUi);
        }
        else
        {
            updateUi();
        }
    }

    private void OnRegistryChanged()
    {
        if (System.Windows.Application.Current != null)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshModels);
        }
    }

    [RelayCommand]
    private void ApproveTool()
    {
        DismissPendingApproval(true);
    }

    [RelayCommand]
    private void DenyTool()
    {
        DismissPendingApproval(false);
    }

    [RelayCommand]
    private void CloseAlertModal()
    {
        IsAlertPending = false;
    }

    private void DismissPendingApproval(bool result)
    {
        if (IsApprovalPending)
        {
            IsApprovalPending = false;
            _approvalTcs?.TrySetResult(result);
            _approvalTcs = null;
        }
    }

    public void ShowAlert(string title, string message)
    {
        Action action = () =>
        {
            AlertTitle = title;
            AlertMessage = message;
            IsAlertPending = true;
        };

        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(action);
        }
        else
        {
            action();
        }
    }

    private async Task<bool> ShowToolApprovalDialogAsync(ToolCallRequest request)
    {
        var argsSb = new StringBuilder();
        if (request.Arguments != null && request.Arguments.Count > 0)
        {
            foreach (var kvp in request.Arguments)
            {
                if (argsSb.Length > 0) argsSb.Append("\n");
                argsSb.Append($"{kvp.Key}: {kvp.Value}");
            }
        }

        var tcs = new TaskCompletionSource<bool>();

        Action showModal = () =>
        {
            DismissPendingApproval(false);

            _approvalTcs = tcs;
            PendingToolName = request.Name;
            PendingToolArguments = argsSb.ToString();
            PendingApprovalTitle = "Tool Approval Requested";
            PendingApprovalMessage = $"The model is attempting to execute '{request.Name}'. Allow this operation?";
            IsApprovalPending = true;
        };

        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(showModal);
        }
        else
        {
            showModal();
        }

        return await tcs.Task;
    }



    partial void OnSelectedRiskLevelChanged(RiskLevel value)
    {
        if (_toolExecutor != null)
        {
            _toolExecutor.CurrentRiskLevel = value;
        }
    }

    private async Task InitializeSessionsAsync()
    {
        var dbSessions = await _messageStore.GetSessionsAsync();

        if (System.Windows.Application.Current != null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Sessions.Clear();
                foreach (var session in dbSessions)
                {
                    Sessions.Add(new SessionInfo
                    {
                        Id = session.Id,
                        Title = CleanTitle(session.Title),
                        LastMessagePreview = "",
                        Timestamp = session.UpdatedAt,
                        IsPinned = session.IsPinned
                    });
                }
            });
        }
        else
        {
            Sessions.Clear();
            foreach (var session in dbSessions)
            {
                Sessions.Add(new SessionInfo
                {
                    Id = session.Id,
                    Title = CleanTitle(session.Title),
                    LastMessagePreview = "",
                    Timestamp = session.UpdatedAt,
                    IsPinned = session.IsPinned
                });
            }
        }

        if (Sessions.Count > 0)
        {
            SelectedSession = Sessions[0];
        }
        else
        {
            await CreateNewSessionAsync();
        }
    }

    async partial void OnSelectedModelIdChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        
        await _modelLoadGate.WaitAsync();
        try
        {
            long seqId = Interlocked.Increment(ref _modelLoadSequenceId);

            var oldCts = _modelLoadCts;
            var newCts = new CancellationTokenSource();
            var ct = newCts.Token;
            _modelLoadCts = newCts;

            try { oldCts?.Cancel(); } catch { }

            _userExplicitlyUnloaded = false;

            var modelInfo = _registry.GetAllModels().FirstOrDefault(m => m.DisplayName == value);
            if (modelInfo != null)
            {
                if (_inferenceEngine.IsModelLoaded && _inferenceEngine.CurrentModelPath == modelInfo.FilePath)
                {
                    // Prevent infinite loop when WPF clears ComboBox ItemsSource
                    IsModelLoading = false;
                    IsModelReady = true;
                    return;
                }

                try
                {
                    // Load state is app chrome, not conversation content: it is shown
                    // in the header model chip and status bar, never in the transcript.
                    IsModelLoading = true;
                    IsModelReady = false;

                    // Unload any existing model asynchronously to free VRAM and cancel ongoing generation
                    await _inferenceEngine.UnloadModelAsync(ct);
                    if (ct.IsCancellationRequested || seqId != Volatile.Read(ref _modelLoadSequenceId)) return;
                    
                    // Set architecture for prompt templating
                    _inferenceEngine.Architecture = modelInfo.Architecture ?? "llama";

                    // Load new model using hardware-aware plan
                    var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
                    var systemInfo = await _systemProfiler.GetSystemInfoAsync();
                    if (ct.IsCancellationRequested || seqId != Volatile.Read(ref _modelLoadSequenceId)) return;

                    var metadata = Klydis.Core.Models.GgufMetadataReader.Parse(modelInfo.FilePath);
                    int totalLayers = metadata != null && metadata.BlockCount.HasValue && metadata.BlockCount.Value > 0 ? (int)metadata.BlockCount.Value : 32;
                    long layerSizeBytes = modelInfo.FileSizeBytes / Math.Max(1, totalLayers);
                    int rawContextLength = (int)(metadata?.ContextLength ?? 4096);
                    int contextLength = Math.Clamp(rawContextLength, 2048, 16384);
                    
                    long kvCachePerLayerBytes = 2048;
                    if (metadata != null)
                    {
                        var kvEst = Klydis.Core.Inference.KvCacheCalculator.Calculate(metadata, 1, Klydis.Core.Inference.KvCacheQuantizationType.Q4_0);
                        kvCachePerLayerBytes = (long)Math.Max(512, kvEst.BytesPerToken / Math.Max(1, kvEst.NumLayers));
                    }

                    var plan = _offloadStrategy.CalculatePlan(
                        totalLayers, layerSizeBytes, kvCachePerLayerBytes, contextLength, gpuInfo, systemInfo, Klydis.Core.Hardware.OffloadStrategyType.FullGpu);
                    
                    if (ct.IsCancellationRequested || seqId != Volatile.Read(ref _modelLoadSequenceId)) return;

                    await _inferenceEngine.LoadModelAsync(modelInfo.FilePath, plan);
                    if (ct.IsCancellationRequested || seqId != Volatile.Read(ref _modelLoadSequenceId)) return;

                    // Isolate speculative draft model attachment into background task so draft failures never break main model loading
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _inferenceEngine.AttachSpeculativeDraftAsync(modelInfo.FilePath);
                        }
                        catch (Exception draftEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Speculative draft attachment failed: {draftEx}");
                        }
                    });

                    if (ct.IsCancellationRequested || seqId != Volatile.Read(ref _modelLoadSequenceId)) return;

                    if (System.Windows.Application.Current != null)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (seqId == Volatile.Read(ref _modelLoadSequenceId))
                            {
                                IsModelLoading = false;
                                IsModelReady = true;
                                ProcessNextQueuedMessageIfAvailable();
                            }
                        });
                    }
                    else
                    {
                        if (seqId == Volatile.Read(ref _modelLoadSequenceId))
                        {
                            IsModelLoading = false;
                            IsModelReady = true;
                            ProcessNextQueuedMessageIfAvailable();
                        }
                    }

                    // Remember the previously loaded model
                    var updatedModel = modelInfo with { LastUsedAt = DateTime.UtcNow };
                    await _registry.UpsertModelAsync(updatedModel);
                }
                catch (OperationCanceledException)
                {
                    // Task canceled by a newer selection change
                }
                catch (Exception ex)
                {
                    if (seqId != Volatile.Read(ref _modelLoadSequenceId)) return;

                    string nativeLog = "";
                    try
                    {
                        if (System.IO.File.Exists("llama_native.log"))
                        {
                            using var fs = new System.IO.FileStream("llama_native.log", System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                            if (fs.Length > 0)
                            {
                                long offset = Math.Max(0, fs.Length - 4096);
                                fs.Seek(offset, System.IO.SeekOrigin.Begin);
                                using var reader = new System.IO.StreamReader(fs);
                                var tailText = reader.ReadToEnd();
                                var lines = tailText.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(10);
                                nativeLog = "\n\nNative Log:\n" + string.Join("\n", lines);
                            }
                        }
                    }
                    catch { }

                    System.Diagnostics.Debug.WriteLine($"Failed to load model: {ex}");
                    
                    if (System.Windows.Application.Current != null)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (seqId == Volatile.Read(ref _modelLoadSequenceId))
                            {
                                IsModelLoading = false;
                                IsModelReady = false;
                                Messages.Add(new ChatMessageViewModel { Role = "error", Content = $"Failed to load model: {ex.Message}{nativeLog}", Timestamp = DateTime.Now });
                            }
                        });
                    }
                    else
                    {
                        if (seqId == Volatile.Read(ref _modelLoadSequenceId))
                        {
                            IsModelLoading = false;
                            IsModelReady = false;
                            Messages.Add(new ChatMessageViewModel { Role = "error", Content = $"Failed to load model: {ex.Message}{nativeLog}", Timestamp = DateTime.Now });
                        }
                    }
                }
                finally
                {
                    if (seqId == Volatile.Read(ref _modelLoadSequenceId))
                    {
                        if (System.Windows.Application.Current != null)
                        {
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                IsModelLoading = false;
                            });
                        }
                        else
                        {
                            IsModelLoading = false;
                        }
                    }
                }
            }
        }
        finally
        {
            _modelLoadGate.Release();
        }
    }

    async partial void OnSelectedSessionChanged(SessionInfo? value)
    {
        if (value != null)
        {
            await SelectSessionAsync(value);
        }
    }

    private void RefreshModels()
    {
        var currentSelected = SelectedModelId;
        AvailableModels.Clear();
        
        var models = _registry.GetAllModels().OrderBy(m => m.DisplayName).ToList();
        
        foreach (var model in models)
        {
            AvailableModels.Add(model.DisplayName);
        }
        
        if (!string.IsNullOrEmpty(currentSelected) && AvailableModels.Contains(currentSelected))
        {
            SelectedModelId = currentSelected;
        }
        else if (AvailableModels.Count > 0 && !_userExplicitlyUnloaded)
        {
            var mostRecentlyUsed = _registry.GetAllModels().OrderByDescending(m => m.LastUsedAt).FirstOrDefault();
            if (mostRecentlyUsed != null)
            {
                SelectedModelId = mostRecentlyUsed.DisplayName;
            }
            else
            {
                SelectedModelId = AvailableModels[0];
            }
        }
        else
        {
            SelectedModelId = string.Empty;
        }
    }

    [RelayCommand]
    private async Task UnloadModelAsync()
    {
        await _inferenceEngine.UnloadModelAsync();
        IsModelReady = false;
        _userExplicitlyUnloaded = true;
        SelectedModelId = string.Empty;
    }

    [RelayCommand]
    private void AttachFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Attach Files or Code to Chat Context",
            Filter = "All Supported Files|*.cs;*.py;*.js;*.ts;*.html;*.css;*.json;*.md;*.txt;*.xml;*.sql;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.wav;*.mp3;*.m4a;*.ogg;*.flac|Documents & Code|*.cs;*.py;*.js;*.ts;*.html;*.css;*.json;*.md;*.txt;*.xml;*.sql;*.c;*.cpp;*.h;*.sh;*.bat;*.ps1|Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|Audio Clips|*.wav;*.mp3;*.m4a;*.ogg;*.flac;*.aac|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                AddAttachmentFromPath(file);
            }
        }
    }

    [RelayCommand]
    private void AttachImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Attach Image to Chat",
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                AddAttachmentFromPath(file);
            }
        }
    }

    [RelayCommand]
    private void AttachAudio()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Attach Audio Clip",
            Filter = "Audio Files (*.wav;*.mp3;*.m4a;*.ogg;*.flac;*.aac)|*.wav;*.mp3;*.m4a;*.ogg;*.flac;*.aac|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                AddAttachmentFromPath(file);
            }
        }
    }

    [RelayCommand]
    private void AddTextContext()
    {
        var win = new Klydis.App.Views.TextContextWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (win.ShowDialog() == true)
        {
            var item = AttachmentItemViewModel.FromTextContext(win.ContextTitle, win.ContextText);
            item.OnRemoveRequested = RemoveAttachment;
            PendingAttachments.Add(item);
        }
    }

    [RelayCommand]
    private async Task CaptureScreenshotAsync()
    {
        try
        {
            int width = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
            int height = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Klydis_Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            using (var bmp = new System.Drawing.Bitmap(width, height))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                }
                bmp.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            var bitmapSource = new System.Windows.Media.Imaging.BitmapImage();
            bitmapSource.BeginInit();
            bitmapSource.UriSource = new Uri(tempPath, UriKind.Absolute);
            bitmapSource.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmapSource.EndInit();
            bitmapSource.Freeze();

            var item = AttachmentItemViewModel.FromScreenshot(tempPath, bitmapSource);
            item.OnRemoveRequested = RemoveAttachment;
            PendingAttachments.Add(item);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Screenshot capture failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentItemViewModel? item)
    {
        if (item != null && PendingAttachments.Contains(item))
        {
            PendingAttachments.Remove(item);
        }
    }

    [RelayCommand]
    private void ClearPendingAttachments()
    {
        PendingAttachments.Clear();
    }

    public void AddAttachmentFromPath(string path)
    {
        if (System.IO.File.Exists(path))
        {
            var item = AttachmentItemViewModel.FromFile(path);
            item.OnRemoveRequested = RemoveAttachment;
            PendingAttachments.Add(item);
        }
    }

    public void AddAttachmentFromImage(System.Windows.Media.Imaging.BitmapSource bitmapSource, string namePrefix = "Pasted_Image")
    {
        try
        {
            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{namePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            using (var fileStream = new System.IO.FileStream(tempPath, System.IO.FileMode.Create))
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
                encoder.Save(fileStream);
            }

            var item = AttachmentItemViewModel.FromScreenshot(tempPath, bitmapSource);
            item.OnRemoveRequested = RemoveAttachment;
            PendingAttachments.Add(item);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Adding image attachment failed: {ex.Message}");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0)
            return;

        if (IsGenerating || IsModelLoading)
        {
            EnqueueMessage();
            return;
        }

        var userMessage = InputText;
        InputText = string.Empty;

        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();

        await SendMessageForTextAsync(userMessage, attachments);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ForceSendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0)
            return;

        var userMessage = InputText;
        InputText = string.Empty;

        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();

        if (IsGenerating)
        {
            // Gracefully cancel active generation to interrupt active model loop
            Cancel();
            if (_chatEngine != null)
            {
                await _chatEngine.CancelActiveGenerationAsync();
            }

            // Small delay to allow active loop background task to finalize partial response history
            await Task.Delay(200);
        }

        await SendMessageForTextAsync(userMessage, attachments);
    }

    private async Task SendMessageForTextAsync(string userMessage, List<AttachmentItemViewModel>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(userMessage) && (attachments == null || attachments.Count == 0))
            return;

        string promptMessagePayload = userMessage;

        if (attachments != null && attachments.Count > 0)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                sb.AppendLine(userMessage);
                sb.AppendLine();
            }

            foreach (var att in attachments)
            {
                if (att.Type == AttachmentType.TextContext || (att.Type == AttachmentType.File && !string.IsNullOrEmpty(att.Content)))
                {
                    sb.AppendLine($"--- Attached Context: {att.FileName} ---");
                    sb.AppendLine(att.Content);
                    sb.AppendLine("----------------------------------------");
                    sb.AppendLine();
                }
                else if (att.Type == AttachmentType.Image || att.Type == AttachmentType.Screenshot)
                {
                    sb.AppendLine($"[Attached Image/Screenshot: {att.FileName} ({att.FilePath})]");
                }
                else if (att.Type == AttachmentType.Audio)
                {
                    sb.AppendLine($"[Attached Audio Clip: {att.FileName} ({att.FilePath})]");
                }
                else if (att.Type == AttachmentType.File)
                {
                    sb.AppendLine($"[Attached File: {att.FileName} ({att.FilePath}) - {att.SizeDisplay}]");
                }
            }

            promptMessagePayload = sb.ToString().TrimEnd();
        }

        var userMsgVm = new ChatMessageViewModel
        {
            Role = "user",
            Content = string.IsNullOrWhiteSpace(userMessage) && (attachments?.Count > 0) ? $"[Attached {attachments.Count} item(s)]" : userMessage,
            Timestamp = DateTime.Now
        };

        if (attachments != null)
        {
            foreach (var att in attachments)
            {
                userMsgVm.Attachments.Add(att);
            }
        }

        Messages.Add(userMsgVm);

        IsGenerating = true;
        var localGeneratingSessionId = SelectedSession?.Id;
        _generatingSessionId = localGeneratingSessionId;
        _generationCts = new CancellationTokenSource();

        // Bubbles are created lazily and appended in the exact order events
        // arrive, so text, thinking and tool activity stay chronological even
        // when the engine runs multiple tool iterations.
        ChatMessageViewModel? assistantMessage = null;
        ChatMessageViewModel? thoughtMessage = null;
        ToolCallViewModel? pendingToolCall = null;
        var fullAssistantText = new StringBuilder();

        // The generating indicator lives in the transcript as the always-last
        // item, where the response will materialize — not pinned above the input.
        var typingIndicator = new ChatMessageViewModel { Role = "typing", Timestamp = DateTime.Now };
        if (SelectedSession?.Id == localGeneratingSessionId)
        {
            Messages.Add(typingIndicator);
        }

        void OnUi(Action action)
        {
            if (localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
            {
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal);
                }
                else
                {
                    action();
                }
            }
        }

        void AppendMessage(ChatMessageViewModel message)
        {
            if (localGeneratingSessionId == null || SelectedSession?.Id != localGeneratingSessionId) return;
            int indicatorIdx = Messages.IndexOf(typingIndicator);
            if (indicatorIdx >= 0)
            {
                Messages.Insert(indicatorIdx, message);
            }
            else
            {
                Messages.Add(message);
            }
        }

        void CloseAssistantBubble()
        {
            if (assistantMessage != null)
            {
                assistantMessage.IsStreaming = false;
                if (string.IsNullOrWhiteSpace(assistantMessage.Content))
                {
                    Messages.Remove(assistantMessage);
                }
                assistantMessage = null;
            }
        }

        void CloseThoughtBubble()
        {
            if (thoughtMessage != null)
            {
                thoughtMessage.IsStreaming = false;
                // Wait for a real token to collapse it, so it remains open if stream ends here
            }
        }

        try
        {
            if (_chatEngine != null)
            {
                string? skillContext = null;
                if (_skillSelector != null)
                {
                    var brainIndex = _skillSelector.GenerateBrainIndex();
                    var skillReasoning = _skillSelector.ReasonAndSelectSkills(promptMessagePayload);
                    
                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(brainIndex))
                    {
                        sb.AppendLine(brainIndex.Trim());
                    }
                    if (skillReasoning.SelectedSkills.Count > 0)
                    {
                        sb.AppendLine(skillReasoning.FormattedPromptInjection.Trim());
                        OnUi(() =>
                        {
                            var skillReasoningMsg = new ChatMessageViewModel
                            {
                                Role = "thought",
                                Content = skillReasoning.ReasoningExplanation,
                                IsThinkingExpanded = true,
                                Timestamp = DateTime.Now
                            };
                            AppendMessage(skillReasoningMsg);
                        });
                    }
                    if (sb.Length > 0)
                    {
                        skillContext = sb.ToString();
                    }
                }

                await foreach (var evt in _chatEngine.StreamResponseAsync(promptMessagePayload, _generationCts.Token, skillContext))
                {

                    switch (evt.Type)
                    {
                        case ChatStreamEventType.Token:
                            fullAssistantText.Append(evt.Content);
                            OnUi(() =>
                            {
                                if (thoughtMessage != null)
                                {
                                    if (string.IsNullOrWhiteSpace(thoughtMessage.Content))
                                    {
                                        Messages.Remove(thoughtMessage);
                                    }
                                    else
                                    {
                                        thoughtMessage.IsThinkingExpanded = false;
                                    }
                                    thoughtMessage = null;
                                }
                                if (assistantMessage == null)
                                {
                                    assistantMessage = new ChatMessageViewModel
                                    {
                                        Role = "assistant",
                                        Content = string.Empty,
                                        IsStreaming = true,
                                        Timestamp = DateTime.Now
                                    };
                                    AppendMessage(assistantMessage);
                                }
                                assistantMessage.Content += evt.Content;
                            });
                            break;
                        case ChatStreamEventType.ThinkingStart:
                            OnUi(() =>
                            {
                                CloseAssistantBubble();
                                if (thoughtMessage != null)
                                {
                                    thoughtMessage.IsThinkingExpanded = false;
                                    thoughtMessage.IsStreaming = false;
                                }
                                thoughtMessage = new ChatMessageViewModel
                                {
                                    Role = "thought",
                                    Content = string.Empty,
                                    IsStreaming = true,
                                    IsThinkingExpanded = true,
                                    Timestamp = DateTime.Now
                                };
                                AppendMessage(thoughtMessage);
                            });
                            break;
                        case ChatStreamEventType.ThinkingToken:
                            OnUi(() =>
                            {
                                if (thoughtMessage == null)
                                {
                                    CloseAssistantBubble();
                                    thoughtMessage = new ChatMessageViewModel
                                    {
                                        Role = "thought",
                                        Content = string.Empty,
                                        IsStreaming = true,
                                        IsThinkingExpanded = true,
                                        Timestamp = DateTime.Now
                                    };
                                    AppendMessage(thoughtMessage);
                                }
                                thoughtMessage.Content += evt.Content;
                            });
                            break;
                        case ChatStreamEventType.ThinkingEnd:
                            OnUi(() =>
                            {
                                if (thoughtMessage != null)
                                {
                                    if (string.IsNullOrWhiteSpace(thoughtMessage.Content))
                                    {
                                        Messages.Remove(thoughtMessage);
                                        thoughtMessage = null;
                                    }
                                    else
                                    {
                                        CloseThoughtBubble();
                                    }
                                }
                            });
                            break;
                        case ChatStreamEventType.MemorySummarizing:
                            OnUi(() =>
                            {
                                CloseAssistantBubble();
                                var memoryMsg = new ChatMessageViewModel
                                {
                                    Role = "thought",
                                    Content = evt.Content,
                                    IsThinkingExpanded = true,
                                    IsStreaming = true,
                                    Timestamp = DateTime.Now
                                };
                                AppendMessage(memoryMsg);
                            });
                            break;
                        case ChatStreamEventType.ToolCall:
                            OnUi(() =>
                            {
                                CloseAssistantBubble();
                                if (thoughtMessage != null)
                                {
                                    if (string.IsNullOrWhiteSpace(thoughtMessage.Content))
                                    {
                                        Messages.Remove(thoughtMessage);
                                    }
                                    else
                                    {
                                        thoughtMessage.IsThinkingExpanded = false;
                                        thoughtMessage.IsStreaming = false;
                                    }
                                    thoughtMessage = null;
                                }
                                var toolMessage = new ChatMessageViewModel
                                {
                                    Role = "toolcall",
                                    HasToolCalls = true,
                                    Timestamp = DateTime.Now
                                };

                                string formattedArgs = string.Empty;
                                string commandTextPreview = string.Empty;

                                if (evt.Metadata != null && evt.Metadata.TryGetValue("Arguments", out var rawArgsObj) && rawArgsObj is IDictionary<string, object> argsDict)
                                {
                                    var lines = new List<string>();
                                    foreach (var kvp in argsDict)
                                    {
                                        var unwrappedVal = Klydis.Core.Chat.ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString() ?? string.Empty;
                                        lines.Add($"{kvp.Key}: {unwrappedVal}");
                                        if (kvp.Key.Equals("command", StringComparison.OrdinalIgnoreCase) || kvp.Key.Equals("path", StringComparison.OrdinalIgnoreCase) || kvp.Key.Equals("query", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (string.IsNullOrEmpty(commandTextPreview))
                                                commandTextPreview = unwrappedVal;
                                        }
                                    }
                                    formattedArgs = string.Join("\n", lines);
                                    if (string.IsNullOrEmpty(commandTextPreview) && lines.Count > 0)
                                    {
                                        commandTextPreview = lines[0];
                                    }
                                }

                                pendingToolCall = new ToolCallViewModel 
                                { 
                                    Name = evt.Content, 
                                    CommandText = commandTextPreview,
                                    Arguments = formattedArgs,
                                    Status = "running", 
                                    Output = "Executing...",
                                    IsExpanded = true 
                                };
                                toolMessage.ToolCalls.Add(pendingToolCall);
                                AppendMessage(toolMessage);
                            });
                            break;
                        case ChatStreamEventType.ToolResult:
                            OnUi(() =>
                            {
                                if (pendingToolCall != null)
                                {
                                    bool success = evt.Metadata == null
                                        || !evt.Metadata.TryGetValue("Success", out var flag)
                                        || flag is not bool ok
                                        || ok;
                                    pendingToolCall.Status = success ? "done" : "failed";
                                    pendingToolCall.Output = string.IsNullOrWhiteSpace(evt.Content) ? (success ? "Done (No output returned)" : "Failed") : evt.Content;
                                    pendingToolCall.IsExpanded = true;
                                    pendingToolCall = null;
                                }
                            });
                            break;
                        case ChatStreamEventType.Error:
                            OnUi(() =>
                            {
                                CloseAssistantBubble();
                                if (thoughtMessage != null)
                                {
                                    if (string.IsNullOrWhiteSpace(thoughtMessage.Content))
                                    {
                                        Messages.Remove(thoughtMessage);
                                    }
                                    else
                                    {
                                        thoughtMessage.IsThinkingExpanded = false;
                                    }
                                }
                                CloseThoughtBubble();
                                if (pendingToolCall != null)
                                {
                                    pendingToolCall.Status = "failed";
                                    pendingToolCall.Output = evt.Content;
                                    pendingToolCall.IsExpanded = true;
                                    pendingToolCall = null;
                                }
                                AppendMessage(new ChatMessageViewModel
                                {
                                    Role = "error",
                                    Content = evt.Content,
                                    Timestamp = DateTime.Now
                                });
                            });
                            break;
                        case ChatStreamEventType.StreamEnd:
                            OnUi(() =>
                            {
                                if (thoughtMessage != null)
                                {
                                    if (string.IsNullOrWhiteSpace(thoughtMessage.Content))
                                    {
                                        Messages.Remove(thoughtMessage);
                                    }
                                    else
                                    {
                                        thoughtMessage.IsStreaming = false;
                                    }
                                    thoughtMessage = null;
                                }
                                CloseAssistantBubble();
                                if (pendingToolCall != null)
                                {
                                    pendingToolCall = null;
                                }
                            });
                            break;
                    }
                }
            }
            else
            {
                // Dummy streaming
                await Task.Delay(500);
                AppendMessage(new ChatMessageViewModel
                {
                    Role = "assistant",
                    Content = "This is a placeholder response since no engine was provided.",
                    Timestamp = DateTime.Now
                });
            }
        }
        catch (OperationCanceledException)
        {
            if (localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
            {
                OnUi(() => AppendMessage(new ChatMessageViewModel
                {
                    Role = "system",
                    Content = "Generation canceled.",
                    Timestamp = DateTime.Now
                }));
            }
        }
        catch (Exception ex)
        {
            if (localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
            {
                OnUi(() => AppendMessage(new ChatMessageViewModel
                {
                    Role = "error",
                    Content = ex.Message,
                    Timestamp = DateTime.Now
                }));
            }
        }
        finally
        {
            if (localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
            {
                OnUi(() =>
                {
                    CloseAssistantBubble();
                    if (thoughtMessage != null)
                    {
                        if (string.IsNullOrWhiteSpace(thoughtMessage.Content))
                        {
                            Messages.Remove(thoughtMessage);
                        }
                        else
                        {
                            CloseThoughtBubble();
                        }
                    }
                    Messages.Remove(typingIndicator);
                });
            }
            bool isCancelled = _generationCts != null && _generationCts.IsCancellationRequested;
            IsGenerating = false;
            _generatingSessionId = null;
            _generationCts?.Dispose();
            _generationCts = null;

            // Auto-rename chat if it is the first interaction
            var responseText = fullAssistantText.ToString();
            if (localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId && SessionTitle == "New Chat" && Messages.Count >= 2 && !string.IsNullOrWhiteSpace(responseText))
            {
                _ = Task.Run(async () =>
                {
                    var newTitle = CleanTitle(await _chatEngine!.GenerateTitleAsync(userMessage, responseText));
                    if (!string.IsNullOrEmpty(newTitle) && newTitle != "New Chat" && SelectedSession?.Id == localGeneratingSessionId)
                    {
                        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            SessionTitle = newTitle;
                            SelectedSession.Title = newTitle;
                        });
                        await _messageStore.UpdateSessionAsync(SelectedSession.Id, newTitle, null, null, null);
                    }
                });
            }

            // If generation was not explicitly cancelled, auto-process next queued message
            if (!isCancelled)
            {
                ProcessNextQueuedMessageIfAvailable(localGeneratingSessionId);
            }
        }
    }

    private void ProcessNextQueuedMessageIfAvailable(string? targetSessionId = null)
    {
        if (IsGenerating || !IsModelReady || _isProcessingQueue) return;
        var sessionId = targetSessionId ?? SelectedSession?.Id ?? string.Empty;
        if (string.IsNullOrEmpty(sessionId)) return;

        // Ensure we only process if the target session is the active selected session
        if (SelectedSession?.Id != sessionId) return;

        var nextItem = _messageQueue?.GetNextDirectSend(sessionId) ?? _messageQueue?.GetNextPending(sessionId);
        if (nextItem != null)
        {
            _isProcessingQueue = true;
            IsGenerating = true;
            _messageQueue?.MarkStatus(nextItem.Id, QueuedMessageStatus.Processing);
            _messageQueue?.Remove(nextItem.Id);
            Action action = async () =>
            {
                try
                {
                    await Task.Delay(100);
                    await SendMessageForTextAsync(nextItem.Content);
                }
                finally
                {
                    _isProcessingQueue = false;
                }
            };

            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(action);
            }
            else
            {
                action();
            }
        }
    }

    private void RefreshQueueUI()
    {
        Action update = () =>
        {
            var sessionId = SelectedSession?.Id ?? string.Empty;
            var pending = _messageQueue?.GetPending(sessionId) ?? Array.Empty<QueuedMessage>();
            QueuedMessages.Clear();
            foreach (var item in pending)
            {
                QueuedMessages.Add(new QueuedMessageViewModel(item));
            }
            HasQueuedMessages = QueuedMessages.Count > 0;
        };

        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(update);
        }
        else
        {
            update();
        }
    }

    [RelayCommand]
    private void EnqueueMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;
        var text = InputText;
        InputText = string.Empty;
        var sessionId = SelectedSession?.Id ?? string.Empty;
        _messageQueue?.Enqueue(sessionId, text, SelectedQueueMode);
        ProcessNextQueuedMessageIfAvailable();
    }

    [RelayCommand]
    private void ClearQueue()
    {
        var sessionId = SelectedSession?.Id ?? string.Empty;
        _messageQueue?.Clear(sessionId);
        RefreshQueueUI();
    }

    [RelayCommand]
    private void ToggleQueueItemMode(QueuedMessageViewModel? item)
    {
        if (item != null)
        {
            _messageQueue?.ToggleMode(item.Id);
        }
    }

    [RelayCommand]
    private void RemoveQueuedItem(QueuedMessageViewModel? item)
    {
        if (item != null)
        {
            _messageQueue?.Remove(item.Id);
        }
    }

    /// <summary>
    /// ModelMessageQueue exposes no in-place content update, so an edit is applied as a
    /// remove-then-re-enqueue of the same session/mode with the new text. That means an
    /// edited item loses its original queue position and reappears at the back (queue
    /// order is by CreatedAt) - an accepted tradeoff to keep this change UI-only.
    /// </summary>
    [RelayCommand]
    private void SaveQueuedItemEdit(QueuedMessageViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.EditText)) return;

        var sessionId = item.SessionId;
        var mode = item.Mode;
        var newText = item.EditText.Trim();

        _messageQueue?.Remove(item.Id);
        _messageQueue?.Enqueue(sessionId, newText, mode);
    }

    [RelayCommand]
    private void ToggleSelectedQueueMode()
    {
        SelectedQueueMode = SelectedQueueMode == QueuedMessageMode.Steer ? QueuedMessageMode.DirectSend : QueuedMessageMode.Steer;
    }

    [RelayCommand]
    private async Task SubmitEditAsync(ChatMessageViewModel message)
    {
        if (SelectedSession == null || message.Role != "user" || IsGenerating) return;

        int uiIndex = Messages.IndexOf(message);
        if (uiIndex < 0) return;

        int occurrence = 0;
        for (int i = 0; i < uiIndex; i++)
        {
            if (Messages[i].Role == "user" && Messages[i].Content == message.Content)
                occurrence++;
        }

        var dbMessages = await _messageStore.GetMessagesAsync(SelectedSession.Id, null);
        
        int startDbIndex = -1;
        int dbOccurrence = 0;
        for (int i = 0; i < dbMessages.Count; i++)
        {
            if (dbMessages[i].Role.ToString().ToLowerInvariant() == "user" && dbMessages[i].Content == message.Content)
            {
                if (dbOccurrence == occurrence)
                {
                    startDbIndex = i;
                    break;
                }
                dbOccurrence++;
            }
        }

        if (startDbIndex >= 0)
        {
            for (int i = startDbIndex; i < dbMessages.Count; i++)
            {
                await _messageStore.DeleteMessageAsync(dbMessages[i].Id);
            }
        }

        InputText = message.EditText;
        message.IsEditing = false;
        await SelectSessionAsync(SelectedSession);
        
        await SendMessageAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        _generationCts?.Cancel();
        DismissPendingApproval(false);
    }

    [RelayCommand]
    private async Task CreateNewSessionAsync()
    {
        string title = "New Chat";
        string sessionId = await _messageStore.CreateSessionAsync(title, SelectedModelId);
        
        var newSession = new SessionInfo { Id = sessionId, Title = title, Timestamp = DateTime.Now };
        if (System.Windows.Application.Current != null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Sessions.Insert(0, newSession);
                SelectedSession = newSession;
            });
        }
        else
        {
            Sessions.Insert(0, newSession);
            SelectedSession = newSession;
        }
    }

    [RelayCommand]
    private async Task SelectSessionAsync(SessionInfo session)
    {
        if (session == null) return;
        SessionTitle = session.Title;
        Messages.Clear();
        
        var dbMessages = await _messageStore.GetMessagesAsync(session.Id, null);
        var chatEngineMessages = new List<ChatMessage>();
        
        foreach (var msg in dbMessages)
        {
            var roleStr = msg.Role.ToString().ToLowerInvariant();
            if (roleStr == "assistant")
            {
                var (thinking, content) = SplitThinkingContent(msg.Content);
                if (!string.IsNullOrEmpty(thinking))
                {
                    Messages.Add(new ChatMessageViewModel
                    {
                        Role = "thought",
                        Content = thinking,
                        IsThinkingExpanded = string.IsNullOrWhiteSpace(StripToolCallBlocks(content)),
                        Timestamp = msg.Timestamp
                    });
                }

                // Raw tool-call JSON is engine plumbing; keep it out of the transcript view.
                content = StripToolCallBlocks(content);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    Messages.Add(new ChatMessageViewModel
                    {
                        Role = "assistant",
                        Content = content,
                        Timestamp = msg.Timestamp
                    });
                }
            }
            else
            {
                Messages.Add(new ChatMessageViewModel
                {
                    Role = roleStr,
                    Content = msg.Content,
                    Timestamp = msg.Timestamp
                });
            }
            if (!msg.IsConsolidated)
            {
                chatEngineMessages.Add(new ChatMessage(msg.Role, msg.Content));
            }
        }
        
        _chatEngine?.LoadHistory(chatEngineMessages, session.Id);
        RefreshQueueUI();
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionInfo session)
    {
        if (session == null) return;

        if (IsGenerating && (session.Id == _generatingSessionId || session.Id == SelectedSession?.Id || session.Id == _chatEngine?.CurrentSessionId))
        {
            Cancel();
            if (_chatEngine != null)
            {
                await _chatEngine.CancelActiveGenerationAsync();
            }
        }

        _messageQueue?.Clear(session.Id);
        await _messageStore.DeleteSessionAsync(session.Id);
        Sessions.Remove(session);
        if (Sessions.Count > 0)
        {
            SelectedSession = Sessions[0];
        }
        else
        {
            await CreateNewSessionAsync();
        }
    }

    [RelayCommand]
    private async Task TogglePinSessionAsync(SessionInfo session)
    {
        if (session == null) return;
        session.IsPinned = !session.IsPinned;
        await _messageStore.UpdateSessionAsync(session.Id, null, null, null, session.IsPinned);
        
        // Re-sort the sessions collection based on IsPinned then Timestamp
        var sorted = new List<SessionInfo>(Sessions);
        sorted.Sort((a, b) =>
        {
            if (a.IsPinned && !b.IsPinned) return -1;
            if (!a.IsPinned && b.IsPinned) return 1;
            return b.Timestamp.CompareTo(a.Timestamp);
        });
        
        Sessions.Clear();
        foreach (var s in sorted) Sessions.Add(s);
        
        SelectedSession = session;
    }

    [RelayCommand]
    private void BeginEditTitle(SessionInfo session)
    {
        if (session != null)
        {
            session.IsEditingTitle = true;
        }
    }

    [RelayCommand]
    private async Task CommitEditTitleAsync(SessionInfo session)
    {
        if (session != null && session.IsEditingTitle)
        {
            session.IsEditingTitle = false;
            if (!string.IsNullOrWhiteSpace(session.Title))
            {
                await _messageStore.UpdateSessionAsync(session.Id, session.Title, null, null, null);
                if (SelectedSession?.Id == session.Id)
                {
                    SessionTitle = session.Title;
                }
            }
        }
    }

    [RelayCommand]
    private void CancelEditTitle(SessionInfo session)
    {
        if (session != null)
        {
            session.IsEditingTitle = false;
        }
    }

    [RelayCommand]
    private async Task ExportChatAsync()
    {
        if (SelectedSession == null) return;

        var dbMessages = await _messageStore.GetMessagesAsync(SelectedSession.Id, null);
        if (dbMessages == null || dbMessages.Count == 0) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{CleanTitle(SelectedSession.Title)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultExt = ".txt",
            Filter = "Text documents (.txt)|*.txt|All files (*.*)|*.*",
            Title = "Export Chat Log"
        };

        if (dialog.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Chat: {SelectedSession.Title}");
            sb.AppendLine($"Exported: {DateTime.Now}");
            sb.AppendLine(new string('=', 40));
            sb.AppendLine();

            foreach (var msg in dbMessages)
            {
                sb.AppendLine($"[{msg.Timestamp:yyyy-MM-dd HH:mm:ss}] {msg.Role.ToString().ToUpperInvariant()}:");
                sb.AppendLine(msg.Content);
                sb.AppendLine(new string('-', 40));
                sb.AppendLine();
            }

            try
            {
                await System.IO.File.WriteAllTextAsync(dialog.FileName, sb.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to export chat: {ex}");
                ShowAlert("Export Failed", $"Failed to export chat:\n{ex.Message}");
            }
        }
    }

    private static string CleanTitle(string title)
    {
        return TitleSanitizer.SanitizeTitle(title);
    }

    private static string StripToolCallBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var stripped = Regex.Replace(text, @"<\|?tool_call\|?>.*?(</\|?tool_call\|?>|<\|/tool_call\|>|$)", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    private static (string Thinking, string Content) SplitThinkingContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (string.Empty, text);

        var match = Regex.Match(text, @"<\|?(?:think|thought)\|?>(.*?)(?:</\|?(?:think|thought)\|?>|<\|/(?:think|thought)\|?>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(text, @"\[(?:THINK|THOUGHT)\](.*?)(?:\[/(?:THINK|THOUGHT)\]|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }
        if (match.Success)
        {
            string thinking = match.Groups[1].Value.Trim();
            string remaining = text.Remove(match.Index, match.Length).Trim();
            return (thinking, remaining);
        }

        return (string.Empty, text);
    }
}

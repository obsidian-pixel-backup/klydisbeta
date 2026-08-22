using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.App.Services;
using Klydis.Core.Chat;
using Klydis.Core.Diagnostics;
using Klydis.Core.Tasks;

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

    /// <summary>
    /// True while the model is actively processing a request in this session — drives the
    /// working indicator on the chat's sidebar item and header so the user can see which
    /// chat the model is working on (including when they have switched to another chat).
    /// </summary>
    [ObservableProperty]
    private bool _isWorking;

    /// <summary>
    /// Short human-readable status shown next to the working indicator, e.g. "Thinking…",
    /// "Running tool: search_web", "Tool finished".
    /// </summary>
    [ObservableProperty]
    private string _workingStatusText = string.Empty;

    /// <summary>
    /// Display name of the model currently processing this session (used for tooltips and
    /// the "working elsewhere" notice).
    /// </summary>
    [ObservableProperty]
    private string _workingModel = string.Empty;
}

/// <summary>
/// ViewModel for the Chat interface.
/// </summary>
public partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly ChatEngine? _chatEngine;
    private CancellationTokenSource? _generationCts;
    // P0: the in-flight turn's Task, captured so a replacement turn (ForceSend) can await the
    // previous turn's FULL completion (finally included) as a lifecycle barrier before
    // starting — replacing the old 200 ms sleep, which was not a synchronization barrier.
    private Task? _generationTask;
    // P0: serializes turn STARTS. Two racing ForceSend calls can both observe IsGenerating,
    // both capture the same previous _generationTask, and both start replacement turns — the
    // second would then begin without waiting for the first replacement. Every send entry
    // (Send, ForceSend, queue advancement, session/import paths) funnels through
    // SendMessageForTextAsync, so one gate serializes them all.
    private readonly System.Threading.SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _modelLoadCts;
    private string? _generatingSessionId;
    private long _modelLoadSequenceId = 0;
    private bool _userExplicitlyUnloaded;
    private bool _isProcessingQueue;
    private EventHandler? _queueChangedHandler;

    // The id of the session whose transcript is currently held in <see cref="Messages"/>.
    // Used to snapshot a generating session's live transcript when the user switches away
    // from it (at that point SelectedSession has already moved on, so the old session can't
    // be identified from the selection alone).
    private string? _displayedSessionId;

    // Live transcript snapshot of the session whose generation is in flight, captured when
    // the user switches away. Switching back to that chat restores this snapshot (the partial
    // assistant text is not persisted until the turn ends, so a DB reload would show a gap).
    private readonly Dictionary<string, List<ChatMessageViewModel>> _sessionTranscriptCache = new();

    // Session switching is serialized: rapid chat clicks cannot interleave two loads, a stale
    // (superseded) load never becomes the engine's active session, and a message sent right
    // after selecting a chat is guaranteed to land in THAT chat (see EnsureEngineSessionAsync).
    private readonly System.Threading.SemaphoreSlim _sessionLoadGate = new(1, 1);
    private long _sessionLoadSeq;

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
    [NotifyPropertyChangedFor(nameof(CurrentSettingsSummary))]
    private RiskLevel _selectedRiskLevel;

    [ObservableProperty]
    private string _sessionTitle = "New Chat";

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    /// <summary>
    /// Right-side chat panel (queue / files / changes / preview / terminal / notes). Owned here
    /// so it follows the selected session; the panel's view keeps ChatViewModel as its
    /// DataContext so queue commands and message bindings keep working unchanged.
    /// </summary>
    public ChatSidePanelViewModel SidePanel { get; }

    /// <summary>
    /// True when the model is generating in a DIFFERENT chat than the one currently shown.
    /// Drives a banner over the input so a "frozen" chat is explained instead of silently
    /// queueing the user's next message behind an invisible background generation.
    /// </summary>
    [ObservableProperty]
    private bool _isWorkingElsewhere;

    /// <summary>
    /// Human-readable "working elsewhere" notice (which chat/model is busy).
    /// </summary>
    [ObservableProperty]
    private string _workingElsewhereText = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(CurrentSettingsSummary))]
    private QueuedMessageMode _selectedQueueMode = QueuedMessageMode.Steer;

    /// <summary>
    /// Active personality mode for the chat (from UserStyle_Modes.md). Applied to the engine
    /// in real time — the next generation uses it immediately.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSettingsSummary))]
    private string _selectedPersonality = "Default";

    /// <summary>
    /// One-line summary of the current chat settings for the inline settings menu button,
    /// e.g. "Standard · Steer · Default".
    /// </summary>
    public string CurrentSettingsSummary => $"{SelectedRiskLevel} · {SelectedQueueMode} · {SelectedPersonality}";

    /// <summary>
    /// True when the left session-sidebar (chat list) is expanded. Drives the collapse/expand
    /// chevron in the chat header and the sidebar column width.
    /// </summary>
    [ObservableProperty]
    private bool _isSessionSidebarOpen = true;

    public ObservableCollection<string> AvailablePersonalities { get; } = new();

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

    /// <summary>
    /// Display order for the queued-messages panel. <see cref="QueueSortMode.Manual"/> (the
    /// default) mirrors the queue's actual processing order — the order set by drag-and-drop
    /// reordering. The other modes are presentation-only views over that same order.
    /// </summary>
    public enum QueueSortMode
    {
        Manual,
        OldestFirst,
        NewestFirst,
        ModeThenAge,
        Alphabetical
    }

    public sealed record QueueSortOption(QueueSortMode Mode, string Label);

    public ObservableCollection<QueueSortOption> QueueSortOptions { get; } = new()
    {
        new(QueueSortMode.Manual, "Manual (drag)"),
        new(QueueSortMode.OldestFirst, "Oldest first"),
        new(QueueSortMode.NewestFirst, "Newest first"),
        new(QueueSortMode.ModeThenAge, "Direct send first"),
        new(QueueSortMode.Alphabetical, "A → Z")
    };

    [ObservableProperty]
    private QueueSortOption _selectedQueueSortOption;

    private readonly Klydis.Core.Models.ModelRegistry _registry;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;

    private readonly Klydis.Core.Hardware.GpuProfiler _gpuProfiler;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Hardware.OffloadStrategy _offloadStrategy;
    private readonly Klydis.Core.Memory.MessageStore _messageStore;
    private readonly ToolExecutor _toolExecutor;
    private readonly ModelMessageQueue? _messageQueue;
    private readonly Klydis.Core.Skills.DynamicSkillSelector? _skillSelector;
    private readonly ThemeService? _themeService;
    private readonly SemaphoreSlim _modelLoadGate = new(1, 1);

    /// <summary>
    /// The task the chat engine is currently executing (null for conversation turns or when
    /// no task is active). The right-side workbench reads this to scope its projections
    /// (Changes, Files, Preview, Terminal) to the ACTIVE task — never session-wide state.
    /// </summary>
    public string? CurrentTaskId => _chatEngine?.CurrentTaskId;
    public Klydis.Core.Tasks.AgentRuntime? AgentRuntime => _chatEngine?.AgentRuntime;

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
        Klydis.Core.Skills.DynamicSkillSelector? skillSelector = null,
        ThemeService? themeService = null)
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
        _themeService = themeService;

        SidePanel = new ChatSidePanelViewModel(this, _messageStore, _toolExecutor);

        // Queue panel default: Manual order — the queue's actual processing order, which the
        // user edits by dragging items. Other sort modes are presentation-only views.
        SelectedQueueSortOption = QueueSortOptions[0];

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

        // Personality quick-switcher (UserStyle_Modes.md). Initialized from the persisted
        // setting so the inline menu reflects what the engine is actually using.
        foreach (var p in Klydis.Core.Chat.SystemPromptManager.GetAvailablePersonalities())
        {
            AvailablePersonalities.Add(p);
        }
        SelectedPersonality = _themeService?.SelectedPersonality ?? "Default";
        if (AvailablePersonalities.Count > 0 && !AvailablePersonalities.Contains(SelectedPersonality))
        {
            SelectedPersonality = AvailablePersonalities[0];
        }
        
        PendingAttachments.CollectionChanged += (_, _) => HasPendingAttachments = PendingAttachments.Count > 0;

        RefreshModels();
        _registry.RegistryChanged += OnRegistryChanged;
        _inferenceEngine.ModelStateChanged += OnModelStateChanged;
        FireAndForget.Observe(InitializeSessionsAsync(), operation: nameof(InitializeSessionsAsync));
    }

    public void Dispose()
    {
        SidePanel?.Dispose();
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
                // Only sync the ComboBox when no user-initiated load is in flight. Otherwise
                // every load completion writes SelectedModelId, which fires
                // OnSelectedModelIdChanged -> LoadModelAsync -> ... -> another completion event,
                // and the status bar/header flip between two model names forever (the observed
                // "alternating between models" symptom, with six full weight loads in a row in
                // the native log). While IsModelLoading is true the user's choice is what
                // governs; the writeback only serves the ModelLibrary path that loads outside
                // the chat ComboBox.
                if (!IsModelLoading)
                {
                    var modelInfo = _registry.GetAllModels().FirstOrDefault(m => m.FilePath == modelPath);
                    if (modelInfo != null && SelectedModelId != modelInfo.DisplayName)
                    {
                        SelectedModelId = modelInfo.DisplayName;
                    }
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

    partial void OnSelectedPersonalityChanged(string value)
    {
        string pName = string.IsNullOrWhiteSpace(value) ? "Default" : value;
        _themeService?.SavePersonalitySetting(pName);
        if (_chatEngine != null)
        {
            _chatEngine.SelectedPersonality = pName;
        }
        // Invalidate the KV cache so the next prompt rebuilds with the new personality
        // directives (same behavior as the Settings page personality picker).
        _inferenceEngine.ResetContext();
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
            _displayedSessionId = SelectedSession?.Id;
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
                    // Use the model's native context (bounded by the architecture ceiling) instead
                    // of an arbitrary 16K cap: the offload plan's VRAM math (safeVramContext)
                    // already protects the GPU, and hybrid/recurrent models (tiny KV caches) run
                    // fine at 64K+ on a 16GB card. The old `?? 4096` fallback + 16K clamp could
                    // hand the plan a tiny desired context, which — combined with a zero
                    // UserContextLimit on pooled/pre-settings engines — loaded the model at a 4K
                    // window and capped every generation at window − prompt − 512 ≈ 2K tokens
                    // (the observed long-horizon "terminates after ~2k tokens" failure).
                    string archLower = (metadata?.Architecture ?? "").ToLowerInvariant();
                    bool isHybridSsm = archLower is "qwen35" or "qwen3next" or "qwen35moe" or "mamba" or "rwkv" or "jamba";
                    int archCeiling = isHybridSsm ? 262144 : 131072;
                    int rawContextLength = metadata?.ContextLength is > 0
                        ? (int)metadata.ContextLength.Value
                        : (isHybridSsm ? 262144 : 65536);
                    int contextLength = Math.Clamp(rawContextLength, 2048, archCeiling);
                    
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
                    FireAndForget.Run(
                        () => _inferenceEngine.AttachSpeculativeDraftAsync(modelInfo.FilePath),
                        operation: "AttachSpeculativeDraftAsync");

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
                        // Native log lives in %LOCALAPPDATA%\Klydis\logs (see KlydisLog).
                        var tailText = Klydis.Core.Diagnostics.KlydisLog.ReadNativeLogTail();
                        if (!string.IsNullOrWhiteSpace(tailText))
                        {
                            var lines = tailText.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(10);
                            nativeLog = "\n\nNative Log:\n" + string.Join("\n", lines);
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

            // P0: REAL lifecycle barrier — await the previous turn's method until it has fully
            // unwound (streaming loop, tool cleanup, durable action completion, and its finally
            // block) before the replacement turn starts. The old 200 ms delay was not a
            // barrier: the old turn's finally could still be running after the new turn
            // replaced _generationCts, letting the old turn dispose the NEW turn's cancellation
            // source, clear IsGenerating, and close the new turn's bubbles — the observed
            // empty-response and cancellation races.
            var previousTurnTask = _generationTask;
            if (previousTurnTask != null && !previousTurnTask.IsCompleted)
            {
                try
                {
                    await previousTurnTask;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Previous generation task faulted during force-send barrier: {ex}");
                }
            }
        }

        await SendMessageForTextAsync(userMessage, attachments);
    }

    private async Task SendMessageForTextAsync(string userMessage, List<AttachmentItemViewModel>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(userMessage) && (attachments == null || attachments.Count == 0))
            return;

        // P0: one turn start at a time. The gate is held for the FULL turn (its finally
        // included), so a queued send or a racing ForceSend cannot begin until the previous
        // turn has completely unwound — the same barrier ForceSend awaits explicitly.
        await _sendGate.WaitAsync();
        try
        {
            // P0: capture the running turn task so ForceSendMessageAsync can await the FULL
            // turn (finally included) before replacing it. Shared generation state changes
            // hands only after the previous turn has fully unwound.
            _generationTask = SendMessageForTextCoreAsync(userMessage, attachments);
            await _generationTask;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendMessageForTextCoreAsync(string userMessage, List<AttachmentItemViewModel>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(userMessage) && (attachments == null || attachments.Count == 0))
            return;

        // The engine's active session must be the chat the user is looking at — otherwise the
        // message (and its response) would persist into whichever session was last loaded.
        await EnsureEngineSessionAsync();

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
        // P0: the turn holds its OWN cancellation source locally; the shared _generationCts
        // field is only the slot that the CURRENT turn owns. Every shared-state write in the
        // finally block is guarded by an ownership check (ReferenceEquals), so an old turn
        // unwinding after a replacement turn started can never dispose the new turn's source,
        // clear IsGenerating, or wipe the new turn's generation state.
        var localGenerationCts = new CancellationTokenSource();
        _generationCts = localGenerationCts;

        // Per-session working indicator: mark this chat as the one the model is working
        // on, so the sidebar/header shows it even if the user switches away mid-turn.
        if (localGeneratingSessionId != null)
        {
            var generatingSession = FindSession(localGeneratingSessionId);
            if (generatingSession != null)
            {
                DispatcherSafe(() => { generatingSession.WorkingModel = string.IsNullOrWhiteSpace(SelectedModelId) ? "model" : SelectedModelId; });
            }
            UpdateSessionWorkingState(localGeneratingSessionId, true, "Working…");
        }
        UpdateWorkingElsewhereText();

        // Bubbles are created lazily and appended in the exact order events
        // arrive, so text, thinking and tool activity stay chronological even
        // when the engine runs multiple tool iterations.
        ChatMessageViewModel? assistantMessage = null;
        ChatMessageViewModel? thoughtMessage = null;
        ToolCallViewModel? pendingToolCall = null;
        var fullAssistantText = new StringBuilder();
        // A turn "produced output" when it yielded visible text OR executed at least one tool
        // call (tool bubbles are real output even if the model wrote no final text). Gating the
        // queue auto-processing on this — instead of only on visible text — keeps legit
        // tool-driven turns chaining queued work while still halting the empty-response spiral.
        bool turnProducedOutput = false;

        // The generating indicator lives in the transcript as the always-last
        // item, where the response will materialize — not pinned above the input.
        var typingIndicator = new ChatMessageViewModel { Role = "typing", Timestamp = DateTime.Now };
        if (SelectedSession?.Id == localGeneratingSessionId)
        {
            Messages.Add(typingIndicator);
        }

        void OnUi(Action action)
        {
            // Fast-path check before scheduling. The AUTHORITATIVE check is re-run inside the
            // dispatcher callback: the user can switch sessions between scheduling and
            // execution, and a stale callback must never mutate the replacement session's
            // transcript, typing indicator, or bubbles.
            if (localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
            {
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (SelectedSession?.Id == localGeneratingSessionId)
                        {
                            action();
                        }
                    }, System.Windows.Threading.DispatcherPriority.Normal);
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

        // ── Streaming UI batching ──────────────────────────────────────────────
        // Per-token Dispatcher hops + `Content += token` string concatenation are O(n^2)
        // on the UI thread and a hop per token. Accumulate into local builders and flush
        // in chunks (or at every event boundary / stream end) instead.
        var pendingVisibleText = new StringBuilder();
        var pendingThoughtText = new StringBuilder();

        void FlushVisibleText()
        {
            if (pendingVisibleText.Length == 0) return;
            string chunk = pendingVisibleText.ToString();
            pendingVisibleText.Clear();
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
                assistantMessage.Content += chunk;
            });
        }

        void FlushThoughtText()
        {
            if (pendingThoughtText.Length == 0) return;
            string chunk = pendingThoughtText.ToString();
            pendingThoughtText.Clear();
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
                        // Reasoning stays behind the collapsed toggle: the transcript reads as
                        // the assistant's reply, and internal deliberation is one click away.
                        IsThinkingExpanded = false,
                        Timestamp = DateTime.Now
                    };
                    AppendMessage(thoughtMessage);
                }
                thoughtMessage.Content += chunk;
            });
        }

        // Flush both pending buffers, forcing any partial text to the UI before a
        // non-text event (tag transitions, tool calls, errors, stream end).
        void FlushAllPendingText()
        {
            FlushVisibleText();
            FlushThoughtText();
        }

        try
        {
            if (_chatEngine != null)
            {
                string? skillContext = null;
                // Interaction-mode gate: conversation turns (greetings, small talk, explanations)
                // get NO skill brain index and NO skill activation. Injecting the skill index on
                // every send over-conditioned the model — it started treating a greeting as an
                // agent turn (see InteractionClassifier). Task/Autonomous turns keep the full
                // brain + relevance-scored skill injection.
                if (_skillSelector != null && InteractionClassifier.Classify(promptMessagePayload) != InteractionMode.Conversation)
                {
                    var brainIndex = _skillSelector.GenerateBrainIndex();
                    var skillReasoning = await _skillSelector.ReasonAndSelectSkillsAsync(promptMessagePayload, ct: localGenerationCts.Token);
                    
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

                await foreach (var evt in _chatEngine.StreamResponseAsync(promptMessagePayload, localGenerationCts.Token, skillContext))
                {

                    switch (evt.Type)
                    {
                        case ChatStreamEventType.Token:
                            turnProducedOutput = true;
                            fullAssistantText.Append(evt.Content);
                            pendingVisibleText.Append(evt.Content);
                            // Flush in chunks to bound Dispatcher hops; the rest lands at
                            // the next event boundary or stream end.
                            if (pendingVisibleText.Length >= 200)
                            {
                                FlushVisibleText();
                            }
                            break;
                        case ChatStreamEventType.ThinkingStart:
                            UpdateSessionWorkingState(localGeneratingSessionId, true, "Thinking…");
                            FlushAllPendingText();
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
                                    // Collapsed by default — see FlushThoughtText.
                                    IsThinkingExpanded = false,
                                    Timestamp = DateTime.Now
                                };
                                AppendMessage(thoughtMessage);
                            });
                            break;
                        case ChatStreamEventType.ThinkingToken:
                            pendingThoughtText.Append(evt.Content);
                            if (pendingThoughtText.Length >= 200)
                            {
                                FlushThoughtText();
                            }
                            break;
                        case ChatStreamEventType.ThinkingEnd:
                            FlushAllPendingText();
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
                            UpdateSessionWorkingState(localGeneratingSessionId, true, "Summarizing context…");
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
                            turnProducedOutput = true;
                            UpdateSessionWorkingState(localGeneratingSessionId, true, $"Running tool: {evt.Content}");
                            FlushAllPendingText();
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
                            UpdateSessionWorkingState(localGeneratingSessionId, true, "Tool finished");
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
                            FlushAllPendingText();
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
                            FlushAllPendingText();
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
            // P0: only surface the cancellation notice if this turn still owns the slot — a
            // stale turn must not inject UI state into a session a replacement turn now owns.
            if (ReferenceEquals(_generationCts, localGenerationCts) &&
                localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
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
            // P0: ownership-guarded like the cancellation path above.
            if (ReferenceEquals(_generationCts, localGenerationCts) &&
                localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
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
            // Flush any text still buffered in the chunked UI updates before the
            // bubbles are closed/settled below.
            FlushAllPendingText();

            // P0 turn-ownership guard: this turn may only touch shared generation state if it
            // STILL OWNS the generation slot. A force-send (or any replacement turn) swaps
            // _generationCts for the new turn's source before this finally runs; a stale turn
            // must only flush its own buffered text — never dispose the new turn's CTS, clear
            // IsGenerating, null the shared session id, close the new turn's bubbles, or
            // advance the queue for a turn that is no longer current.
            bool stillOwner = ReferenceEquals(_generationCts, localGenerationCts);
            bool isCancelled = localGenerationCts.IsCancellationRequested;

            if (stillOwner && localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId)
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

            if (stillOwner)
            {
                IsGenerating = false;
                _generatingSessionId = null;
                _generationCts?.Dispose();
                _generationCts = null;
            }

            // Clear the per-session working indicator and the live-transcript snapshot — only
            // while this turn still owns the slot: a replacement turn in the same session is
            // still working and must keep the indicator.
            if (stillOwner && localGeneratingSessionId != null)
            {
                _sessionTranscriptCache.Remove(localGeneratingSessionId);
            }
            if (stillOwner)
            {
                UpdateSessionWorkingState(localGeneratingSessionId, false, null);
            }

            // If the turn completed while the user was viewing another chat, the engine's
            // in-memory cached history for this session may be a stale DB snapshot (taken
            // mid-generation by a switch-back load). Converge it with the store so the model
            // never answers this chat's next message without the finished turn in context.
            if (stillOwner && !isCancelled && _chatEngine != null && localGeneratingSessionId != null)
            {
                FireAndForget.Observe(_chatEngine.ResyncSessionHistoryFromStoreAsync(localGeneratingSessionId), operation: "ResyncSessionHistoryFromStore");
            }

            // Auto-rename chat if it is the first interaction
            var responseText = fullAssistantText.ToString();
            if (stillOwner && localGeneratingSessionId != null && SelectedSession?.Id == localGeneratingSessionId && SessionTitle == "New Chat" && Messages.Count >= 2 && !string.IsNullOrWhiteSpace(responseText))
            {
                FireAndForget.Run(async () =>
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
                }, operation: "GenerateTitleAsync");
            }

            // If generation was not explicitly cancelled AND the turn actually produced output
            // (visible text or an executed tool call), auto-process the next queued message.
            // Gating on produced output is what stops the observed death spiral: a model that
            // keeps producing EMPTY responses (qwen35 recurrent window-full) fails every turn,
            // and auto-feeding the next of 64 queued messages into it turns a bounded per-turn
            // correction budget into an unbounded full-context-rebuild loop until the app dies.
            // A failed turn surfaces its error to the user instead of silently churning the queue.
            if (stillOwner && !isCancelled && turnProducedOutput)
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
            // P0: claim the durable lease (Processing) but do NOT delete the item yet.
            // Previously the item was removed BEFORE the turn ran, so a crash, model-load
            // failure, or cancelled turn silently lost the queued message. The item is now
            // ACKed (Incorporated → durable delete) only after the turn completes below;
            // a crash mid-turn leaves it Processing in the durable store (lease-expiry
            // reclaim on restart is tracked separately).
            _messageQueue?.MarkStatus(nextItem.Id, QueuedMessageStatus.Processing);
            Action action = async () =>
            {
                try
                {
                    await Task.Delay(100);
                    var attachments = nextItem.Attachments?.Select(AttachmentItemViewModel.FromQueuedAttachment).ToList();
                    await SendMessageForTextAsync(nextItem.Content, attachments);
                    // Turn completed — the message was durably stored and processed (this
                    // includes user-cancelled turns: the message was already delivered to the
                    // transcript). ACK now.
                    _messageQueue?.MarkStatus(nextItem.Id, QueuedMessageStatus.Incorporated);
                }
                catch (Exception ex)
                {
                    // Delivery failed before the turn completed — release the lease back to
                    // Queued so the message remains retryable instead of being lost.
                    System.Diagnostics.Debug.WriteLine($"Queued message delivery failed, releasing lease: {ex.Message}");
                    _messageQueue?.MarkStatus(nextItem.Id, QueuedMessageStatus.Queued);
                }
                finally
                {
                    _isProcessingQueue = false;
                    // Drain the queue in sequence: while this queued turn was running the flag
                    // above blocked the turn's own end-of-turn advancement, so after releasing
                    // it we re-check and deliver the NEXT queued item (if any) instead of
                    // stalling after a single item per manual turn. Each queued turn runs to
                    // completion before the next is claimed, so this cannot overlap turns.
                    if (System.Windows.Application.Current?.Dispatcher != null)
                    {
                        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ProcessNextQueuedMessageIfAvailable());
                    }
                    else
                    {
                        ProcessNextQueuedMessageIfAvailable();
                    }
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

    partial void OnSelectedQueueSortOptionChanged(QueueSortOption value) => RefreshQueueUI();

    private void RefreshQueueUI()
    {
        Action update = () =>
        {
            var sessionId = SelectedSession?.Id ?? string.Empty;
            var pending = _messageQueue?.GetPending(sessionId) ?? Array.Empty<QueuedMessage>();
            var viewModels = pending.Select(i => new QueuedMessageViewModel(i)).ToList();

            // Manual mirrors the queue's actual processing order (Position, then FIFO tiebreak) —
            // the order the user edits by dragging. Other modes are presentation-only views.
            IEnumerable<QueuedMessageViewModel> ordered = SelectedQueueSortOption?.Mode switch
            {
                QueueSortMode.Manual => viewModels.OrderBy(v => v.Position).ThenBy(v => v.CreatedAt),
                QueueSortMode.NewestFirst => viewModels.OrderByDescending(v => v.CreatedAt),
                QueueSortMode.ModeThenAge => viewModels.OrderBy(v => v.Mode).ThenBy(v => v.CreatedAt),
                QueueSortMode.Alphabetical => viewModels.OrderBy(v => v.Content, StringComparer.OrdinalIgnoreCase),
                _ => viewModels.OrderBy(v => v.CreatedAt) // OldestFirst
            };

            QueuedMessages.Clear();
            foreach (var item in ordered)
            {
                QueuedMessages.Add(item);
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

    /// <summary>
    /// Applies a drag-and-drop reorder: moves the item to <paramref name="newIndex"/> in the
    /// session's processing order (persisted by the durable queue) and refreshes the panel.
    /// Called from the queue panel's drag-drop code-behind.
    /// </summary>
    public void MoveQueuedItem(QueuedMessageViewModel item, int newIndex)
    {
        if (item == null) return;
        var sessionId = SelectedSession?.Id ?? string.Empty;
        var msg = _messageQueue?.GetById(item.Id, sessionId);
        if (msg == null) return;

        bool moved = _messageQueue!.Reorder(item.Id, newIndex);
        if (moved && SelectedQueueSortOption?.Mode != QueueSortMode.Manual)
        {
            // The user is editing the real order — surface it in Manual mode so what they
            // see matches what will be processed.
            SelectedQueueSortOption = QueueSortOptions[0];
        }
        else if (moved)
        {
            RefreshQueueUI();
        }
    }

    [RelayCommand]
    private void EnqueueMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0) return;
        var text = InputText;
        InputText = string.Empty;
        var attachments = PendingAttachments.Select(a => a.ToQueuedAttachment()).ToList();
        PendingAttachments.Clear();
        var sessionId = SelectedSession?.Id ?? string.Empty;
        // Stamp the item with the session's current task so the engine only offers the
        // CURRENT task's queued messages to the model (task-scoped queue isolation).
        _messageQueue?.Enqueue(sessionId, text, attachments, SelectedQueueMode, _chatEngine?.CurrentTaskId);
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
    /// Gracefully interrupts any in-flight generation (mirrors ForceSendMessageAsync) so a
    /// queued message can be sent immediately instead of waiting for the queue's auto-process
    /// path, which is gated on idle state and never fires for Steer messages the model fails
    /// to incorporate via 'incorporate_queued_message' (the "messages stuck in the queue
    /// forever" bug).
    /// </summary>
    private async Task CancelActiveGenerationIfNeededAsync()
    {
        if (IsGenerating || IsModelLoading)
        {
            Cancel();
            if (_chatEngine != null)
            {
                await _chatEngine.CancelActiveGenerationAsync();
            }

            // Small delay to allow the active loop background task to finalize its partial
            // response history before the new turn starts.
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// Sends ONE queued message immediately as a real user turn, bypassing the queue: removes
    /// it from the queue, cancels any running generation, and sends the content now. The
    /// escape hatch for messages stuck waiting on the model to incorporate them.
    /// </summary>
    [RelayCommand]
    private async Task SendQueuedItemNowAsync(QueuedMessageViewModel? item)
    {
        if (item == null || (string.IsNullOrWhiteSpace(item.Content) && !item.HasAttachments)) return;
        if (!IsModelReady) return; // cannot send without a loaded model — leave it queued

        var content = item.Content;
        var attachments = item.Attachments.ToList();
        // Remove first so the item can never be double-sent or re-picked by the auto-process path.
        _messageQueue?.Remove(item.Id);

        await CancelActiveGenerationIfNeededAsync();
        await SendMessageForTextAsync(content, attachments);
    }

    /// <summary>
    /// Sends ALL pending queued messages for the current session immediately, one after
    /// another. All items are removed up front so the queue's auto-process-on-complete path
    /// cannot double-send any of them.
    /// </summary>
    [RelayCommand]
    private async Task SendAllQueuedNowAsync()
    {
        var sessionId = SelectedSession?.Id ?? string.Empty;
        if (string.IsNullOrEmpty(sessionId)) return;
        var pending = (_messageQueue?.GetPending(sessionId) ?? Array.Empty<QueuedMessage>()).ToList();
        if (pending.Count == 0) return;
        if (!IsModelReady) return;

        // Remove ALL items up front: after each send completes, the engine's finally block
        // auto-processes the next queued message — with an empty queue nothing is double-sent.
        foreach (var msg in pending)
        {
            _messageQueue?.Remove(msg.Id);
        }

        foreach (var msg in pending)
        {
            await CancelActiveGenerationIfNeededAsync();
            var attachments = msg.Attachments?.Select(AttachmentItemViewModel.FromQueuedAttachment).ToList();
            await SendMessageForTextAsync(msg.Content, attachments);
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
        if (item == null || (string.IsNullOrWhiteSpace(item.EditText) && !item.HasAttachments)) return;

        var sessionId = item.SessionId;
        var mode = item.Mode;
        var newText = item.EditText?.Trim() ?? string.Empty;
        var attachments = item.Attachments.Select(a => a.ToQueuedAttachment()).ToList();

        _messageQueue?.Remove(item.Id);
        _messageQueue?.Enqueue(sessionId, newText, attachments, mode, _chatEngine?.CurrentTaskId);
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

    private void DispatcherSafe(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(action);
        }
        else
        {
            action();
        }
    }

    private SessionInfo? FindSession(string? sessionId)
        => Sessions.FirstOrDefault(s => s.Id == sessionId);

    /// <summary>
    /// Sets the per-session working indicator state. Always routed through the dispatcher
    /// because stream events can arrive off the UI thread and SessionInfo is a bound
    /// ObservableObject.
    /// </summary>
    private void UpdateSessionWorkingState(string? sessionId, bool working, string? status)
    {
        var session = FindSession(sessionId);
        if (session == null) return;

        DispatcherSafe(() =>
        {
            if (working)
            {
                session.IsWorking = true;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    session.WorkingStatusText = status;
                }
            }
            else
            {
                session.IsWorking = false;
                session.WorkingStatusText = string.Empty;
            }
            UpdateWorkingElsewhereText();
        });
    }

    /// <summary>
    /// Recomputes the "the model is working in another chat" banner state. Called whenever
    /// generation starts/stops or the selected session changes.
    /// </summary>
    private void UpdateWorkingElsewhereText()
    {
        bool elsewhere = IsGenerating && _generatingSessionId != null && SelectedSession?.Id != _generatingSessionId;
        if (elsewhere != IsWorkingElsewhere)
        {
            IsWorkingElsewhere = elsewhere;
        }

        if (elsewhere)
        {
            var session = FindSession(_generatingSessionId);
            string model = !string.IsNullOrWhiteSpace(session?.WorkingModel) ? session!.WorkingModel : SelectedModelId;
            string title = !string.IsNullOrWhiteSpace(session?.Title) ? session!.Title : "another chat";
            WorkingElsewhereText = $"⏳ {model} is working in “{title}” — new messages here will be queued until it finishes.";
        }
        else if (!string.IsNullOrEmpty(WorkingElsewhereText))
        {
            WorkingElsewhereText = string.Empty;
        }
    }

    private void InsertSessionSorted(SessionInfo session)
    {
        int index = 0;
        while (index < Sessions.Count)
        {
            var current = Sessions[index];
            if (session.IsPinned && !current.IsPinned)
                break;
            if (session.IsPinned == current.IsPinned && session.Timestamp >= current.Timestamp)
                break;
            index++;
        }
        Sessions.Insert(index, session);
    }

    private void ResortSessions()
    {
        var sorted = Sessions.OrderByDescending(s => s.IsPinned).ThenByDescending(s => s.Timestamp).ToList();
        Sessions.Clear();
        foreach (var s in sorted) Sessions.Add(s);
    }

    [RelayCommand]
    private async Task CreateNewSessionAsync()
    {
        // Do not stack duplicate blank chats (observed: seven identical "New Chat" sidebar
        // entries after spamming the + button). If a default-titled chat with no messages
        // already exists — the one the user just made and abandoned, or the current one —
        // select it instead of creating another. A chat is only "blank" when it has the
        // default title AND zero persisted messages, so a chat the model is working in (a
        // message was already sent/persisted) is never mistaken for one.
        var existingBlank = Sessions.FirstOrDefault(s =>
            string.Equals(s.Title?.Trim(), "New Chat", StringComparison.OrdinalIgnoreCase));
        if (existingBlank != null)
        {
            try
            {
                int count = await _messageStore.GetMessageCountAsync(existingBlank.Id);
                if (count == 0)
                {
                    // Setting SelectedSession triggers OnSelectedSessionChanged -> load; when
                    // it is already the selected chat the setter is a no-op, which is fine.
                    SelectedSession = existingBlank;
                    return;
                }
            }
            catch
            {
                // Store unavailable — fall through and create a fresh session.
            }
        }

        string title = "New Chat";
        string sessionId = await _messageStore.CreateSessionAsync(title, SelectedModelId);
        
        var newSession = new SessionInfo { Id = sessionId, Title = title, Timestamp = DateTime.Now };
        if (System.Windows.Application.Current != null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                InsertSessionSorted(newSession);
                SelectedSession = newSession;
                _displayedSessionId = newSession.Id;
            });
        }
        else
        {
            InsertSessionSorted(newSession);
            SelectedSession = newSession;
            _displayedSessionId = newSession.Id;
        }
    }

    [RelayCommand]
    private async Task SelectSessionAsync(SessionInfo session)
    {
        if (session == null) return;
        long seqId = Interlocked.Increment(ref _sessionLoadSeq);
        await _sessionLoadGate.WaitAsync();
        try
        {
            await LoadSessionAsync(session, seqId);
        }
        finally
        {
            _sessionLoadGate.Release();
        }
    }

    /// <summary>
    /// Loads one session's transcript + engine history. Serialized via <see cref="_sessionLoadGate"/>
    /// so rapid chat clicks cannot interleave two loads, and guarded by <paramref name="seqId"/>
    /// so a superseded (stale) load never clobbers the engine's active session — only the most
    /// recent selection may call LoadHistory.
    /// </summary>
    private async Task LoadSessionAsync(SessionInfo session, long seqId)
    {
        SessionTitle = session.Title;

        // The user is switching away from the session whose generation is in flight: snapshot
        // its live transcript BEFORE it is cleared, so returning to it restores the model's
        // current state (partial assistant text is only persisted to the store when the turn
        // ends). Keyed off _displayedSessionId because SelectedSession has already moved on.
        if (_generatingSessionId != null &&
            _displayedSessionId == _generatingSessionId &&
            !_sessionTranscriptCache.ContainsKey(_generatingSessionId))
        {
            _sessionTranscriptCache[_generatingSessionId] = Messages.ToList();
        }

        Messages.Clear();
        
        List<ChatMessage> chatEngineMessages;
        var uiMessages = new List<ChatMessageViewModel>();
        try
        {
            var dbMessages = await _messageStore.GetMessagesAsync(session.Id, null);
            chatEngineMessages = new List<ChatMessage>();
            
            foreach (var msg in dbMessages)
            {
                var roleStr = msg.Role.ToString().ToLowerInvariant();
                if (roleStr == "assistant")
                {
                    var (thinking, content) = SplitThinkingContent(msg.Content);
                    if (!string.IsNullOrEmpty(thinking))
                    {
                        uiMessages.Add(new ChatMessageViewModel
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
                        uiMessages.Add(new ChatMessageViewModel
                        {
                            Role = "assistant",
                            Content = content,
                            Timestamp = msg.Timestamp
                        });
                    }
                }
                else
                {
                    uiMessages.Add(new ChatMessageViewModel
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load session {session.Id}: {ex.Message}");
            return;
        }
        
        // Stale load: the user has since selected a different session — let that newer load win.
        if (seqId != Interlocked.Read(ref _sessionLoadSeq)) return;

        // Returning to the chat the model is currently working on: restore the live in-flight
        // transcript (with the typing indicator still in place) so the model's current state
        // is shown the moment the user switches back, instead of a stale DB gap.
        if (session.Id == _generatingSessionId && IsGenerating &&
            _sessionTranscriptCache.TryGetValue(session.Id, out var liveTranscript) &&
            liveTranscript.Count > 0)
        {
            foreach (var m in liveTranscript)
            {
                Messages.Add(m);
            }
        }
        else
        {
            foreach (var m in uiMessages)
            {
                Messages.Add(m);
            }
        }
        _displayedSessionId = session.Id;

        _chatEngine?.LoadHistory(chatEngineMessages, session.Id);
        RefreshQueueUI();
        UpdateWorkingElsewhereText();
        FireAndForget.Observe(SidePanel.OnSessionChangedAsync(session.Id), operation: nameof(SidePanel.OnSessionChangedAsync));
    }

    /// <summary>
    /// Guarantees the engine's active session matches the currently selected chat before a
    /// message is sent. SelectSessionAsync is async (and can be slow on big sessions), so a
    /// message typed immediately after clicking a chat could otherwise land in the PREVIOUS
    /// session's history and store rows. Serialized on the same gate as session loads.
    /// </summary>
    private async Task EnsureEngineSessionAsync()
    {
        var targetId = SelectedSession?.Id;
        if (targetId == null) return;
        if (_chatEngine == null || _chatEngine.CurrentSessionId == targetId) return;

        await _sessionLoadGate.WaitAsync();
        try
        {
            // Re-check under the gate: a session load may have completed while we waited.
            if (_chatEngine == null || _chatEngine.CurrentSessionId == targetId || SelectedSession?.Id != targetId) return;
            await LoadSessionAsync(SelectedSession!, Interlocked.Increment(ref _sessionLoadSeq));
        }
        finally
        {
            _sessionLoadGate.Release();
        }
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
        _sessionTranscriptCache.Remove(session.Id);
        if (Sessions.Count > 0)
        {
            SelectedSession = Sessions[0];
            _displayedSessionId = SelectedSession?.Id;
        }
        else
        {
            await CreateNewSessionAsync();
        }
        UpdateWorkingElsewhereText();
    }

    [RelayCommand]
    private async Task TogglePinSessionAsync(SessionInfo session)
    {
        if (session == null) return;
        session.IsPinned = !session.IsPinned;
        await _messageStore.UpdateSessionAsync(session.Id, null, null, null, session.IsPinned);
        
        ResortSessions();
        
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

            // ===== Verbose header: app, session, task, and FULL model diagnostics =====
            sb.AppendLine("============================================================");
            sb.AppendLine("KLYDIS CHAT EXPORT");
            sb.AppendLine("============================================================");
            sb.AppendLine($"App version: {GetAppVersion()}");
            sb.AppendLine($"Session: {SelectedSession.Title}");
            sb.AppendLine($"SessionId: {SelectedSession.Id}");
            sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            var taskManager = _chatEngine?.TaskManager;
            var authoritativeTask = _chatEngine?.CurrentTaskId != null && taskManager != null
                ? await taskManager.GetTaskAsync(_chatEngine.CurrentTaskId)
                : (taskManager != null ? await taskManager.GetCurrentTaskAsync(SelectedSession.Id) : null);

            string taskIdDisplay = authoritativeTask?.TaskId ?? _chatEngine?.CurrentTaskId ?? "(no active task)";
            string taskObjectiveDisplay = authoritativeTask?.Objective ?? _chatEngine?.CurrentTaskObjective ?? "(none)";
            string taskStatusDisplay = authoritativeTask?.Status.ToString() ?? "(unknown)";

            sb.AppendLine("--- TASK ---");
            sb.AppendLine($"TaskId: {taskIdDisplay}");
            sb.AppendLine($"Status: {taskStatusDisplay}");
            sb.AppendLine($"Objective: {taskObjectiveDisplay}");
            if (authoritativeTask != null && !string.IsNullOrWhiteSpace(authoritativeTask.PlanJson))
            {
                sb.AppendLine($"Plan: {authoritativeTask.PlanJson}");
            }
            sb.AppendLine();
            sb.AppendLine("--- MODEL ---");
            var profile = _chatEngine?.CurrentModelProfile;
            if (profile == null)
            {
                sb.AppendLine("(no model profile available — model not loaded)");
            }
            else
            {
                var adapter = _chatEngine?.CurrentProtocolAdapter;
                sb.AppendLine($"Model: {profile.ModelId}");
                sb.AppendLine($"Path: {profile.ModelPath}");
                sb.AppendLine($"Architecture: {profile.Architecture}");
                sb.AppendLine($"ChatTemplate: {profile.Template}");
                sb.AppendLine($"Reasoning: {profile.Reasoning}");
                sb.AppendLine($"ToolProtocol: {profile.ToolProtocol}");
                sb.AppendLine($"PreferredProtocol: {profile.PreferredProtocol}");
                sb.AppendLine($"SupportedProtocols: {string.Join(", ", profile.SupportedProtocols)}");
                sb.AppendLine($"FallbackProtocols: {(profile.FallbackProtocols.Count > 0 ? string.Join(", ", profile.FallbackProtocols) : "(none)")}");
                sb.AppendLine($"NativeTools: {profile.SupportsNativeTools} | StructuredOutput: {profile.SupportsStructuredOutput} | Grammar: {profile.SupportsGrammar} | Thinking: {profile.SupportsThinking} | ToolContinuation: {profile.SupportsToolContinuation}");
                sb.AppendLine($"ToolCalling: {profile.ToolCalling} | Continuation: {profile.Continuation} | Repair: {profile.Repair}");
                sb.AppendLine($"ProtocolConfidence: {profile.ProtocolConfidence:0.00}");
                sb.AppendLine($"ProtocolKey: {Klydis.Core.Protocol.ProtocolRegistry.ResolveProtocolKey(profile) ?? "legacy-fallback"}");
                sb.AppendLine($"Adapter: {adapter?.GetType().Name ?? "legacy-fallback (no registered adapter)"}");
                sb.AppendLine($"Fingerprint: {profile.Fingerprint}");
                sb.AppendLine($"ProfileVersion: {profile.ProfileVersion}");
            }
            sb.AppendLine($"ContextSize: {_chatEngine?.ContextSize ?? 0} tokens");
            sb.AppendLine();
            sb.AppendLine("--- MESSAGES ---");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            foreach (var msg in dbMessages)
            {
                sb.Append($"[{msg.Timestamp:yyyy-MM-dd HH:mm:ss}] {msg.Role.ToString().ToUpperInvariant()}");
                if (msg.IsConsolidated) sb.Append(" (consolidated)");
                if (msg.TokenCount > 0) sb.Append($" | tokens: {msg.TokenCount}");
                sb.AppendLine(":");
                sb.AppendLine(msg.Content);
                if (!string.IsNullOrWhiteSpace(msg.ToolCallsJson))
                {
                    sb.AppendLine($"ToolCalls: {msg.ToolCallsJson}");
                }
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

    private static string GetAppVersion()
        => System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                System.Reflection.Assembly.GetExecutingAssembly())?.InformationalVersion
            ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

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

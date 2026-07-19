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
public partial class ChatViewModel : ObservableObject
{
    private readonly ChatEngine? _chatEngine;
    private CancellationTokenSource? _generationCts;
    private bool _userExplicitlyUnloaded;

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

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public ObservableCollection<RiskLevel> AvailableRiskLevels { get; } = new();

    private readonly Klydis.Core.Models.ModelRegistry _registry;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;

    private readonly Klydis.Core.Hardware.GpuProfiler _gpuProfiler;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Hardware.OffloadStrategy _offloadStrategy;
    private readonly Klydis.Core.Memory.MessageStore _messageStore;
    private readonly ToolExecutor _toolExecutor;

    public ChatViewModel(
        ChatEngine chatEngine,
        Klydis.Core.Models.ModelRegistry registry,
        Klydis.Core.Inference.InferenceEngine inferenceEngine,
        Klydis.Core.Hardware.GpuProfiler gpuProfiler,
        Klydis.Core.Hardware.SystemProfiler systemProfiler,
        Klydis.Core.Hardware.OffloadStrategy offloadStrategy,
        Klydis.Core.Memory.MessageStore messageStore,
        ToolExecutor toolExecutor)
    {
        _chatEngine = chatEngine;
        _registry = registry;
        _inferenceEngine = inferenceEngine;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        _messageStore = messageStore;
        _toolExecutor = toolExecutor;

        _toolExecutor.ToolApprovalRequested += ToolExecutor_ToolApprovalRequested;

        AvailableRiskLevels.Add(RiskLevel.Safe);
        AvailableRiskLevels.Add(RiskLevel.Standard);
        AvailableRiskLevels.Add(RiskLevel.AutoPilot);
        SelectedRiskLevel = _toolExecutor.CurrentRiskLevel;
        
        RefreshModels();
        _registry.RegistryChanged += OnRegistryChanged;
        _ = InitializeSessionsAsync();
    }

    private void OnRegistryChanged()
    {
        if (System.Windows.Application.Current != null)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshModels);
        }
    }

    private void ToolExecutor_ToolApprovalRequested(object? sender, ToolApprovalEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var msg = $"The model is attempting to execute '{e.Request.Name}'.\n\nAllow this operation?";
            var result = System.Windows.MessageBox.Show(msg, "Tool Approval Requested", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            e.IsApproved = result == System.Windows.MessageBoxResult.Yes;
        });
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
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
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
            // Sort to ensure pinned items are at top if any are manually modified, 
            // though the db already sorts them.
        });

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

                // Unload any existing model to free VRAM
                _inferenceEngine.UnloadModel();
                
                // Set architecture for prompt templating
                _inferenceEngine.Architecture = modelInfo.Architecture ?? "llama";

                // Load new model using hardware-aware plan
                var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
                var systemInfo = await _systemProfiler.GetSystemInfoAsync();
                var metadata = Klydis.Core.Models.GgufMetadataReader.Parse(modelInfo.FilePath);
                int totalLayers = metadata != null && metadata.BlockCount.HasValue ? (int)metadata.BlockCount.Value : 32;
                long layerSizeBytes = modelInfo.FileSizeBytes / totalLayers;
                // Cap context length to practical default to prevent VRAM overallocation.
                // The model's trained context (often 1M+) would require enormous KV cache.
                int rawContextLength = (int)(metadata?.ContextLength ?? 8192);
                int contextLength = Math.Min(rawContextLength, 32768);
                
                // KV cache per layer per token: 2 (K+V) * HeadCountKv * HeadDim * sizeof(element)
                // For Q8_0 KV cache, sizeof(element) = 1 byte.
                long kvCachePerLayerBytes = 4096; // Safe default: 2 * 8 * 128 * 2 = 4096
                if (metadata != null && metadata.EmbeddingLength.HasValue && metadata.HeadCount.HasValue && metadata.HeadCountKv.HasValue)
                {
                    long headDim = metadata.EmbeddingLength.Value / metadata.HeadCount.Value;
                    // K + V (2) * HeadCountKv * headDim * 2 bytes (F16 KV cache)
                    kvCachePerLayerBytes = 2 * metadata.HeadCountKv.Value * headDim * 2;
                }

                var plan = _offloadStrategy.CalculatePlan(
                    totalLayers, layerSizeBytes, kvCachePerLayerBytes, contextLength, gpuInfo, systemInfo, Klydis.Core.Hardware.OffloadStrategyType.FullGpu);
                
                await _inferenceEngine.LoadModelAsync(modelInfo.FilePath, plan);
                IsModelReady = true;

                // Remember the previously loaded model
                var updatedModel = modelInfo with { LastUsedAt = DateTime.UtcNow };
                await _registry.UpsertModelAsync(updatedModel);
            }
            catch (Exception ex)
            {
                string nativeLog = "";
                try
                {
                    var logLines = System.IO.File.ReadLines("llama_native.log").TakeLast(15).ToList();
                    nativeLog = "\n\nNative Log:\n" + string.Join("\n", logLines);
                }
                catch { }

                System.Diagnostics.Debug.WriteLine($"Failed to load model: {ex}");
                Messages.Add(new ChatMessageViewModel { Role = "error", Content = $"Failed to load model: {ex.Message}{nativeLog}", Timestamp = DateTime.Now });
            }
            finally
            {
                IsModelLoading = false;
            }
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
    private void UnloadModel()
    {
        _inferenceEngine.UnloadModel();
        IsModelReady = false;
        _userExplicitlyUnloaded = true;
        SelectedModelId = string.Empty;
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsGenerating || IsModelLoading)
            return;

        var userMessage = InputText;
        InputText = string.Empty;

        Messages.Add(new ChatMessageViewModel
        {
            Role = "user",
            Content = userMessage,
            Timestamp = DateTime.Now
        });

        IsGenerating = true;
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
        Messages.Add(typingIndicator);

        void OnUi(Action action) => System.Windows.Application.Current.Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Background);

        void AppendMessage(ChatMessageViewModel message)
        {
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
                int _throttleCounter = 0;
                await foreach (var evt in _chatEngine.StreamResponseAsync(userMessage, _generationCts.Token))
                {
                    if (++_throttleCounter % 10 == 0)
                    {
                        await Task.Delay(1); // Yield UI thread to process queued Dispatcher messages
                    }

                    switch (evt.Type)
                    {
                        case ChatStreamEventType.Token:
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
                                fullAssistantText.Append(evt.Content);
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
                                if (thoughtMessage != null)
                                {
                                    thoughtMessage.Content += evt.Content;
                                }
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
                                pendingToolCall = new ToolCallViewModel { Name = evt.Content, Status = "running", IsExpanded = true };
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
                                    pendingToolCall.Output = evt.Content;
                                    pendingToolCall.IsExpanded = !success;
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
            OnUi(() => AppendMessage(new ChatMessageViewModel
            {
                Role = "system",
                Content = "Generation canceled.",
                Timestamp = DateTime.Now
            }));
        }
        catch (Exception ex)
        {
            OnUi(() => AppendMessage(new ChatMessageViewModel
            {
                Role = "error",
                Content = ex.Message,
                Timestamp = DateTime.Now
            }));
        }
        finally
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
            IsGenerating = false;
            _generationCts?.Dispose();
            _generationCts = null;

            // Auto-rename chat if it is the first interaction
            var responseText = fullAssistantText.ToString();
            if (SessionTitle == "New Chat" && Messages.Count >= 2 && SelectedSession != null && !string.IsNullOrWhiteSpace(responseText))
            {
                _ = Task.Run(async () =>
                {
                    var newTitle = CleanTitle(await _chatEngine!.GenerateTitleAsync(userMessage, responseText));
                    if (!string.IsNullOrEmpty(newTitle) && newTitle != "New Chat")
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
        }
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
    }

    [RelayCommand]
    private async Task CreateNewSessionAsync()
    {
        string title = "New Chat";
        string sessionId = await _messageStore.CreateSessionAsync(title, SelectedModelId);
        
        var newSession = new SessionInfo { Id = sessionId, Title = title, Timestamp = DateTime.Now };
        Sessions.Insert(0, newSession);
        SelectedSession = newSession;
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
            chatEngineMessages.Add(new ChatMessage(msg.Role, msg.Content));
        }
        
        _chatEngine?.LoadHistory(chatEngineMessages, Guid.Parse(session.Id));
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionInfo session)
    {
        if (session == null) return;
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
                System.Windows.MessageBox.Show($"Failed to export chat:\n{ex.Message}", "Export Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "New Chat";
        return Regex.Replace(title, @"\s+", " ").Trim();
    }

    private static string StripToolCallBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var stripped = Regex.Replace(text, @"<\|?tool_call\|?>.*?(</\|?tool_call\|?>|<\|/tool_call\|>|$)", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    private static (string Thinking, string Content) SplitThinkingContent(string text)
    {
        int start = text.IndexOf("<think>", StringComparison.Ordinal);
        if (start >= 0)
        {
            int end = text.IndexOf("</think>", start + 7, StringComparison.Ordinal);
            if (end >= 0)
            {
                string thinking = text.Substring(start + 7, end - (start + 7)).Trim();
                string remaining = text.Remove(start, end + 8 - start).Trim();
                return (thinking, remaining);
            }
            else
            {
                string thinking = text.Substring(start + 7).Trim();
                string remaining = text.Substring(0, start).Trim();
                return (thinking, remaining);
            }
        }
        return (string.Empty, text);
    }
}

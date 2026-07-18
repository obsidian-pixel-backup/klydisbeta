using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private double _tokensPerSecond;

    [ObservableProperty]
    private string _selectedModelId = string.Empty;

    [ObservableProperty]
    private string _sessionTitle = "New Chat";

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    private readonly Klydis.Core.Models.ModelRegistry _registry;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;

    private readonly Klydis.Core.Hardware.GpuProfiler _gpuProfiler;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Hardware.OffloadStrategy _offloadStrategy;
    private readonly Klydis.Core.Memory.MessageStore _messageStore;

    public ChatViewModel(
        ChatEngine chatEngine,
        Klydis.Core.Models.ModelRegistry registry,
        Klydis.Core.Inference.InferenceEngine inferenceEngine,
        Klydis.Core.Hardware.GpuProfiler gpuProfiler,
        Klydis.Core.Hardware.SystemProfiler systemProfiler,
        Klydis.Core.Hardware.OffloadStrategy offloadStrategy,
        Klydis.Core.Memory.MessageStore messageStore)
    {
        _chatEngine = chatEngine;
        _registry = registry;
        _inferenceEngine = inferenceEngine;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        _messageStore = messageStore;
        
        RefreshModels();
        _ = InitializeSessionsAsync();
    }

    private async Task InitializeSessionsAsync()
    {
        var dbSessions = await _messageStore.GetSessionsAsync();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Sessions.Clear();
            foreach (var session in dbSessions)
            {
                Sessions.Add(new SessionInfo
                {
                    Id = session.Id,
                    Title = session.Title,
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
            await SelectSessionAsync(Sessions[0]);
        }
        else
        {
            await CreateNewSessionAsync();
        }
    }

    async partial void OnSelectedModelIdChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        
        var modelInfo = _registry.GetAllModels().FirstOrDefault(m => m.DisplayName == value);
        if (modelInfo != null)
        {
            try 
            {
                Messages.Add(new ChatMessageViewModel { Role = "system", Content = $"Loading model {modelInfo.DisplayName}...", Timestamp = DateTime.Now });
                IsGenerating = true; // block sending
                
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
                
                Messages.Add(new ChatMessageViewModel { Role = "system", Content = $"Model loaded successfully.", Timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load model: {ex}");
                Messages.Add(new ChatMessageViewModel { Role = "system", Content = $"Failed to load model: {ex.Message}\n{ex.StackTrace}", Timestamp = DateTime.Now });
            }
            finally
            {
                IsGenerating = false;
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
        AvailableModels.Clear();
        foreach (var model in _registry.GetAllModels())
        {
            AvailableModels.Add(model.DisplayName);
        }
        if (AvailableModels.Count > 0)
        {
            SelectedModelId = AvailableModels[0];
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsGenerating)
            return;

        var userMessage = InputText;
        InputText = string.Empty;

        Messages.Add(new ChatMessageViewModel
        {
            Role = "user",
            Content = userMessage,
            Timestamp = DateTime.Now
        });

        var assistantMessage = new ChatMessageViewModel
        {
            Role = "assistant",
            Content = string.Empty,
            IsStreaming = true,
            Timestamp = DateTime.Now
        };
        Messages.Add(assistantMessage);

        IsGenerating = true;
        _generationCts = new CancellationTokenSource();

        ChatMessageViewModel? thoughtMessage = null;

        try
        {
            if (_chatEngine != null)
            {
                await foreach (var evt in _chatEngine.StreamResponseAsync(userMessage, _generationCts.Token))
                {
                    switch (evt.Type)
                    {
                        case ChatStreamEventType.Token:
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                assistantMessage.Content += evt.Content;
                            });
                            break;
                        case ChatStreamEventType.ThinkingStart:
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                thoughtMessage = new ChatMessageViewModel
                                {
                                    Role = "thought",
                                    Content = string.Empty,
                                    IsThinkingExpanded = true,
                                    Timestamp = DateTime.Now
                                };
                                int assistantIdx = Messages.IndexOf(assistantMessage);
                                if (assistantIdx >= 0)
                                {
                                    Messages.Insert(assistantIdx, thoughtMessage);
                                }
                                else
                                {
                                    Messages.Add(thoughtMessage);
                                }
                            });
                            break;
                        case ChatStreamEventType.ThinkingToken:
                            if (thoughtMessage != null)
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    thoughtMessage.Content += evt.Content;
                                });
                            }
                            break;
                        case ChatStreamEventType.ThinkingEnd:
                            if (thoughtMessage != null)
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    thoughtMessage.IsThinkingExpanded = false;
                                });
                            }
                            break;
                        case ChatStreamEventType.ToolCall:
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                assistantMessage.HasToolCalls = true;
                                assistantMessage.ToolCalls.Add(new ToolCallViewModel { Name = evt.Content, Status = "running", Output = "Executing..." });
                            });
                            break;
                        case ChatStreamEventType.ToolResult:
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (assistantMessage.ToolCalls.Count > 0)
                                {
                                    var lastTool = assistantMessage.ToolCalls[^1];
                                    lastTool.Status = "done";
                                    lastTool.Output = evt.Content;
                                }
                            });
                            break;
                        case ChatStreamEventType.Error:
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                assistantMessage.Content += $"\n\n[Error: {evt.Content}]";
                            });
                            break;
                    }
                }
            }
            else
            {
                // Dummy streaming
                await Task.Delay(500);
                assistantMessage.Content = "This is a placeholder response since no engine was provided.";
            }
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Content += "\n\n[Generation Canceled]";
        }
        catch (Exception ex)
        {
            assistantMessage.Content += $"\n\n[Error: {ex.Message}]";
        }
        finally
        {
            assistantMessage.IsStreaming = false;
            IsGenerating = false;
            _generationCts?.Dispose();
            _generationCts = null;

            // Auto-rename chat if it is the first interaction
            if (SessionTitle == "New Chat" && Messages.Count >= 2 && SelectedSession != null)
            {
                _ = Task.Run(async () =>
                {
                    var newTitle = await _chatEngine!.GenerateTitleAsync(userMessage, assistantMessage.Content);
                    if (!string.IsNullOrEmpty(newTitle) && newTitle != "New Chat")
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
                        IsThinkingExpanded = false,
                        Timestamp = msg.Timestamp
                    });
                }
                
                Messages.Add(new ChatMessageViewModel
                {
                    Role = "assistant",
                    Content = content,
                    Timestamp = msg.Timestamp
                });
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
            await SelectSessionAsync(Sessions[0]);
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

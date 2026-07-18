using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LLama.Common;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference;

/// <summary>
/// Represents the role of a participant in a chat message.
/// </summary>
public enum Role
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>
/// Represents a single chat message in a conversation.
/// </summary>
public record ChatMessage(Role Role, string Content, DateTime Timestamp, int TokenCount);

/// <summary>
/// Wraps a conversation session on top of the inference engine, maintaining history and context windows.
/// </summary>
public sealed class InferenceSession
{
    private readonly InferenceEngine _engine;
    private readonly ChatTemplate _template;
    private readonly ILogger<InferenceSession> _logger;
    private readonly List<ChatMessage> _messages;
    private readonly int _maxContextTokens;

    /// <summary>
    /// Gets the conversation history.
    /// </summary>
    public IReadOnlyList<ChatMessage> History => _messages.AsReadOnly();

    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceSession"/> class.
    /// </summary>
    public InferenceSession(InferenceEngine engine, ChatTemplate template, ILogger<InferenceSession> logger, int maxContextTokens = 8192)
    {
        _engine = engine;
        _template = template;
        _logger = logger;
        _messages = new List<ChatMessage>();
        _maxContextTokens = maxContextTokens;
    }

    /// <summary>
    /// Private constructor for branching an existing session.
    /// </summary>
    private InferenceSession(InferenceEngine engine, ChatTemplate template, ILogger<InferenceSession> logger, IEnumerable<ChatMessage> history, int maxContextTokens)
    {
        _engine = engine;
        _template = template;
        _logger = logger;
        _messages = new List<ChatMessage>(history);
        _maxContextTokens = maxContextTokens;
    }

    /// <summary>
    /// Adds a system message to the conversation.
    /// </summary>
    public void AddSystemMessage(string content)
    {
        var tokenCount = _engine.IsModelLoaded ? _engine.GetTokenCount(content) : 0;
        _messages.Add(new ChatMessage(Role.System, content, DateTime.UtcNow, tokenCount));
    }

    /// <summary>
    /// Sends a user message, trims context if necessary, and yields the assistant's response tokens.
    /// </summary>
    public async IAsyncEnumerable<string> SendMessageAsync(
        string userMessage, 
        InferenceParams inferenceParams,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userTokenCount = _engine.IsModelLoaded ? _engine.GetTokenCount(userMessage) : 0;
        _messages.Add(new ChatMessage(Role.User, userMessage, DateTime.UtcNow, userTokenCount));

        TrimContext();

        var responseContent = new System.Text.StringBuilder();
        var responseTokens = 0;

        await foreach (var token in _engine.GenerateChatAsync(_messages, _template, inferenceParams, ct))
        {
            responseContent.Append(token);
            responseTokens++;
            yield return token;
        }

        _messages.Add(new ChatMessage(Role.Assistant, responseContent.ToString(), DateTime.UtcNow, responseTokens));
    }

    /// <summary>
    /// Trims old messages from the context window to prevent exceeding the maximum token limit.
    /// Preserves the system prompt if present.
    /// </summary>
    private void TrimContext()
    {
        int totalTokens = _messages.Sum(m => m.TokenCount);

        while (totalTokens > _maxContextTokens && _messages.Count > 1)
        {
            // Find the oldest non-system message
            var msgToRemove = _messages.FirstOrDefault(m => m.Role != Role.System);
            if (msgToRemove == null)
            {
                break; // Only system messages left, cannot trim further safely
            }

            _messages.Remove(msgToRemove);
            totalTokens -= msgToRemove.TokenCount;
            _logger.LogDebug("Trimmed old message (Role: {Role}) to fit context window. New total tokens: {TotalTokens}", msgToRemove.Role, totalTokens);
        }
    }

    /// <summary>
    /// Creates a duplicate of the current session state, allowing parallel conversation branches.
    /// </summary>
    public InferenceSession Branch()
    {
        _logger.LogInformation("Branching session with {MessageCount} messages.", _messages.Count);
        return new InferenceSession(_engine, _template, _logger, _messages, _maxContextTokens);
    }
}

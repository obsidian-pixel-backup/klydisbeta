using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Klydis.Core.Chat;

namespace Klydis.Core.Memory;

/// <summary>
/// Record representing a chat session.
/// </summary>
public record SessionRecord(
    string Id,
    string Title,
    string? ModelId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? WorldState,
    string? SystemPrompt,
    string? SettingsJson
);

/// <summary>
/// Record representing a single chat message.
/// </summary>
public record MessageRecord(
    int Id,
    string SessionId,
    ChatRole Role,
    string Content,
    DateTime Timestamp,
    int TokenCount,
    string? ToolCallsJson
);

/// <summary>
/// SQLite-based persistence for chat sessions and messages.
/// </summary>
public class MessageStore
{
    private readonly string _connectionString;
    private readonly ILogger<MessageStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageStore"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public MessageStore(ILogger<MessageStore> logger)
    {
        _logger = logger;
        
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dbDirectory = Path.Combine(appData, ".klydis", "data");
        Directory.CreateDirectory(dbDirectory);
        
        string dbPath = Path.Combine(dbDirectory, "klydis.db");
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
    }

    /// <summary>
    /// Creates the database and tables if they do not exist.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing MessageStore SQLite database.");
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        // Enable Write-Ahead Logging for concurrent reads
        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
        await pragmaCommand.ExecuteNonQueryAsync();

        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT 'New Chat',
                model_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                world_state TEXT,
                system_prompt TEXT,
                settings_json TEXT
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                token_count INTEGER DEFAULT 0,
                tool_calls_json TEXT,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            -- FTS5 Virtual Table for full-text search
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(content, content='messages', content_rowid='id');

            -- Triggers to keep FTS table synchronized
            CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, content) VALUES ('delete', old.id, old.content);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, content) VALUES ('delete', old.id, old.content);
                INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
            END;
        ";
        await createCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Creates a new chat session.
    /// </summary>
    public async Task<string> CreateSessionAsync(string title, string? modelId)
    {
        string sessionId = Guid.NewGuid().ToString();
        string now = DateTime.UtcNow.ToString("o");
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO sessions (id, title, model_id, created_at, updated_at) 
            VALUES (@id, @title, @modelId, @now, @now)";
            
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@modelId", modelId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        
        await command.ExecuteNonQueryAsync();
        return sessionId;
    }

    /// <summary>
    /// Retrieves all chat sessions ordered by the most recently updated.
    /// </summary>
    public async Task<List<SessionRecord>> GetSessionsAsync()
    {
        var sessions = new List<SessionRecord>();
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions ORDER BY updated_at DESC";
        
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sessions.Add(MapSessionRecord(reader));
        }
        
        return sessions;
    }

    /// <summary>
    /// Retrieves a specific chat session by ID.
    /// </summary>
    public async Task<SessionRecord?> GetSessionAsync(string sessionId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions WHERE id = @id";
        command.Parameters.AddWithValue("@id", sessionId);
        
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapSessionRecord(reader);
        }
        
        return null;
    }

    /// <summary>
    /// Updates an existing chat session.
    /// </summary>
    public async Task UpdateSessionAsync(string sessionId, string? title, string? worldState, string? systemPrompt)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        
        var sets = new List<string> { "updated_at = @now" };
        command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@id", sessionId);

        if (title != null)
        {
            sets.Add("title = @title");
            command.Parameters.AddWithValue("@title", title);
        }
        if (worldState != null)
        {
            sets.Add("world_state = @worldState");
            command.Parameters.AddWithValue("@worldState", worldState);
        }
        if (systemPrompt != null)
        {
            sets.Add("system_prompt = @systemPrompt");
            command.Parameters.AddWithValue("@systemPrompt", systemPrompt);
        }

        command.CommandText = $"UPDATE sessions SET {string.Join(", ", sets)} WHERE id = @id";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a chat session and cascades to delete all associated messages.
    /// </summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        // Ensure foreign key constraints are enforced for the cascading delete
        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
        await pragmaCommand.ExecuteNonQueryAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = @id";
        command.Parameters.AddWithValue("@id", sessionId);
        
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Adds a new message to a chat session.
    /// </summary>
    public async Task AddMessageAsync(string sessionId, ChatRole role, string content, int tokenCount, string? toolCallsJson)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO messages (session_id, role, content, timestamp, token_count, tool_calls_json)
            VALUES (@sessionId, @role, @content, @timestamp, @tokenCount, @toolCallsJson);
            
            UPDATE sessions SET updated_at = @timestamp WHERE id = @sessionId;
        ";
        
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@role", role.ToString());
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@tokenCount", tokenCount);
        command.Parameters.AddWithValue("@toolCallsJson", toolCallsJson ?? (object)DBNull.Value);
        
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Retrieves messages for a session, optionally limited to a specific count.
    /// </summary>
    public async Task<List<MessageRecord>> GetMessagesAsync(string sessionId, int? limit)
    {
        var messages = new List<MessageRecord>();
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM messages WHERE session_id = @sessionId ORDER BY id ASC" + (limit.HasValue ? " LIMIT @limit" : "");
        command.Parameters.AddWithValue("@sessionId", sessionId);
        
        if (limit.HasValue)
            command.Parameters.AddWithValue("@limit", limit.Value);
            
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            messages.Add(MapMessageRecord(reader));
        }
        
        return messages;
    }

    /// <summary>
    /// Retrieves the total message count for a session.
    /// </summary>
    public async Task<int> GetMessageCountAsync(string sessionId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE session_id = @sessionId";
        command.Parameters.AddWithValue("@sessionId", sessionId);
        
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Deletes a specific message by ID.
    /// </summary>
    public async Task DeleteMessageAsync(int messageId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE id = @id";
        command.Parameters.AddWithValue("@id", messageId);
        
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Searches messages using FTS5 full-text search index.
    /// </summary>
    public async Task<List<(MessageRecord Message, double Rank)>> SearchMessagesAsync(string sessionId, string query, int topK)
    {
        var results = new List<(MessageRecord Message, double Rank)>();
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT m.*, fts.rank
            FROM messages m
            JOIN messages_fts fts ON m.id = fts.rowid
            WHERE m.session_id = @sessionId AND messages_fts MATCH @query
            ORDER BY fts.rank
            LIMIT @topK";
            
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@query", query);
        
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var msg = MapMessageRecord(reader);
            var rank = reader.GetDouble(reader.GetOrdinal("rank"));
            results.Add((msg, rank));
        }
        
        return results;
    }

    private static SessionRecord MapSessionRecord(SqliteDataReader reader)
    {
        return new SessionRecord(
            Id: reader.GetString(reader.GetOrdinal("id")),
            Title: reader.GetString(reader.GetOrdinal("title")),
            ModelId: reader.IsDBNull(reader.GetOrdinal("model_id")) ? null : reader.GetString(reader.GetOrdinal("model_id")),
            CreatedAt: DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt: DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            WorldState: reader.IsDBNull(reader.GetOrdinal("world_state")) ? null : reader.GetString(reader.GetOrdinal("world_state")),
            SystemPrompt: reader.IsDBNull(reader.GetOrdinal("system_prompt")) ? null : reader.GetString(reader.GetOrdinal("system_prompt")),
            SettingsJson: reader.IsDBNull(reader.GetOrdinal("settings_json")) ? null : reader.GetString(reader.GetOrdinal("settings_json"))
        );
    }

    private static MessageRecord MapMessageRecord(SqliteDataReader reader)
    {
        return new MessageRecord(
            Id: reader.GetInt32(reader.GetOrdinal("id")),
            SessionId: reader.GetString(reader.GetOrdinal("session_id")),
            Role: Enum.Parse<ChatRole>(reader.GetString(reader.GetOrdinal("role")), ignoreCase: true),
            Content: reader.GetString(reader.GetOrdinal("content")),
            Timestamp: DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
            TokenCount: reader.GetInt32(reader.GetOrdinal("token_count")),
            ToolCallsJson: reader.IsDBNull(reader.GetOrdinal("tool_calls_json")) ? null : reader.GetString(reader.GetOrdinal("tool_calls_json"))
        );
    }
}

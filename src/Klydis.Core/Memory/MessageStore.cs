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
    string? SettingsJson,
    bool IsPinned,
    string? PlanJson
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
    string? ToolCallsJson,
    bool IsConsolidated = false
);

/// <summary>
/// Record representing a custom tool defined by the model.
/// </summary>
public record CustomToolRecord(
    string Name,
    string Description,
    string ParametersJson,
    string ScriptContent,
    string Language,
    DateTime CreatedAt
);

/// <summary>
/// A persistent lesson learned by the system or the model, shared across sessions.
/// </summary>
public record LessonRecord(
    string LessonKey,
    string ModelName,
    string Type,
    string Content,
    string? Source,
    string CreatedAt,
    int UseCount
);

/// <summary>
/// A user-authored note attached to a chat session. Notes are surfaced to the model as part
/// of the system prompt context on every generation, so the user can steer long-running work
/// ("keep this file read-only", "verify before claiming done") without re-sending messages.
/// </summary>
public record SessionNoteRecord(
    string Id,
    string SessionId,
    string Content,
    DateTime CreatedAt,
    DateTime UpdatedAt
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
    /// <param name="dbPathOverride">Optional explicit SQLite database path (used by tests to keep
    /// the database hermetic and avoid touching the real user-profile database).</param>
    public MessageStore(ILogger<MessageStore> logger, string? dbPathOverride = null)
    {
        _logger = logger;
        
        string dbPath = dbPathOverride ?? Path.Combine(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".klydis", "data"),
            "klydis.db");
        string dbDirectory = Path.GetDirectoryName(dbPath) ?? ".";
        Directory.CreateDirectory(dbDirectory);
        
        // Pooling=True lets ADO.NET reuse native SQLite connections across the per-call
        // SqliteConnection instances this store creates, instead of opening/closing a native
        // handle for every operation.
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=True";
    }

    /// <summary>
    /// Opens a pooled connection with the per-connection pragmas this store relies on.
    /// <c>journal_mode=WAL</c> is set once at initialize (it is persistent in the database
    /// header), but <c>synchronous</c> and <c>busy_timeout</c> are per-connection, so every
    /// open re-applies them. <c>synchronous=NORMAL</c> is the recommended durability level
    /// for WAL mode — it removes the per-transaction fsync that <c>FULL</c> performs, which
    /// matters here because AddMessageAsync commits inside the generation loop.
    /// </summary>
    private async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
        await pragma.ExecuteNonQueryAsync();
        return connection;
    }

    /// <summary>
    /// Creates the database and tables if they do not exist.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing MessageStore SQLite database.");
        
        await using var connection = await CreateConnectionAsync();
        
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
                settings_json TEXT,
                is_pinned INTEGER DEFAULT 0,
                plan_json TEXT
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                token_count INTEGER DEFAULT 0,
                tool_calls_json TEXT,
                is_consolidated INTEGER DEFAULT 0,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS custom_tools (
                name TEXT PRIMARY KEY,
                description TEXT NOT NULL,
                parameters_json TEXT NOT NULL,
                script_content TEXT NOT NULL,
                language TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS lessons (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                lesson_key TEXT NOT NULL UNIQUE,
                model_name TEXT NOT NULL,
                type TEXT NOT NULL,
                content TEXT NOT NULL,
                source TEXT,
                created_at TEXT NOT NULL,
                use_count INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS session_notes (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            -- Durable model message queue: steering/direct-send messages must survive
            -- process restarts so a terminated model turn never loses queued work. The
            -- stable id doubles as the idempotency key (a re-delivered message can be
            -- detected and skipped); attempt_count is the lease signal incremented on
            -- each claim, mirroring at-least-once delivery semantics.
            CREATE TABLE IF NOT EXISTS queued_messages (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                content TEXT NOT NULL,
                mode INTEGER NOT NULL,
                status INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0
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

        try
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE sessions ADD COLUMN is_pinned INTEGER DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists, ignore
        }

        try
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE messages ADD COLUMN is_consolidated INTEGER DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists, ignore
        }

        // The agent's task plan / todo list (persisted so long-horizon plans survive app
        // restarts and model switches — see ToolExecutor.SaveSessionPlanAsync).
        try
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE sessions ADD COLUMN plan_json TEXT;";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists, ignore
        }

        // GetMessagesAsync / GetMessageCountAsync filter by session_id and order by id: without
        // this index they full-scan the messages table (which includes every tool output) as
        // sessions grow to tens of thousands of rows.
        try
        {
            await using var indexCmd = connection.CreateCommand();
            indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_messages_session_id_id ON messages(session_id, id);";
            await indexCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Index already exists, ignore
        }
    }

    /// <summary>
    /// Creates a new chat session.
    /// </summary>
    public async Task<string> CreateSessionAsync(string title, string? modelId, string? customSessionId = null)
    {
        string sessionId = string.IsNullOrEmpty(customSessionId) ? Guid.NewGuid().ToString() : customSessionId;
        string now = DateTime.UtcNow.ToString("o");
        
        await using var connection = await CreateConnectionAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO sessions (id, title, model_id, created_at, updated_at) 
            VALUES (@id, @title, @modelId, @now, @now)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, model_id = excluded.model_id, updated_at = excluded.updated_at";
            
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
        
        await using var connection = await CreateConnectionAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions ORDER BY is_pinned DESC, updated_at DESC";
        
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
        await using var connection = await CreateConnectionAsync();
        
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
    public async Task UpdateSessionAsync(string sessionId, string? title, string? worldState, string? systemPrompt, bool? isPinned = null)
    {
        await using var connection = await CreateConnectionAsync();
        
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
        if (isPinned.HasValue)
        {
            sets.Add("is_pinned = @isPinned");
            command.Parameters.AddWithValue("@isPinned", isPinned.Value ? 1 : 0);
        }

        command.CommandText = $"UPDATE sessions SET {string.Join(", ", sets)} WHERE id = @id";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Persists the agent's task plan (todo list) and progress for a session, so long-horizon
    /// plans survive app restarts and model switches. <paramref name="planJson"/> is the JSON
    /// snapshot written by ToolExecutor on every 'plan' tool mutation; null clears it.
    /// </summary>
    public async Task SaveSessionPlanAsync(string sessionId, string? planJson)
    {
        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET plan_json = @plan, updated_at = @now WHERE id = @id";
        command.Parameters.AddWithValue("@plan", planJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@id", sessionId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a chat session and cascades to delete all associated messages.
    /// </summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        await using var connection = await CreateConnectionAsync();
        
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
        await using var connection = await CreateConnectionAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO sessions (id, title, created_at, updated_at)
            VALUES (@sessionId, 'New Chat', @timestamp, @timestamp);

            INSERT INTO messages (session_id, role, content, timestamp, token_count, tool_calls_json, is_consolidated)
            VALUES (@sessionId, @role, @content, @timestamp, @tokenCount, @toolCallsJson, 0);
            
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
        
        await using var connection = await CreateConnectionAsync();
        
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
    /// Persists a queued message (INSERT OR REPLACE so a re-enqueue of the same id is idempotent).
    /// </summary>
    public async Task SaveQueuedMessageAsync(QueuedMessage msg)
    {
        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO queued_messages (id, session_id, content, mode, status, created_at, attempt_count)
            VALUES (@id, @sessionId, @content, @mode, @status, @createdAt, @attemptCount);
        ";
        command.Parameters.AddWithValue("@id", msg.Id.ToString());
        command.Parameters.AddWithValue("@sessionId", msg.SessionId);
        command.Parameters.AddWithValue("@content", msg.Content);
        command.Parameters.AddWithValue("@mode", (int)msg.Mode);
        command.Parameters.AddWithValue("@status", (int)msg.Status);
        command.Parameters.AddWithValue("@createdAt", msg.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("@attemptCount", msg.AttemptCount);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Updates a queued message's status and attempt count (lease signal).
    /// </summary>
    public async Task UpdateQueuedMessageAsync(Guid id, QueuedMessageStatus status, int attemptCount)
    {
        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE queued_messages SET status = @status, attempt_count = @attemptCount WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString());
        command.Parameters.AddWithValue("@status", (int)status);
        command.Parameters.AddWithValue("@attemptCount", attemptCount);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Loads all persisted queued messages (hydration after a restart).
    /// </summary>
    public async Task<List<QueuedMessage>> LoadQueuedMessagesAsync()
    {
        var result = new List<QueuedMessage>();

        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, session_id, content, mode, status, created_at, attempt_count FROM queued_messages ORDER BY created_at ASC;";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new QueuedMessage
            {
                Id = Guid.Parse(reader.GetString(0)),
                SessionId = reader.GetString(1),
                Content = reader.GetString(2),
                Mode = (QueuedMessageMode)reader.GetInt32(3),
                Status = (QueuedMessageStatus)reader.GetInt32(4),
                CreatedAt = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                AttemptCount = reader.GetInt32(6)
            });
        }

        return result;
    }

    /// <summary>
    /// Deletes a queued message (terminal states are removed from the durable queue).
    /// </summary>
    public async Task DeleteQueuedMessageAsync(Guid id)
    {
        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM queued_messages WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString());

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Retrieves the total message count for a session.
    /// </summary>
    public async Task<int> GetMessageCountAsync(string sessionId)
    {
        await using var connection = await CreateConnectionAsync();
        
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
        await using var connection = await CreateConnectionAsync();
        
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
        
        await using var connection = await CreateConnectionAsync();
        
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
        command.Parameters.AddWithValue("@topK", topK);
        
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var msg = MapMessageRecord(reader);
            var rank = reader.GetDouble(reader.GetOrdinal("rank"));
            results.Add((msg, rank));
        }
        
        return results;
    }

    /// <summary>
    /// Retrieves all custom tools defined by the model.
    /// </summary>
    public async Task<List<CustomToolRecord>> GetCustomToolsAsync()
    {
        var tools = new List<CustomToolRecord>();
        
        await using var connection = await CreateConnectionAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM custom_tools ORDER BY name ASC";
        
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tools.Add(new CustomToolRecord(
                Name: reader.GetString(reader.GetOrdinal("name")),
                Description: reader.GetString(reader.GetOrdinal("description")),
                ParametersJson: reader.GetString(reader.GetOrdinal("parameters_json")),
                ScriptContent: reader.GetString(reader.GetOrdinal("script_content")),
                Language: reader.GetString(reader.GetOrdinal("language")),
                CreatedAt: DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")))
            ));
        }
        
        return tools;
    }

    /// <summary>
    /// Creates or updates a custom tool.
    /// </summary>
    public async Task CreateCustomToolAsync(CustomToolRecord tool)
    {
        await using var connection = await CreateConnectionAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO custom_tools (name, description, parameters_json, script_content, language, created_at) 
            VALUES (@name, @description, @parametersJson, @scriptContent, @language, @createdAt)
            ON CONFLICT(name) DO UPDATE SET
                description = excluded.description,
                parameters_json = excluded.parameters_json,
                script_content = excluded.script_content,
                language = excluded.language;";
            
        command.Parameters.AddWithValue("@name", tool.Name);
        command.Parameters.AddWithValue("@description", tool.Description);
        command.Parameters.AddWithValue("@parametersJson", tool.ParametersJson);
        command.Parameters.AddWithValue("@scriptContent", tool.ScriptContent);
        command.Parameters.AddWithValue("@language", tool.Language);
        command.Parameters.AddWithValue("@createdAt", tool.CreatedAt.ToString("o"));
        
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a custom tool by name.
    /// </summary>
    public async Task DeleteCustomToolAsync(string name)
    {
        await using var connection = await CreateConnectionAsync();
        
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM custom_tools WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);
        
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Persists a lesson learned (auto-correction event, tool failure, or explicit model learning).
    /// Deduplicates by (model, type, normalized content): a repeated identical lesson increments
    /// its use_count instead of creating new rows, so the recurrence signal survives.
    /// </summary>
    public async Task AddLessonAsync(string modelName, string type, string content, string? source = null)
    {
        var normalized = content.Trim().ToLowerInvariant();
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        string key = $"{modelName}|{type}|{Convert.ToHexString(hashBytes)[..16]}";

        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO lessons (lesson_key, model_name, type, content, source, created_at, use_count)
            VALUES (@key, @model, @type, @content, @source, @created, 1)
            ON CONFLICT(lesson_key) DO UPDATE SET use_count = use_count + 1";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@model", modelName);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@content", content.Trim());
        command.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
        command.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns the most recent lessons, newest first, optionally filtered by model and type.
    /// </summary>
    public async Task<List<LessonRecord>> GetRecentLessonsAsync(string? modelName = null, string? type = null, int limit = 20)
    {
        var results = new List<LessonRecord>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT lesson_key, model_name, type, content, source, created_at, use_count
            FROM lessons
            WHERE (@model IS NULL OR model_name = @model)
              AND (@type IS NULL OR type = @type)
            ORDER BY use_count DESC, created_at DESC
            LIMIT @limit";
        command.Parameters.AddWithValue("@model", (object?)modelName ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", (object?)type ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new LessonRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6)));
        }
        return results;
    }

    /// <summary>
    /// Counts lessons of a given type for a model — used for adaptive behavior decisions
    /// (e.g. switching a model off the native tool-call format after repeated failures).
    /// </summary>
    public async Task<int> GetLessonCountAsync(string modelName, string type)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(use_count), 0) FROM lessons WHERE model_name = @model AND type = @type";
        command.Parameters.AddWithValue("@model", modelName);
        command.Parameters.AddWithValue("@type", type);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    /// <summary>
    /// Returns all notes for a session, oldest first.
    /// </summary>
    public async Task<List<SessionNoteRecord>> GetNotesAsync(string sessionId)
    {
        var results = new List<SessionNoteRecord>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, session_id, content, created_at, updated_at
            FROM session_notes
            WHERE session_id = @sessionId
            ORDER BY created_at ASC";
        command.Parameters.AddWithValue("@sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SessionNoteRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3)).ToLocalTime(),
                DateTime.Parse(reader.GetString(4)).ToLocalTime()));
        }
        return results;
    }

    /// <summary>
    /// Upserts a note for a session. When <paramref name="noteId"/> is null or empty a new
    /// note is created (the generated id is returned); otherwise the existing note's content
    /// and updated_at are replaced.
    /// </summary>
    public async Task<string> SaveNoteAsync(string sessionId, string? noteId, string content)
    {
        string id = string.IsNullOrWhiteSpace(noteId) ? Guid.NewGuid().ToString("N") : noteId;
        string now = DateTime.UtcNow.ToString("o");

        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO session_notes (id, session_id, content, created_at, updated_at)
            VALUES (@id, @sessionId, @content, @created, @updated)
            ON CONFLICT(id) DO UPDATE SET
                content = excluded.content,
                updated_at = excluded.updated_at";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@created", now);
        command.Parameters.AddWithValue("@updated", now);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>
    /// Deletes a note from a session.
    /// </summary>
    public async Task DeleteNoteAsync(string sessionId, string noteId)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM session_notes WHERE id = @id AND session_id = @sessionId";
        command.Parameters.AddWithValue("@id", noteId);
        command.Parameters.AddWithValue("@sessionId", sessionId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Repairs known broken built-in custom tool scripts. Called at startup to patch
    /// chrome-navigator (C2) and weather-fetcher (C3) which had PowerShell syntax errors.
    /// Uses ON CONFLICT upsert so it is safe to run every launch.
    /// </summary>
    public async Task RepairBrokenCustomToolsAsync()
    {
        // C2: chrome-navigator — reads args from environment vars set by ToolExecutor
        const string chromeSchema = "[{\"name\":\"url\",\"type\":\"string\",\"description\":\"The URL to open in Chrome\",\"required\":false},{\"name\":\"mode\",\"type\":\"string\",\"description\":\"Mode: new-window, new-tab, or incognito\",\"required\":false}]";
        var chromeLines = new[]
        {
            "param()",
            "$url  = if ($env:url)  { $env:url  } else { 'https://www.google.com' }",
            "$mode = if ($env:mode) { $env:mode } else { 'new-tab' }",
            "",
            "$chromePaths = @(",
            "    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',",
            "    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',",
            "    \"$env:LOCALAPPDATA\\Google\\Chrome\\Application\\chrome.exe\"",
            ")",
            "",
            "$chromePath = $chromePaths | Where-Object { Test-Path $_ } | Select-Object -First 1",
            "if (-not $chromePath) { Write-Output 'Chrome not found'; exit 1 }",
            "",
            "$flag = switch ($mode) {",
            "    'new-window'  { '--new-window' }",
            "    'incognito'   { '--incognito' }",
            "    default       { '--new-tab' }",
            "}",
            "",
            "Start-Process -FilePath $chromePath -ArgumentList @($flag, $url)",
            "Write-Output \"Chrome launched: $url ($mode)\""
        };
        string chromeScript = string.Join("\n", chromeLines);

        await CreateCustomToolAsync(new CustomToolRecord(
            "chrome-navigator",
            "Opens a URL in Google Chrome in a new tab, new window, or incognito mode.",
            chromeSchema, chromeScript, "powershell", DateTime.UtcNow));

        // C3: weather-fetcher — uses Open-Meteo free API (no key needed)
        const string weatherSchema = "[{\"name\":\"location\",\"type\":\"string\",\"description\":\"City name or coordinates for weather lookup\",\"required\":true},{\"name\":\"unit\",\"type\":\"string\",\"description\":\"Temperature unit: celsius or fahrenheit\",\"required\":false}]";
        var weatherLines = new[]
        {
            "param()",
            "$location = if ($env:location) { $env:location } else { 'London' }",
            "$unit     = if ($env:unit)     { $env:unit.ToLower() } else { 'celsius' }",
            "",
            "$unitParam       = if ($unit -eq 'fahrenheit') { '&temperature_unit=fahrenheit' } else { '' }",
            "$encodedLocation = [System.Uri]::EscapeDataString($location)",
            "",
            "try {",
            "    $geoUrl      = \"https://geocoding-api.open-meteo.com/v1/search?name=$encodedLocation&count=1&format=json\"",
            "    $geoResponse = Invoke-RestMethod -Uri $geoUrl -UseBasicParsing -TimeoutSec 15",
            "    if (-not $geoResponse.results -or $geoResponse.results.Count -eq 0) {",
            "        Write-Output \"Location '$location' not found.\"; exit 1",
            "    }",
            "    $lat  = $geoResponse.results[0].latitude",
            "    $lon  = $geoResponse.results[0].longitude",
            "    $name = $geoResponse.results[0].name",
            "",
            "    $weatherUrl      = \"https://api.open-meteo.com/v1/forecast?latitude=$lat&longitude=$lon&current_weather=true&hourly=relative_humidity_2m,apparent_temperature,wind_speed_10m$unitParam\"",
            "    $weatherResponse = Invoke-RestMethod -Uri $weatherUrl -UseBasicParsing -TimeoutSec 15",
            "    $current         = $weatherResponse.current_weather",
            "",
            "    $tempUnit = if ($unit -eq 'fahrenheit') { 'degF' } else { 'degC' }",
            "    $windKmh  = [math]::Round($current.windspeed, 1)",
            "",
            "    Write-Output \"Weather for $name (Lat: $lat, Lon: $lon)\"",
            "    Write-Output \"Temperature : $($current.temperature) $tempUnit\"",
            "    Write-Output \"Wind Speed  : $windKmh km/h\"",
            "    Write-Output \"Wind Dir    : $($current.winddirection) degrees\"",
            "    Write-Output \"Weather Code: $($current.weathercode)\"",
            "    Write-Output \"Is Day      : $(if ($current.is_day -eq 1) { 'Yes' } else { 'No' })\"",
            "} catch {",
            "    Write-Output \"Failed to fetch weather: $($_.Exception.Message)\"",
            "    exit 1",
            "}"
        };
        string weatherScript = string.Join("\n", weatherLines);

        await CreateCustomToolAsync(new CustomToolRecord(
            "weather-fetcher",
            "Fetches current weather for a location using the Open-Meteo API. Returns temperature, wind speed, and conditions.",
            weatherSchema, weatherScript, "powershell", DateTime.UtcNow));
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
            SettingsJson: reader.IsDBNull(reader.GetOrdinal("settings_json")) ? null : reader.GetString(reader.GetOrdinal("settings_json")),
            IsPinned: !reader.IsDBNull(reader.GetOrdinal("is_pinned")) && reader.GetInt32(reader.GetOrdinal("is_pinned")) == 1,
            PlanJson: reader.IsDBNull(reader.GetOrdinal("plan_json")) ? null : reader.GetString(reader.GetOrdinal("plan_json"))
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
            ToolCallsJson: reader.IsDBNull(reader.GetOrdinal("tool_calls_json")) ? null : reader.GetString(reader.GetOrdinal("tool_calls_json")),
            IsConsolidated: !reader.IsDBNull(reader.GetOrdinal("is_consolidated")) && reader.GetInt32(reader.GetOrdinal("is_consolidated")) == 1
        );
    }

    /// <summary>
    /// Marks a list of messages as consolidated in the database.
    /// </summary>
    public async Task MarkMessagesAsConsolidatedAsync(IEnumerable<int> messageIds)
    {
        if (messageIds == null || !messageIds.Any()) return;

        await using var connection = await CreateConnectionAsync();
        
        // One batched UPDATE instead of one round-trip per row (the old N+1). Ids come from the
        // database itself (AUTOINCREMENT integers), so inlining them is injection-safe.
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE messages SET is_consolidated = 1 WHERE id IN ({string.Join(",", messageIds)})";
        await command.ExecuteNonQueryAsync();
    }
}

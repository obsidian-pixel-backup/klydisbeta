using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Klydis.Core.Chat;
using Klydis.Core.Tasks;
using Klydis.Core.Workbench;
using TaskStatus = Klydis.Core.Chat.TaskStatus;

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
/// A durable record of one tool invocation. The workbench Files/Preview/Terminal panels read
/// this from SQLite so activity survives app restarts and model switches; the in-memory
/// ToolExecutor activity list is only a cache of this table.
/// </summary>
public sealed record ToolActivityRow(
    string ActivityId,
    string SessionId,
    string? TaskId,
    string? RunId,
    string ToolName,
    string ArgsJson,
    bool Success,
    string OutputPreview,
    DateTime TimestampUtc
);

/// <summary>
/// A durable artifact: a file the agent produced that is useful to the user (previewable
/// pages, docs, reports). Registered automatically by the harness after a successful file
/// mutation — never derived from model narration. IsCurrent flags the latest revision of a
/// path so the Preview tab can show the newest content without scanning the workspace.
/// </summary>
public sealed record ArtifactRow(
    string ArtifactId,
    string SessionId,
    string? TaskId,
    string Path,
    string ArtifactType,
    string? ContentHash,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool Previewable,
    bool IsCurrent
);

/// <summary>
/// A durable execution event: a factual state change in the agent loop (task/step/tool/file/
/// artifact lifecycle). The event stream is the backbone the workbench panels project from;
/// nothing in the UI invents state that is not recorded here.
/// </summary>
public sealed record ExecutionEventRow(
    string EventId,
    string SessionId,
    string? TaskId,
    string? RunId,
    string EventType,
    DateTime TimestampUtc,
    string? ToolName = null,
    string? Path = null,
    string? PayloadJson = null
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
                attempt_count INTEGER NOT NULL DEFAULT 0,
                position INTEGER NOT NULL DEFAULT 0,
                task_id TEXT
            );

            -- Durable tasks: the unit of agentic work inside a session. A conversation
            -- contains many tasks; execution state (plan, queue, artifacts, completion)
            -- attaches to the task, so a new task in the same chat never inherits an old
            -- task's checklist, and a superseded task remains resumable. task_id is the
            -- stable identity every plan/queue read keys off.
            CREATE TABLE IF NOT EXISTS tasks (
                task_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                objective TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                plan_json TEXT,
                summary TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_tasks_session_id ON tasks(session_id, created_at);

            -- Execution runs: one continuous attempt at a task. The durable anchor of the
            -- Task → Run → Step → Turn → Generation hierarchy, so a restart can reconstruct
            -- which run was executing and how far it got (the checkpoint/recovery phase reads
            -- this before considering event sourcing).
            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                turn_count INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_runs_task_id ON runs(task_id, started_at);

            -- Factual file-change log (workbench): captured around file-mutating tools so the
            -- Changes tab shows REAL filesystem diffs (before/after hashes + unified diff),
            -- never model-generated narration. Task-scoped via task_id.
            CREATE TABLE IF NOT EXISTS file_changes (
                change_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                task_id TEXT,
                path TEXT NOT NULL,
                tool TEXT NOT NULL,
                before_hash TEXT,
                after_hash TEXT,
                diff TEXT,
                added_lines INTEGER NOT NULL DEFAULT 0,
                deleted_lines INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_file_changes_session ON file_changes(session_id, created_at);

            -- Durable tool-activity log (workbench): every tool invocation the model makes,
            -- persisted so Files/Preview/Terminal survive restarts. The in-memory session
            -- activity list in ToolExecutor is a cache of this table, not the source of truth.
            CREATE TABLE IF NOT EXISTS tool_activity (
                activity_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                task_id TEXT,
                run_id TEXT,
                tool_name TEXT NOT NULL,
                args_json TEXT,
                success INTEGER NOT NULL,
                output_preview TEXT,
                timestamp_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tool_activity_session ON tool_activity(session_id, timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_tool_activity_task ON tool_activity(task_id, timestamp_utc);

            -- Durable artifact registry (workbench): files the agent produced that are useful
            -- to the user. Auto-registered by the mutation pipeline after successful writes —
            -- the Preview tab reads this instead of inferring artifacts from memory.
            CREATE TABLE IF NOT EXISTS artifacts (
                artifact_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                task_id TEXT,
                path TEXT NOT NULL,
                artifact_type TEXT NOT NULL,
                content_hash TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                previewable INTEGER NOT NULL DEFAULT 0,
                is_current INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_artifacts_session ON artifacts(session_id, path);
            CREATE INDEX IF NOT EXISTS idx_artifacts_task ON artifacts(task_id, path);

            -- Durable execution-event stream: the factual backbone of the agent loop
            -- (task/step/tool/file/artifact lifecycle). Workbench panels project from this —
            -- nothing invents state that is not recorded here.
            CREATE TABLE IF NOT EXISTS execution_events (
                event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                task_id TEXT,
                run_id TEXT,
                event_type TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                tool_name TEXT,
                path TEXT,
                payload_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_execution_events_session ON execution_events(session_id, timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_execution_events_task ON execution_events(task_id, timestamp_utc);

            -- Durable action ledger (review §9): every executed tool action with its replay
            -- identity, side-effect level and lifecycle status. The recovery-critical state
            -- is UNKNOWN — a process death mid-command leaves the action Unknown, never
            -- silently Succeeded/Failed, so recovery inspects instead of re-running blindly.
            CREATE TABLE IF NOT EXISTS task_actions (
                action_id TEXT PRIMARY KEY,
                replay_key TEXT,
                task_id TEXT,
                run_id TEXT,
                step_id TEXT,
                turn_id TEXT,
                tool_name TEXT NOT NULL,
                arguments_json TEXT,
                side_effect_level TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT,
                result_preview TEXT,
                error TEXT,
                model_id TEXT,
                protocol_key TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_task_actions_model ON task_actions(model_id, protocol_key, started_at);
            CREATE INDEX IF NOT EXISTS idx_task_actions_run ON task_actions(run_id, started_at);
            CREATE INDEX IF NOT EXISTS idx_task_actions_task ON task_actions(task_id, started_at);
            CREATE INDEX IF NOT EXISTS idx_task_actions_replay ON task_actions(replay_key);

            -- Durable execution evidence (review §2): verification facts (BuildPassed /
            -- PreviewLoaded / TestPassed) persist with the workspace version they were
            -- produced against, so a recovered run still knows the build was verified and a
            -- later file change invalidates them durably (invalidated_at), never by losing
            -- the row.
            CREATE TABLE IF NOT EXISTS execution_evidence (
                evidence_id TEXT PRIMARY KEY,
                task_id TEXT,
                run_id TEXT,
                step_id TEXT,
                action_id TEXT,
                workspace_version INTEGER NOT NULL,
                kind TEXT NOT NULL,
                subject TEXT,
                tool_name TEXT,
                exit_code INTEGER,
                payload_json TEXT,
                timestamp_utc TEXT NOT NULL,
                invalidated_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_execution_evidence_run ON execution_evidence(run_id, workspace_version);
            CREATE INDEX IF NOT EXISTS idx_execution_evidence_task ON execution_evidence(task_id, timestamp_utc);

            -- Durable supervisor-decision ledger (review §15): the audit trail of what the
            -- supervisor decided, why, and for which step — survives restarts so a
            -- recovered loop is diagnosable.
            CREATE TABLE IF NOT EXISTS execution_decisions (
                decision_id TEXT PRIMARY KEY,
                task_id TEXT,
                run_id TEXT,
                step_id TEXT,
                decision TEXT NOT NULL,
                reason TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_execution_decisions_run ON execution_decisions(run_id, timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_execution_decisions_task ON execution_decisions(task_id, timestamp_utc);

            -- Durable typed TaskStep metadata (review §3): the derived execution semantics of
            -- the plan checklist (kind, allowed tools, verification criteria, status,
            -- attempt_count) persisted per task so step semantics survive restarts instead of
            -- being re-derived from English text. The plan checklist remains the durable
            -- source of truth; this is its typed mirror, rewritten whenever the plan changes.
            CREATE TABLE IF NOT EXISTS task_steps (
                step_id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                order_index INTEGER NOT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                expected_action_kind TEXT NOT NULL,
                allowed_tools_json TEXT,
                required_skills_json TEXT,
                expected_artifacts_json TEXT,
                verification_criteria_json TEXT,
                completion_condition TEXT,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_action_id TEXT,
                started_at TEXT,
                completed_at TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_task_steps_task ON task_steps(task_id, order_index);

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

        // Manual drag-and-drop reorder of queued messages: position is the explicit processing
        // order (0 = first). Defaults to 0 for pre-existing rows, so legacy queues keep FIFO.
        try
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE queued_messages ADD COLUMN position INTEGER NOT NULL DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists, ignore
        }

        // Task identity on queued messages: items are stamped with the task they belong to so
        // the model only ever sees the CURRENT task's queue. Legacy rows stay NULL and fall
        // back to session-scoped behavior.
        try
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE queued_messages ADD COLUMN task_id TEXT;";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists, ignore
        }

        // Per-model execution telemetry (agent-intelligence stage): actions are stamped with
        // the model + protocol that produced them so the capability analyzer can aggregate
        // per-(model, protocol) success/repair/verification rates from the durable ledger.
        try
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE task_actions ADD COLUMN model_id TEXT;";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists, ignore
        }

        try
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE task_actions ADD COLUMN protocol_key TEXT;";
            await alterCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists, ignore
        }

        try
        {
            await using var indexCmd = connection.CreateCommand();
            indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_task_actions_model ON task_actions(model_id, protocol_key, started_at);";
            await indexCmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Index already exists, ignore
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
    /// The most recent task for a session (the current task), or null when the session has
    /// no tasks yet. "Most recent" by creation time; superseded tasks remain queryable via
    /// <see cref="GetTaskAsync"/> so a task can be reopened later.
    /// </summary>
    public async Task<AgentTask?> GetLatestTaskAsync(string sessionId)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT task_id, session_id, objective, status, created_at, updated_at, plan_json, summary
            FROM tasks
            WHERE session_id = @sessionId
            ORDER BY created_at DESC, task_id DESC
            LIMIT 1;";
        command.Parameters.AddWithValue("@sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadTask(reader);
        }
        return null;
    }

    /// <summary>
    /// Loads a task by id (used to restore a superseded task's plan when it is reopened).
    /// </summary>
    public async Task<AgentTask?> GetTaskAsync(string taskId)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT task_id, session_id, objective, status, created_at, updated_at, plan_json, summary
            FROM tasks
            WHERE task_id = @taskId;";
        command.Parameters.AddWithValue("@taskId", taskId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadTask(reader);
        }
        return null;
    }

    /// <summary>
    /// Upserts a task row. The task record is the durable home of the objective, status, and
    /// plan for one unit of agentic work inside a session.
    /// </summary>
    public async Task SaveTaskAsync(AgentTask task)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO tasks (task_id, session_id, objective, status, created_at, updated_at, plan_json, summary)
            VALUES (@taskId, @sessionId, @objective, @status, @createdAt, @updatedAt, @planJson, @summary);
        ";
        command.Parameters.AddWithValue("@taskId", task.TaskId);
        command.Parameters.AddWithValue("@sessionId", task.SessionId);
        command.Parameters.AddWithValue("@objective", task.Objective);
        command.Parameters.AddWithValue("@status", task.Status.ToString());
        command.Parameters.AddWithValue("@createdAt", task.CreatedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("@updatedAt", task.UpdatedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("@planJson", (object?)task.PlanJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@summary", (object?)task.Summary ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Updates a task's plan JSON in place (the plan follows the task). Null clears it.
    /// </summary>
    public async Task SaveTaskPlanAsync(string taskId, string? planJson)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET plan_json = @plan, updated_at = @now WHERE task_id = @taskId;";
        command.Parameters.AddWithValue("@plan", planJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@taskId", taskId);
        await command.ExecuteNonQueryAsync();
    }

    private static AgentTask ReadTask(System.Data.Common.DbDataReader reader)
    {
        return new AgentTask(
            TaskId: reader.GetString(0),
            SessionId: reader.GetString(1),
            Objective: reader.GetString(2),
            Status: Enum.TryParse<TaskStatus>(reader.GetString(3), out var status) ? status : TaskStatus.Running,
            CreatedAtUtc: DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            UpdatedAtUtc: DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            PlanJson: reader.IsDBNull(6) ? null : reader.GetString(6),
            Summary: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <summary>
    /// Upserts a run record (start and end of one execution attempt at a task).
    /// </summary>
    public async Task SaveRunAsync(TaskRun run)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO runs (run_id, task_id, status, started_at, ended_at, turn_count)
            VALUES (@runId, @taskId, @status, @startedAt, @endedAt, @turnCount);
        ";
        command.Parameters.AddWithValue("@runId", run.RunId);
        command.Parameters.AddWithValue("@taskId", run.TaskId);
        command.Parameters.AddWithValue("@status", run.Status.ToString());
        command.Parameters.AddWithValue("@startedAt", run.StartedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("@endedAt", (object?)(run.EndedAtUtc?.ToString("o")) ?? DBNull.Value);
        command.Parameters.AddWithValue("@turnCount", run.TurnCount);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// All runs for a task, oldest first — the durable execution history used to answer
    /// "which run was executing and how did it end?" after a restart.
    /// </summary>
    public async Task<List<TaskRun>> GetRunsAsync(string taskId)
    {
        var result = new List<TaskRun>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id, task_id, status, started_at, ended_at, turn_count FROM runs WHERE task_id = @taskId ORDER BY started_at ASC;";
        command.Parameters.AddWithValue("@taskId", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new TaskRun(
                RunId: reader.GetString(0),
                TaskId: reader.GetString(1),
                Status: Enum.TryParse<RunStatus>(reader.GetString(2), out var status) ? status : RunStatus.Running,
                StartedAtUtc: DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                EndedAtUtc: reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                TurnCount: reader.GetInt32(5)));
        }
        return result;
    }

    /// <summary>Every run across ALL tasks, oldest first — cross-task capability telemetry
    /// attributes run completion to the model that drove the run.</summary>
    public async Task<List<TaskRun>> GetAllRunsAsync()
    {
        var result = new List<TaskRun>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id, task_id, status, started_at, ended_at, turn_count FROM runs ORDER BY started_at ASC;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new TaskRun(
                RunId: reader.GetString(0),
                TaskId: reader.GetString(1),
                Status: Enum.TryParse<RunStatus>(reader.GetString(2), out var status) ? status : RunStatus.Running,
                StartedAtUtc: DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                EndedAtUtc: reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                TurnCount: reader.GetInt32(5)));
        }
        return result;
    }

    // ===== Durable action / evidence / decision ledger (review §2, §9, §15) ================

    /// <summary>Persists one executed action (INSERT OR REPLACE by action id — a lifecycle
    /// update rewrites the same row).</summary>
    public async Task SaveTaskActionAsync(TaskActionRecord action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO task_actions
                (action_id, replay_key, task_id, run_id, step_id, turn_id, tool_name,
                 arguments_json, side_effect_level, status, started_at, completed_at,
                 result_preview, error, model_id, protocol_key)
            VALUES
                (@actionId, @replayKey, @taskId, @runId, @stepId, @turnId, @toolName,
                 @argumentsJson, @sideEffectLevel, @status, @startedAt, @completedAt,
                 @resultPreview, @error, @modelId, @protocolKey);";
        command.Parameters.AddWithValue("@actionId", action.ActionId);
        command.Parameters.AddWithValue("@replayKey", (object?)action.ReplayKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@taskId", (object?)action.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@runId", (object?)action.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("@stepId", (object?)action.StepId ?? DBNull.Value);
        command.Parameters.AddWithValue("@turnId", (object?)action.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("@toolName", action.ToolName ?? "?");
        command.Parameters.AddWithValue("@argumentsJson", (object?)action.ArgumentsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@sideEffectLevel", action.SideEffectLevel.ToString());
        command.Parameters.AddWithValue("@status", action.Status.ToString());
        command.Parameters.AddWithValue("@startedAt", action.StartedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("@completedAt", (object?)(action.CompletedAtUtc?.ToString("o")) ?? DBNull.Value);
        command.Parameters.AddWithValue("@resultPreview", (object?)action.ResultPreview ?? DBNull.Value);
        command.Parameters.AddWithValue("@error", (object?)action.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("@modelId", (object?)action.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("@protocolKey", (object?)action.ProtocolKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>All recorded actions for a run (or a task when runId is null), oldest first.</summary>
    public async Task<List<TaskActionRecord>> GetTaskActionsAsync(string? taskId, string? runId)
    {
        var result = new List<TaskActionRecord>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        if (!string.IsNullOrEmpty(runId))
        {
            command.CommandText = "SELECT * FROM task_actions WHERE run_id = @runId ORDER BY started_at ASC;";
            command.Parameters.AddWithValue("@runId", runId);
        }
        else
        {
            command.CommandText = "SELECT * FROM task_actions WHERE task_id = @taskId ORDER BY started_at ASC;";
            command.Parameters.AddWithValue("@taskId", (object?)taskId ?? DBNull.Value);
        }
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new TaskActionRecord(
                ActionId: reader.GetString(0),
                ReplayKey: reader.IsDBNull(1) ? null : reader.GetString(1),
                TaskId: reader.IsDBNull(2) ? null : reader.GetString(2),
                RunId: reader.IsDBNull(3) ? null : reader.GetString(3),
                StepId: reader.IsDBNull(4) ? null : reader.GetString(4),
                TurnId: reader.IsDBNull(5) ? null : reader.GetString(5),
                ToolName: reader.GetString(6),
                ArgumentsJson: reader.IsDBNull(7) ? null : reader.GetString(7),
                SideEffectLevel: Enum.TryParse<ToolSideEffectLevel>(reader.GetString(8), out var lvl) ? lvl : ToolSideEffectLevel.ExternalSideEffect,
                Status: Enum.TryParse<ActionExecutionStatus>(reader.GetString(9), out var st) ? st : ActionExecutionStatus.Unknown,
                StartedAtUtc: DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
                CompletedAtUtc: reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
                ResultPreview: reader.IsDBNull(12) ? null : reader.GetString(12),
                Error: reader.IsDBNull(13) ? null : reader.GetString(13),
                ModelId: reader.IsDBNull(14) ? null : reader.GetString(14),
                ProtocolKey: reader.IsDBNull(15) ? null : reader.GetString(15)));
        }
        return result;
    }

    /// <summary>
    /// Every recorded action across ALL tasks, oldest first, for cross-task capability
    /// analysis (agent-intelligence stage: per-(model, protocol) success/repair/verification
    /// rates are aggregated from the durable ledger, not from process memory).
    /// </summary>
    public async Task<List<TaskActionRecord>> GetAllTaskActionsAsync()
    {
        var result = new List<TaskActionRecord>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_actions ORDER BY started_at ASC;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadTaskAction(reader));
        }
        return result;
    }

    /// <summary>Maps a task_actions row to a <see cref="TaskActionRecord"/>.</summary>
    private static TaskActionRecord ReadTaskAction(System.Data.Common.DbDataReader reader)
        => new(
            ActionId: reader.GetString(0),
            ReplayKey: reader.IsDBNull(1) ? null : reader.GetString(1),
            TaskId: reader.IsDBNull(2) ? null : reader.GetString(2),
            RunId: reader.IsDBNull(3) ? null : reader.GetString(3),
            StepId: reader.IsDBNull(4) ? null : reader.GetString(4),
            TurnId: reader.IsDBNull(5) ? null : reader.GetString(5),
            ToolName: reader.GetString(6),
            ArgumentsJson: reader.IsDBNull(7) ? null : reader.GetString(7),
            SideEffectLevel: Enum.TryParse<ToolSideEffectLevel>(reader.GetString(8), out var lvl) ? lvl : ToolSideEffectLevel.ExternalSideEffect,
            Status: Enum.TryParse<ActionExecutionStatus>(reader.GetString(9), out var st) ? st : ActionExecutionStatus.Unknown,
            StartedAtUtc: DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
            CompletedAtUtc: reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
            ResultPreview: reader.IsDBNull(12) ? null : reader.GetString(12),
            Error: reader.IsDBNull(13) ? null : reader.GetString(13),
            ModelId: reader.IsDBNull(14) ? null : reader.GetString(14),
            ProtocolKey: reader.IsDBNull(15) ? null : reader.GetString(15));

    /// <summary>A single action record by its primary key (lifecycle completion reads it to
    /// preserve the start row's identity fields).</summary>
    public async Task<TaskActionRecord?> GetTaskActionAsync(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return null;
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_actions WHERE action_id = @actionId;";
        command.Parameters.AddWithValue("@actionId", actionId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new TaskActionRecord(
            ActionId: reader.GetString(0),
            ReplayKey: reader.IsDBNull(1) ? null : reader.GetString(1),
            TaskId: reader.IsDBNull(2) ? null : reader.GetString(2),
            RunId: reader.IsDBNull(3) ? null : reader.GetString(3),
            StepId: reader.IsDBNull(4) ? null : reader.GetString(4),
            TurnId: reader.IsDBNull(5) ? null : reader.GetString(5),
            ToolName: reader.GetString(6),
            ArgumentsJson: reader.IsDBNull(7) ? null : reader.GetString(7),
            SideEffectLevel: Enum.TryParse<ToolSideEffectLevel>(reader.GetString(8), out var lvl) ? lvl : ToolSideEffectLevel.ExternalSideEffect,
            Status: Enum.TryParse<ActionExecutionStatus>(reader.GetString(9), out var st) ? st : ActionExecutionStatus.Unknown,
            StartedAtUtc: DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
            CompletedAtUtc: reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
            ResultPreview: reader.IsDBNull(12) ? null : reader.GetString(12),
            Error: reader.IsDBNull(13) ? null : reader.GetString(13),
            ModelId: reader.IsDBNull(14) ? null : reader.GetString(14),
            ProtocolKey: reader.IsDBNull(15) ? null : reader.GetString(15));
    }

    /// <summary>
    /// Marks every action a run left InProgress as <see cref="ActionExecutionStatus.Unknown"/>
    /// (review §10): a process death mid-execution means we do NOT know whether the action
    /// completed, failed, or is still running — the durable record must say so, so recovery
    /// inspects instead of blindly re-executing.
    /// </summary>
    public async Task MarkInProgressActionsUnknownAsync(string runId, DateTime when)
    {
        if (string.IsNullOrEmpty(runId)) return;
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE task_actions SET status = @status, completed_at = @when
            WHERE run_id = @runId AND status = @inProgress;";
        command.Parameters.AddWithValue("@status", ActionExecutionStatus.Unknown.ToString());
        command.Parameters.AddWithValue("@when", when.ToString("o"));
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@inProgress", ActionExecutionStatus.InProgress.ToString());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Persists one evidence row (INSERT OR REPLACE by evidence id).</summary>
    public async Task SaveExecutionEvidenceAsync(DurableEvidenceRecord evidence)
    {
        if (evidence == null) throw new ArgumentNullException(nameof(evidence));
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO execution_evidence
                (evidence_id, task_id, run_id, step_id, action_id, workspace_version, kind,
                 subject, tool_name, exit_code, payload_json, timestamp_utc, invalidated_at)
            VALUES
                (@evidenceId, @taskId, @runId, @stepId, @actionId, @workspaceVersion, @kind,
                 @subject, @toolName, @exitCode, @payloadJson, @timestampUtc, @invalidatedAt);";
        command.Parameters.AddWithValue("@evidenceId", evidence.EvidenceId);
        command.Parameters.AddWithValue("@taskId", (object?)evidence.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@runId", (object?)evidence.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("@stepId", (object?)evidence.StepId ?? DBNull.Value);
        command.Parameters.AddWithValue("@actionId", (object?)evidence.ActionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@workspaceVersion", evidence.WorkspaceVersion);
        command.Parameters.AddWithValue("@kind", evidence.Kind.ToString());
        command.Parameters.AddWithValue("@subject", (object?)evidence.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("@toolName", (object?)evidence.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("@exitCode", (object?)evidence.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@payloadJson", (object?)evidence.PayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@timestampUtc", evidence.TimestampUtc.ToString("o"));
        command.Parameters.AddWithValue("@invalidatedAt", (object?)(evidence.InvalidatedAtUtc?.ToString("o")) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>All evidence rows for a run (or a task when runId is null), oldest first.</summary>
    public async Task<List<DurableEvidenceRecord>> GetExecutionEvidenceAsync(string? taskId, string? runId)
    {
        var result = new List<DurableEvidenceRecord>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        if (!string.IsNullOrEmpty(runId))
        {
            command.CommandText = "SELECT * FROM execution_evidence WHERE run_id = @runId ORDER BY timestamp_utc ASC;";
            command.Parameters.AddWithValue("@runId", runId);
        }
        else
        {
            command.CommandText = "SELECT * FROM execution_evidence WHERE task_id = @taskId ORDER BY timestamp_utc ASC;";
            command.Parameters.AddWithValue("@taskId", (object?)taskId ?? DBNull.Value);
        }
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new DurableEvidenceRecord(
                EvidenceId: reader.GetString(0),
                TaskId: reader.IsDBNull(1) ? null : reader.GetString(1),
                RunId: reader.IsDBNull(2) ? null : reader.GetString(2),
                StepId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ActionId: reader.IsDBNull(4) ? null : reader.GetString(4),
                WorkspaceVersion: reader.GetInt32(5),
                Kind: Enum.TryParse<EvidenceKind>(reader.GetString(6), out var kind) ? kind : EvidenceKind.Unspecified,
                Subject: reader.IsDBNull(7) ? null : reader.GetString(7),
                ToolName: reader.IsDBNull(8) ? null : reader.GetString(8),
                ExitCode: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                PayloadJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                TimestampUtc: DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
                InvalidatedAtUtc: reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    /// <summary>
    /// Durably invalidates evidence: every CURRENT row for the TASK recorded at a workspace
    /// version BELOW the given version is stamped invalidated (a file change happened after
    /// it, so it can no longer verify the current files). Task-scoped — not run-scoped —
    /// because a recovered run inherits the previous run's surviving evidence and its
    /// workspace version: a file change in the new run must invalidate inherited evidence
    /// too, or stale builds would verify forever.
    /// </summary>
    public async Task InvalidateExecutionEvidenceAsync(string taskId, int belowWorkspaceVersion, DateTime invalidatedAt)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE execution_evidence SET invalidated_at = @invalidatedAt
            WHERE task_id = @taskId AND workspace_version < @belowVersion AND invalidated_at IS NULL;";
        command.Parameters.AddWithValue("@invalidatedAt", invalidatedAt.ToString("o"));
        command.Parameters.AddWithValue("@taskId", taskId);
        command.Parameters.AddWithValue("@belowVersion", belowWorkspaceVersion);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The task's CURRENT (non-invalidated) evidence rows across all runs — the durable
    /// source a recovered run rehydrates from, so verification facts survive process death.
    /// </summary>
    public async Task<List<DurableEvidenceRecord>> GetCurrentExecutionEvidenceAsync(string taskId)
    {
        var result = new List<DurableEvidenceRecord>();
        if (string.IsNullOrEmpty(taskId)) return result;
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM execution_evidence WHERE task_id = @taskId AND invalidated_at IS NULL ORDER BY timestamp_utc ASC;";
        command.Parameters.AddWithValue("@taskId", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(MapEvidenceRecord(reader));
        }
        return result;
    }

    private static DurableEvidenceRecord MapEvidenceRecord(SqliteDataReader reader)
        => new(
            EvidenceId: reader.GetString(0),
            TaskId: reader.IsDBNull(1) ? null : reader.GetString(1),
            RunId: reader.IsDBNull(2) ? null : reader.GetString(2),
            StepId: reader.IsDBNull(3) ? null : reader.GetString(3),
            ActionId: reader.IsDBNull(4) ? null : reader.GetString(4),
            WorkspaceVersion: reader.GetInt32(5),
            Kind: Enum.TryParse<EvidenceKind>(reader.GetString(6), out var kind) ? kind : EvidenceKind.Unspecified,
            Subject: reader.IsDBNull(7) ? null : reader.GetString(7),
            ToolName: reader.IsDBNull(8) ? null : reader.GetString(8),
            ExitCode: reader.IsDBNull(9) ? null : reader.GetInt32(9),
            PayloadJson: reader.IsDBNull(10) ? null : reader.GetString(10),
            TimestampUtc: DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
            InvalidatedAtUtc: reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind));

    /// <summary>Persists one supervisor decision (INSERT OR REPLACE by decision id).</summary>
    public async Task SaveExecutionDecisionAsync(ExecutionDecisionRecord decision)
    {
        if (decision == null) throw new ArgumentNullException(nameof(decision));
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO execution_decisions
                (decision_id, task_id, run_id, step_id, decision, reason, timestamp_utc)
            VALUES
                (@decisionId, @taskId, @runId, @stepId, @decision, @reason, @timestampUtc);";
        command.Parameters.AddWithValue("@decisionId", decision.DecisionId);
        command.Parameters.AddWithValue("@taskId", (object?)decision.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@runId", (object?)decision.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("@stepId", (object?)decision.StepId ?? DBNull.Value);
        command.Parameters.AddWithValue("@decision", decision.Decision.ToString());
        command.Parameters.AddWithValue("@reason", decision.Reason.ToString());
        command.Parameters.AddWithValue("@timestampUtc", decision.TimestampUtc.ToString("o"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>All recorded supervisor decisions for a run (or a task when runId is null), oldest first.</summary>
    public async Task<List<ExecutionDecisionRecord>> GetExecutionDecisionsAsync(string? taskId, string? runId)
    {
        var result = new List<ExecutionDecisionRecord>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        if (!string.IsNullOrEmpty(runId))
        {
            command.CommandText = "SELECT * FROM execution_decisions WHERE run_id = @runId ORDER BY timestamp_utc ASC;";
            command.Parameters.AddWithValue("@runId", runId);
        }
        else
        {
            command.CommandText = "SELECT * FROM execution_decisions WHERE task_id = @taskId ORDER BY timestamp_utc ASC;";
            command.Parameters.AddWithValue("@taskId", (object?)taskId ?? DBNull.Value);
        }
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ExecutionDecisionRecord(
                DecisionId: reader.GetString(0),
                TaskId: reader.IsDBNull(1) ? null : reader.GetString(1),
                RunId: reader.IsDBNull(2) ? null : reader.GetString(2),
                StepId: reader.IsDBNull(3) ? null : reader.GetString(3),
                Decision: Enum.TryParse<ExecutionDecision>(reader.GetString(4), out var d) ? d : ExecutionDecision.ContinueStep,
                Reason: Enum.TryParse<ContinuationReason>(reader.GetString(5), out var r) ? r : ContinuationReason.StepIncomplete,
                TimestampUtc: DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    // ===== Durable typed TaskStep mirror (review §3) ========================================

    /// <summary>Replaces a task's persisted typed-step mirror (delete + insert in one
    /// transaction). Written whenever the plan checklist changes; read on recovery so step
    /// semantics survive restarts without re-deriving from English text.</summary>
    public async Task SaveTaskStepsAsync(string taskId, IReadOnlyList<TaskStep> steps)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        await using var connection = await CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var del = connection.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)transaction;
            del.CommandText = "DELETE FROM task_steps WHERE task_id = @taskId;";
            del.Parameters.AddWithValue("@taskId", taskId);
            await del.ExecuteNonQueryAsync();
        }
        string now = DateTime.UtcNow.ToString("o");
        foreach (var step in steps)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO task_steps
                    (step_id, task_id, order_index, title, status, expected_action_kind,
                     allowed_tools_json, required_skills_json, expected_artifacts_json,
                     verification_criteria_json, completion_condition, attempt_count,
                     last_action_id, started_at, completed_at, updated_at)
                VALUES
                    (@stepId, @taskId, @orderIndex, @title, @status, @expectedActionKind,
                     @allowedToolsJson, @requiredSkillsJson, @expectedArtifactsJson,
                     @verificationCriteriaJson, @completionCondition, @attemptCount,
                     @lastActionId, @startedAt, @completedAt, @updatedAt);";
            cmd.Parameters.AddWithValue("@stepId", step.StepId);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@orderIndex", step.Order);
            cmd.Parameters.AddWithValue("@title", step.Title);
            cmd.Parameters.AddWithValue("@status", step.Status.ToString());
            cmd.Parameters.AddWithValue("@expectedActionKind", step.ExpectedActionKind.ToString());
            cmd.Parameters.AddWithValue("@allowedToolsJson", step.AllowedTools == null ? (object)DBNull.Value : System.Text.Json.JsonSerializer.Serialize(step.AllowedTools.OrderBy(n => n, StringComparer.Ordinal)));
            cmd.Parameters.AddWithValue("@requiredSkillsJson", (object)System.Text.Json.JsonSerializer.Serialize(step.RequiredSkills));
            cmd.Parameters.AddWithValue("@expectedArtifactsJson", (object)System.Text.Json.JsonSerializer.Serialize(step.ExpectedArtifacts));
            cmd.Parameters.AddWithValue("@verificationCriteriaJson", (object)System.Text.Json.JsonSerializer.Serialize(step.VerificationCriteria));
            cmd.Parameters.AddWithValue("@completionCondition", (object?)step.CompletionCondition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@attemptCount", step.AttemptCount);
            cmd.Parameters.AddWithValue("@lastActionId", (object?)step.LastActionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@startedAt", (object?)(step.StartedAt?.ToString("o")) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@completedAt", (object?)(step.CompletedAt?.ToString("o")) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updatedAt", now);
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    /// <summary>The task's persisted typed steps in plan order, or an empty list.</summary>
    public async Task<List<TaskStep>> GetTaskStepsAsync(string taskId)
    {
        var result = new List<TaskStep>();
        if (string.IsNullOrEmpty(taskId)) return result;
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_steps WHERE task_id = @taskId ORDER BY order_index ASC;";
        command.Parameters.AddWithValue("@taskId", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new TaskStep(
                StepId: reader.GetString(0),
                TaskId: reader.GetString(1),
                Order: reader.GetInt32(2),
                Title: reader.GetString(3),
                Status: Enum.TryParse<TaskStepStatus>(reader.GetString(4), out var st) ? st : TaskStepStatus.Pending,
                ExpectedActionKind: Enum.TryParse<StepActionKind>(reader.GetString(5), out var kind) ? kind : StepActionKind.None,
                AllowedTools: reader.IsDBNull(6) ? null : new HashSet<string>(System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                RequiredSkills: reader.IsDBNull(7) ? new List<string>() : System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? new List<string>(),
                ExpectedArtifacts: reader.IsDBNull(8) ? new List<string>() : System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? new List<string>(),
                VerificationCriteria: reader.IsDBNull(9) ? new List<string>() : System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? new List<string>(),
                CompletionCondition: reader.IsDBNull(10) ? null : reader.GetString(10),
                AttemptCount: reader.GetInt32(11),
                LastActionId: reader.IsDBNull(12) ? null : reader.GetString(12),
                StartedAt: reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind),
                CompletedAt: reader.IsDBNull(14) ? null : DateTime.Parse(reader.GetString(14), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    /// <summary>Deletes a task's persisted typed-step mirror (task resolved / plan replaced).</summary>
    public async Task ClearTaskStepsAsync(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM task_steps WHERE task_id = @taskId;";
        command.Parameters.AddWithValue("@taskId", taskId);
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
            INSERT OR REPLACE INTO queued_messages (id, session_id, content, mode, status, created_at, attempt_count, position, task_id)
            VALUES (@id, @sessionId, @content, @mode, @status, @createdAt, @attemptCount, @position, @taskId);
        ";
        command.Parameters.AddWithValue("@id", msg.Id.ToString());
        command.Parameters.AddWithValue("@sessionId", msg.SessionId);
        command.Parameters.AddWithValue("@content", msg.Content);
        command.Parameters.AddWithValue("@mode", (int)msg.Mode);
        command.Parameters.AddWithValue("@status", (int)msg.Status);
        command.Parameters.AddWithValue("@createdAt", msg.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("@attemptCount", msg.AttemptCount);
        command.Parameters.AddWithValue("@position", msg.Position);
        command.Parameters.AddWithValue("@taskId", (object?)msg.TaskId ?? DBNull.Value);

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
        command.CommandText = "SELECT id, session_id, content, mode, status, created_at, attempt_count, position, task_id FROM queued_messages ORDER BY position ASC, created_at ASC;";

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
                AttemptCount = reader.GetInt32(6),
                Position = reader.GetInt32(7),
                TaskId = reader.IsDBNull(8) ? null : reader.GetString(8)
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

    #region File changes (workbench)

    /// <summary>
    /// Persists a factual file change (before/after hashes + real diff) captured around a
    /// file-mutating tool call. The Changes tab reads this; it is evidence, not narration.
    /// </summary>
    public async Task AddFileChangeAsync(FileChange change)
    {
        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO file_changes (change_id, session_id, task_id, path, tool, before_hash, after_hash, diff, added_lines, deleted_lines, created_at)
            VALUES (@id, @sessionId, @taskId, @path, @tool, @beforeHash, @afterHash, @diff, @added, @deleted, @createdAt);
        ";
        command.Parameters.AddWithValue("@id", change.ChangeId);
        command.Parameters.AddWithValue("@sessionId", change.SessionId);
        command.Parameters.AddWithValue("@taskId", (object?)change.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@path", change.Path);
        command.Parameters.AddWithValue("@tool", change.Tool);
        command.Parameters.AddWithValue("@beforeHash", (object?)change.BeforeHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@afterHash", (object?)change.AfterHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@diff", (object?)change.Diff ?? DBNull.Value);
        command.Parameters.AddWithValue("@added", change.AddedLines);
        command.Parameters.AddWithValue("@deleted", change.DeletedLines);
        command.Parameters.AddWithValue("@createdAt", change.TimestampUtc.ToString("o"));

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Loads the factual change log for a session, newest first (bounded for UI display).
    /// </summary>
    public async Task<List<FileChange>> GetFileChangesAsync(string sessionId, int limit = 100)
    {
        var result = new List<FileChange>();

        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT change_id, session_id, task_id, path, tool, before_hash, after_hash, diff, added_lines, deleted_lines, created_at
            FROM file_changes WHERE session_id = @sessionId
            ORDER BY created_at DESC LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new FileChange(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    /// <summary>
    /// Loads the factual change log for a TASK (newest first, bounded for UI display). The
    /// right-side workbench is a projection of task execution state, so the panel must query
    /// by task_id — never by session_id — otherwise switching tasks in the same chat leaks
    /// the previous task's changes into the new task's panel.
    /// </summary>
    public async Task<List<FileChange>> GetFileChangesByTaskAsync(string taskId, int limit = 100)
    {
        var result = new List<FileChange>();

        await using var connection = await CreateConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT change_id, session_id, task_id, path, tool, before_hash, after_hash, diff, added_lines, deleted_lines, created_at
            FROM file_changes WHERE task_id = @taskId
            ORDER BY created_at DESC LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@taskId", taskId);
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new FileChange(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    #endregion

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

    #region Tool activity, artifacts & execution events (durable execution state)

    /// <summary>
    /// Persists one tool invocation. Called by ToolExecutor.FinishToolCallAsync; the in-memory
    /// session activity list is a cache of this table, not the source of truth.
    /// </summary>
    public async Task AddToolActivityAsync(ToolActivityRow row)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO tool_activity (activity_id, session_id, task_id, run_id, tool_name, args_json, success, output_preview, timestamp_utc)
            VALUES (@id, @sessionId, @taskId, @runId, @toolName, @argsJson, @success, @preview, @ts);
        ";
        command.Parameters.AddWithValue("@id", row.ActivityId);
        command.Parameters.AddWithValue("@sessionId", row.SessionId);
        command.Parameters.AddWithValue("@taskId", (object?)row.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@runId", (object?)row.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("@toolName", row.ToolName);
        command.Parameters.AddWithValue("@argsJson", (object?)row.ArgsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@success", row.Success ? 1 : 0);
        command.Parameters.AddWithValue("@preview", (object?)row.OutputPreview ?? DBNull.Value);
        command.Parameters.AddWithValue("@ts", row.TimestampUtc.ToString("o"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Loads the durable tool-activity log for a session, oldest first (bounded).
    /// </summary>
    public async Task<List<ToolActivityRow>> GetToolActivityBySessionAsync(string sessionId, int limit = 2000)
    {
        var result = new List<ToolActivityRow>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT activity_id, session_id, task_id, run_id, tool_name, args_json, success, output_preview, timestamp_utc
            FROM tool_activity WHERE session_id = @sessionId
            ORDER BY timestamp_utc ASC LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadToolActivity(reader));
        }
        return result;
    }

    /// <summary>
    /// Loads the durable tool-activity log for a task, oldest first (bounded). Task-scoped so
    /// the Files panel never leaks another task's activity into the current one.
    /// </summary>
    public async Task<List<ToolActivityRow>> GetToolActivityByTaskAsync(string taskId, int limit = 2000)
    {
        var result = new List<ToolActivityRow>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT activity_id, session_id, task_id, run_id, tool_name, args_json, success, output_preview, timestamp_utc
            FROM tool_activity WHERE task_id = @taskId
            ORDER BY timestamp_utc ASC LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@taskId", taskId);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadToolActivity(reader));
        }
        return result;
    }

    private static ToolActivityRow ReadToolActivity(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        int Ord(string name) => reader.GetOrdinal(name);
        return new ToolActivityRow(
            reader.GetString(Ord("activity_id")),
            reader.GetString(Ord("session_id")),
            reader.IsDBNull(Ord("task_id")) ? null : reader.GetString(Ord("task_id")),
            reader.IsDBNull(Ord("run_id")) ? null : reader.GetString(Ord("run_id")),
            reader.GetString(Ord("tool_name")),
            reader.IsDBNull(Ord("args_json")) ? string.Empty : reader.GetString(Ord("args_json")),
            reader.GetInt32(Ord("success")) != 0,
            reader.IsDBNull(Ord("output_preview")) ? string.Empty : reader.GetString(Ord("output_preview")),
            ParseUtc(reader.GetString(Ord("timestamp_utc"))));
    }

    /// <summary>
    /// Persists an artifact revision. The caller marks previous revisions of the same path
    /// stale first (see <see cref="MarkArtifactsStaleAsync"/>).
    /// </summary>
    public async Task AddArtifactAsync(ArtifactRow row)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO artifacts (artifact_id, session_id, task_id, path, artifact_type, content_hash, created_at_utc, updated_at_utc, previewable, is_current)
            VALUES (@id, @sessionId, @taskId, @path, @type, @hash, @createdAt, @updatedAt, @previewable, @isCurrent);
        ";
        command.Parameters.AddWithValue("@id", row.ArtifactId);
        command.Parameters.AddWithValue("@sessionId", row.SessionId);
        command.Parameters.AddWithValue("@taskId", (object?)row.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@path", row.Path);
        command.Parameters.AddWithValue("@type", row.ArtifactType);
        command.Parameters.AddWithValue("@hash", (object?)row.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", row.CreatedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("@updatedAt", row.UpdatedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("@previewable", row.Previewable ? 1 : 0);
        command.Parameters.AddWithValue("@isCurrent", row.IsCurrent ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Marks every previous revision of a path in a session as superseded (is_current = 0),
    /// so the Preview tab always shows the latest content without scanning the workspace.
    /// </summary>
    public async Task MarkArtifactsStaleAsync(string sessionId, string path)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE artifacts SET is_current = 0
            WHERE session_id = @sessionId AND path = @path;
        ";
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@path", path);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Loads the durable artifact registry for a session (current revisions only by default).
    /// </summary>
    public async Task<List<ArtifactRow>> GetArtifactsBySessionAsync(string sessionId, bool currentOnly = true)
    {
        var result = new List<ArtifactRow>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = currentOnly
            ? "SELECT artifact_id, session_id, task_id, path, artifact_type, content_hash, created_at_utc, updated_at_utc, previewable, is_current FROM artifacts WHERE session_id = @sessionId AND is_current = 1 ORDER BY updated_at_utc DESC;"
            : "SELECT artifact_id, session_id, task_id, path, artifact_type, content_hash, created_at_utc, updated_at_utc, previewable, is_current FROM artifacts WHERE session_id = @sessionId ORDER BY updated_at_utc DESC;";
        command.Parameters.AddWithValue("@sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadArtifact(reader));
        }
        return result;
    }

    /// <summary>
    /// Loads the durable artifact registry for a task (current revisions only by default).
    /// </summary>
    public async Task<List<ArtifactRow>> GetArtifactsByTaskAsync(string taskId, bool currentOnly = true)
    {
        var result = new List<ArtifactRow>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = currentOnly
            ? "SELECT artifact_id, session_id, task_id, path, artifact_type, content_hash, created_at_utc, updated_at_utc, previewable, is_current FROM artifacts WHERE task_id = @taskId AND is_current = 1 ORDER BY updated_at_utc DESC;"
            : "SELECT artifact_id, session_id, task_id, path, artifact_type, content_hash, created_at_utc, updated_at_utc, previewable, is_current FROM artifacts WHERE task_id = @taskId ORDER BY updated_at_utc DESC;";
        command.Parameters.AddWithValue("@taskId", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadArtifact(reader));
        }
        return result;
    }

    private static ArtifactRow ReadArtifact(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        int Ord(string name) => reader.GetOrdinal(name);
        return new ArtifactRow(
            reader.GetString(Ord("artifact_id")),
            reader.GetString(Ord("session_id")),
            reader.IsDBNull(Ord("task_id")) ? null : reader.GetString(Ord("task_id")),
            reader.GetString(Ord("path")),
            reader.GetString(Ord("artifact_type")),
            reader.IsDBNull(Ord("content_hash")) ? null : reader.GetString(Ord("content_hash")),
            ParseUtc(reader.GetString(Ord("created_at_utc"))),
            ParseUtc(reader.GetString(Ord("updated_at_utc"))),
            reader.GetInt32(Ord("previewable")) != 0,
            reader.GetInt32(Ord("is_current")) != 0);
    }

    /// <summary>
    /// Appends one durable execution event to the stream.
    /// </summary>
    public async Task AddExecutionEventAsync(ExecutionEventRow row)
    {
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO execution_events (event_id, session_id, task_id, run_id, event_type, timestamp_utc, tool_name, path, payload_json)
            VALUES (@id, @sessionId, @taskId, @runId, @type, @ts, @toolName, @path, @payload);
        ";
        command.Parameters.AddWithValue("@id", row.EventId);
        command.Parameters.AddWithValue("@sessionId", row.SessionId);
        command.Parameters.AddWithValue("@taskId", (object?)row.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@runId", (object?)row.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", row.EventType);
        command.Parameters.AddWithValue("@ts", row.TimestampUtc.ToString("o"));
        command.Parameters.AddWithValue("@toolName", (object?)row.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("@path", (object?)row.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("@payload", (object?)row.PayloadJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Loads the durable event stream for a task, oldest first (bounded).
    /// </summary>
    public async Task<List<ExecutionEventRow>> GetExecutionEventsByTaskAsync(string taskId, int limit = 500)
    {
        var result = new List<ExecutionEventRow>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT event_id, session_id, task_id, run_id, event_type, timestamp_utc, tool_name, path, payload_json
            FROM execution_events WHERE task_id = @taskId
            ORDER BY timestamp_utc ASC LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@taskId", taskId);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadExecutionEvent(reader));
        }
        return result;
    }

    /// <summary>
    /// Loads the durable event stream for a session, oldest first (bounded).
    /// </summary>
    public async Task<List<ExecutionEventRow>> GetExecutionEventsBySessionAsync(string sessionId, int limit = 500)
    {
        var result = new List<ExecutionEventRow>();
        await using var connection = await CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT event_id, session_id, task_id, run_id, event_type, timestamp_utc, tool_name, path, payload_json
            FROM execution_events WHERE session_id = @sessionId
            ORDER BY timestamp_utc ASC LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadExecutionEvent(reader));
        }
        return result;
    }

    private static ExecutionEventRow ReadExecutionEvent(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        int Ord(string name) => reader.GetOrdinal(name);
        return new ExecutionEventRow(
            reader.GetString(Ord("event_id")),
            reader.GetString(Ord("session_id")),
            reader.IsDBNull(Ord("task_id")) ? null : reader.GetString(Ord("task_id")),
            reader.IsDBNull(Ord("run_id")) ? null : reader.GetString(Ord("run_id")),
            reader.GetString(Ord("event_type")),
            ParseUtc(reader.GetString(Ord("timestamp_utc"))),
            reader.IsDBNull(Ord("tool_name")) ? null : reader.GetString(Ord("tool_name")),
            reader.IsDBNull(Ord("path")) ? null : reader.GetString(Ord("path")),
            reader.IsDBNull(Ord("payload_json")) ? null : reader.GetString(Ord("payload_json")));
    }

    private static DateTime ParseUtc(string s)
        => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    #endregion
}

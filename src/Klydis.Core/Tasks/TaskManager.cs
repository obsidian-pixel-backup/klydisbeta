using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Klydis.Core.Chat;
using Klydis.Core.Memory;
using Microsoft.Extensions.Logging;
using TaskStatus = Klydis.Core.Chat.TaskStatus;

namespace Klydis.Core.Tasks;

/// <summary>
/// Owns task identity and lifecycle. This is the harness's answer to the architectural
/// finding that plan/queue/artifacts were session-scoped: the session remains a
/// conversation, but execution state now attaches to a durable <see cref="AgentTask"/>.
/// Every user message is classified (new / continue / steer / reopen) BEFORE the model
/// runs, the decision is persisted, and the plan follows the task — so a new task in the
/// same chat can never inherit an old task's checklist, and a steer never loses the
/// current one. The classifier is intentionally deterministic (heuristic) for now; the
/// review allows model-assisted classification to evolve it later. Failures degrade to
/// legacy session-scoped behavior (no task), never to a broken turn.
/// </summary>
public class TaskManager(
    MessageStore store,
    ILogger<TaskManager>? logger = null)
{
    private readonly MessageStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<TaskManager>? _logger = logger;

    // Current task per session, plus the hydration guard so the DB is hit at most once per
    // session per process (the getters can be called on the UI thread on a timer).
    private readonly ConcurrentDictionary<string, AgentTask> _currentBySession = new();
    private readonly HashSet<string> _hydrated = new();

    // Words that mark an explicit relationship to the current task ("also add X",
    // "use Y instead", "change Z", "continue"). Detected first: a steer marker makes the
    // message part of the current task even when it also contains task verbs.
    // NOTE: generic conversational words ("can you", "please", "but", "still") were
    // removed — they misclassified ordinary messages ("Can you explain how this works?")
    // as steers of the current task. The interaction-mode boundary already keeps those
    // messages out of the task layer entirely; the remaining markers are task-relational
    // language only.
    private static readonly string[] SteerMarkers =
    {
        "also", "instead", "actually", "additionally", "however", "and now",
        "now make", "continue", "keep going", "keep working", "don't", "stop",
        "wait", "change", "update", "adjust", "replace", "switch", "remember",
        "as well", "on top of", "furthermore", "moreover",
        // Explicit "do the work now" continuations. Safe to treat as steers because the
        // interaction-mode boundary keeps conversational messages out of this resolver
        // entirely — the resolver only ever sees Task/Autonomous messages, where "begin"/
        // "start"/"proceed" mean "continue the current task's work" (the observed case:
        // "i want you to begin building the project" must keep the SAME task and plan, not
        // spawn a new one via the build-verb + length rule).
        "begin", "start", "proceed", "go ahead", "get started"
    };

    // Verbs that imply a fresh multi-step piece of work. Gated by length and by NOT echoing
    // the current objective, so short steers and plain follow-ups never misfire.
    private static readonly string[] TaskActionVerbs =
    {
        "build", "create", "implement", "develop", "refactor", "migrate", "port",
        "optimize", "analyze", "investigate", "research", "debug", "fix", "test",
        "design", "configure", "integrate", "set up", "make", "write", "document",
        "compare", "evaluate", "review", "add", "install", "deploy", "summarize", "produce"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "your", "you", "are", "was",
        "were", "have", "has", "had", "will", "would", "should", "could", "can", "into",
        "about", "over", "under", "than", "then", "them", "they", "what", "when", "where",
        "which", "while", "there", "here", "each", "also", "just", "very", "not", "but",
        "all", "any", "some", "more", "most", "other", "such", "only", "own", "same"
    };

    /// <summary>
    /// Loads a task by id directly from the store (bypasses the current-task cache; used to
    /// read a superseded or terminal task, e.g. for the completion gate or a reopen).
    /// </summary>
    public async Task<AgentTask?> GetTaskAsync(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return null;
        try
        {
            return await _store.GetTaskAsync(taskId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load task {TaskId}.", taskId);
            return null;
        }
    }

    /// <summary>
    /// The session's current task, or null when none exists yet (legacy session with no
    /// task rows and no plan). Hydrates lazily from the store; a legacy session whose
    /// <c>sessions.plan_json</c> predates tasks gets a task created to carry that plan, so
    /// existing chats keep their checklists without any migration step on the user's side.
    /// </summary>
    public async Task<AgentTask?> GetCurrentTaskAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        await EnsureHydratedAsync(sessionId);
        return _currentBySession.TryGetValue(sessionId, out var task) ? task : null;
    }

    /// <summary>
    /// Classifies the user message against the session's current task and returns the task
    /// the message belongs to — creating, reopening, or reusing it and persisting the
    /// decision. The result is the authoritative task context for the turn: the plan shown,
    /// the queue offered, and the completion gate all follow it.
    /// </summary>
    public async Task<AgentTask> ResolveOrCreateCurrentTaskAsync(string sessionId, string userMessage)
    {
        var current = await GetCurrentTaskAsync(sessionId);
        var kind = Resolve(userMessage, current);

        AgentTask result;
        switch (kind)
        {
            case TaskResolutionKind.NewTask:
                result = AgentTask.Create(sessionId, userMessage.Trim());
                await SaveTaskAsync(result);
                _currentBySession[sessionId] = result;
                _logger?.LogInformation(
                    "Task resolution: NEW TASK {TaskId} for session {SessionId} (previous task: {Previous}). Objective: {Objective}",
                    result.TaskId, sessionId, current?.TaskId ?? "(none)", Truncate(result.Objective));
                break;

            case TaskResolutionKind.ReopenTask when current != null:
                // Completed task being resumed: back to Running through the guarded state
                // machine, plan restored by the caller (the plan follows the task record, so
                // switching back re-arms its checklist).
                result = TaskStateMachine.TryTransition(current, TaskStatus.Running)
                         ?? current with { UpdatedAtUtc = DateTime.UtcNow };
                await SaveTaskAsync(result);
                _currentBySession[sessionId] = result;
                _logger?.LogInformation("Task resolution: REOPEN TASK {TaskId} for session {SessionId}.", result.TaskId, sessionId);
                break;

            case TaskResolutionKind.SteerTask when current != null:
                // Same task, modified in place. The objective stays the anchor; the model
                // refines the plan via the plan tool as before. A task the supervisor left
                // Paused (or otherwise suspended) is re-activated to Running — otherwise the
                // completion gate could never seal it (Paused → Completed is not a legal
                // transition), making the Pause decision a dead end.
                result = ResumeIfSuspended(current);
                await SaveTaskAsync(result);
                _currentBySession[sessionId] = result;
                _logger?.LogInformation("Task resolution: STEER TASK {TaskId} for session {SessionId}.", result.TaskId, sessionId);
                break;

            default:
                result = current ?? AgentTask.Create(sessionId, userMessage.Trim());
                if (current == null)
                {
                    await SaveTaskAsync(result);
                }
                else
                {
                    result = ResumeIfSuspended(result);
                    await SaveTaskAsync(result);
                }
                _currentBySession[sessionId] = result;
                _logger?.LogInformation("Task resolution: CONTINUE TASK {TaskId} for session {SessionId}.", result.TaskId, sessionId);
                break;
        }

        return result;
    }

    /// <summary>
    /// Re-activates a suspended task back to Running when the user continues or steers it.
    /// The supervisor's Pause decision transitions the task to Paused (durable), so resuming
    /// MUST move it back through the guarded state machine — otherwise the completion gate
    /// cannot seal (Paused → Completed is illegal) and a paused task is a dead end. Also
    /// covers Waiting/Blocked/AwaitingUser and terminal states reached through a continue
    /// message (a Failed task can be resumed with a fresh attempt).
    /// </summary>
    private static AgentTask ResumeIfSuspended(AgentTask task)
        => task.Status == TaskStatus.Running
            ? task
            : TaskStateMachine.TryTransition(task, TaskStatus.Running) ?? task with { UpdatedAtUtc = DateTime.UtcNow };

    /// <summary>
    /// Deterministic classification. Order matters: explicit relationship language wins
    /// (steer), then a substantial fresh-task phrasing that doesn't echo the objective
    /// (new), otherwise the message continues the current task.
    /// </summary>
    public TaskResolutionKind Resolve(string userMessage, AgentTask? current)
    {
        if (current == null) return TaskResolutionKind.NewTask;
        if (string.IsNullOrWhiteSpace(userMessage)) return TaskResolutionKind.ContinueTask;

        string lower = userMessage.Trim().ToLowerInvariant();

        if (current.Status == TaskStatus.Completed)
        {
            // The last task is sealed. A message that clearly returns to it reopens it;
            // anything else starts fresh.
            return Overlaps(userMessage, current.Objective)
                ? TaskResolutionKind.ReopenTask
                : TaskResolutionKind.NewTask;
        }

        if (SteerMarkers.Any(m => lower.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return TaskResolutionKind.SteerTask;
        }

        bool substantial = lower.Length >= 40
            && TaskActionVerbs.Any(v => lower.Contains(v, StringComparison.OrdinalIgnoreCase));
        bool echoesObjective = Overlaps(userMessage, current.Objective);

        if (substantial && !echoesObjective)
        {
            return TaskResolutionKind.NewTask;
        }

        return TaskResolutionKind.ContinueTask;
    }

    /// <summary>
    /// Persists the task's plan JSON to its record (used by the plan tool's mutation path so
    /// the checklist always follows the task). Null clears it.
    /// </summary>
    public async Task SavePlanAsync(string taskId, string? planJson)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        try
        {
            await _store.SaveTaskPlanAsync(taskId, planJson);
        }
        catch (Exception ex)
        {
            // P0.8: persistence failure is SURFACED, never swallowed. A plan mutation that
            // the store rejected must not look persisted: the completion gate reads the
            // persisted plan, so a silently-dropped write would make "done" diverge from
            // what the harness can verify.
            _logger?.LogError(ex, "Failed to persist plan for task {TaskId}; the plan write was NOT durable.", taskId);
            throw;
        }
    }

    /// <summary>
    /// The task's persisted plan JSON, or null. Used when switching back to a task whose
    /// plan must be restored into the session's active plan slot.
    /// </summary>
    public async Task<string?> GetPlanAsync(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return null;
        try
        {
            var task = await _store.GetTaskAsync(taskId);
            return task?.PlanJson;
        }
        catch (Exception ex)
        {
            // P0: a storage failure must NOT look like "the task has no plan". null means
            // KNOWN-EMPTY; an exception means UNAVAILABLE. The caller decides the fail-closed
            // policy (e.g. refuse task tools / clear task state) instead of silently
            // substituting an empty plan that recovery would later undo.
            _logger?.LogError(ex, "Failed to read plan for task {TaskId}; plan state is UNAVAILABLE (not empty).", taskId);
            throw;
        }
    }

    /// <summary>
    /// Persists a task (upsert). Public so the runtime (e.g. the completion gate sealing a
    /// task) can persist supervised state transitions through the same guarded path.
    /// </summary>
    public async Task SaveTaskAsync(AgentTask task)
    {
        try
        {
            await _store.SaveTaskAsync(task);
        }
        catch (Exception ex)
        {
            // P0.8: a task state transition that the store rejected must NOT look persisted.
            // The caller decides the policy (fail-closed task resolution, completion sealing)
            // from the exception — swallowing it here would let the runtime believe a task
            // was Completed while the database still says Running.
            _logger?.LogError(ex, "Failed to persist task {TaskId}; the task state transition was NOT durable.", task.TaskId);
            throw;
        }
    }

    private async Task EnsureHydratedAsync(string sessionId)
    {
        lock (_hydrated)
        {
            if (_hydrated.Contains(sessionId)) return;
        }

        // P0: hydration is marked complete ONLY after the entire read/migration succeeded.
        // The previous code added the session to _hydrated BEFORE the storage work, so a
        // failed hydration permanently skipped all later hydration for the process — and the
        // catch "continued without task scoping", i.e. task state silently became
        // unavailable while the runtime kept executing. Now a failure rethrows: the caller
        // (task resolution) fails closed, and the next turn retries hydration.
        try
        {
            var latest = await _store.GetLatestTaskAsync(sessionId);
            if (latest != null)
            {
                _currentBySession[sessionId] = latest;
            }
            else
            {
                // Legacy migration: a session with a plan but no task yet. Carry the plan into
                // a freshly created task so existing chats keep their checklists under the new
                // task-scoped model without any user-visible migration.
                var session = await _store.GetSessionAsync(sessionId);
                if (session?.PlanJson != null)
                {
                    var legacy = AgentTask.Create(sessionId, session.Title ?? "Conversation task");
                    legacy = legacy with { PlanJson = session.PlanJson };
                    await SaveTaskAsync(legacy);
                    _currentBySession[sessionId] = legacy;
                    _logger?.LogInformation(
                        "Migrated legacy session {SessionId} to task {TaskId} carrying its existing plan.",
                        sessionId, legacy.TaskId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to hydrate task state for session {SessionId}; task state is UNAVAILABLE.", sessionId);
            throw;
        }

        lock (_hydrated)
        {
            _hydrated.Add(sessionId);
        }
    }

    private static bool Overlaps(string a, string b)
    {
        var wa = MeaningfulWords(a);
        var wb = MeaningfulWords(b);
        if (wa.Count == 0 || wb.Count == 0) return false;
        int shared = wa.Count(w => wb.Contains(w));
        return (double)shared / Math.Min(wa.Count, wb.Count) >= 0.35;
    }

    private static List<string> MeaningfulWords(string text)
    {
        var words = text.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '(', ')', '"', '\'', '?', '!', '-', '_', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var raw in words)
        {
            string word = raw.Trim().TrimEnd('.', ',', '!', '?').ToLowerInvariant();
            if (word.Length >= 4 && !StopWords.Contains(word) && word.All(char.IsLetterOrDigit))
            {
                result.Add(word);
            }
        }
        return result;
    }

    private static string Truncate(string text, int max = 120)
        => text.Length <= max ? text : text[..max] + "…";
}

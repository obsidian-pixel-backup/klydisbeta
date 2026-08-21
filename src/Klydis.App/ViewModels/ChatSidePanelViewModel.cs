using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.Core.Chat;
using Klydis.Core.Diagnostics;
using Klydis.Core.Memory;
using Klydis.Core.Workbench;

namespace Klydis.App.ViewModels;

/// <summary>
/// Tabs of the right-side chat panel (modeled on the agent-workbench panels of desktop
/// coding agents: queue, files, changes, preview, terminal, notes).
/// </summary>
public enum SidePanelTab
{
    Queue,
    Plan,
    Files,
    Changes,
    Preview,
    Terminal,
    Notes
}

/// <summary>
/// A file the agent worked on — either a git status entry or a recently modified file in
/// the workspace (when no git repository is present).
/// </summary>
public partial class WorkspaceFileItem : ObservableObject
{
    public string FullPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public string ModifiedText => LastModified == default ? string.Empty : LastModified.ToString("HH:mm:ss");

    // Factual diff stats + diff text (workbench §7–§8): populated from the persisted
    // file-change log captured around file-mutating tools — real filesystem evidence, never
    // model-generated narration.
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public string DiffText { get; init; } = string.Empty;
    public string ChangeStats => Additions == 0 && Deletions == 0 ? string.Empty : $"+{Additions} −{Deletions}";
}

/// <summary>
/// A file that can be rendered in the Preview tab (HTML via the embedded browser, markdown
/// via MdXaml, or any text file as plain text).
/// </summary>
public partial class PreviewArtifact : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string Kind { get; init; } = "text"; // html | md | text
    public string KindBadge => Kind.ToUpperInvariant();
}

/// <summary>
/// A read-only row in the model's task plan (todo list) surfaced in the Plan tab.
/// </summary>
public partial class PlanItemVm : ObservableObject
{
    public string Text { get; init; } = string.Empty;
    public bool IsDone { get; init; }

    public PlanItemVm(string text, bool isDone)
    {
        Text = text;
        IsDone = isDone;
    }
}

/// <summary>
/// One entry in the Terminal tab's transcript: the exact command the model asked the shell
/// to run (input) and the result the harness received (output). Rendered as a bracketed
/// terminal block — the model's terminal usage, not debug logs.
/// </summary>
public sealed record TerminalEntryItem(string Command, string Output, bool Success, DateTime Timestamp)
{
    public string StatusBadge => Success ? "ok" : "ERR";
    public string Prompt => Command.Contains("\n", StringComparison.Ordinal)
        ? "$ " + Command.Replace("\n", "\n> ")
        : "$ " + Command;
}

/// <summary>
/// A user-authored note pinned to the current chat. Notes are persisted per session and
/// injected into the model's system prompt on every generation, so they act as durable
/// steering context ("keep X read-only", "verify before claiming done") without re-sending
/// messages.
/// </summary>
public partial class SessionNoteItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; }
    public string UpdatedText => UpdatedAt == default ? string.Empty : UpdatedAt.ToString("MMM d, HH:mm");
}

/// <summary>
/// ViewModel for the right-side chat panel. Owned by <see cref="ChatViewModel"/> and exposed
/// as <c>SidePanel</c>; the panel view's DataContext stays the ChatViewModel so queue commands
/// and message bindings keep working unchanged.
/// </summary>
public partial class ChatSidePanelViewModel : ObservableObject, IDisposable
{
    private readonly ChatViewModel _owner;
    private readonly MessageStore _messageStore;
    private readonly ToolExecutor _toolExecutor;
    private readonly DispatcherTimer _refreshTimer;
    private string? _currentSessionId;
    private string? _editingNoteId;

    // Base directory used to render file paths relative. Resolved once; the panel data
    // itself is derived purely from this session's recorded tool activity, never from
    // workspace-global git state.
    private string _workspaceRoot = string.Empty;

    [ObservableProperty]
    private bool _isPanelOpen = true;

    [ObservableProperty]
    private SidePanelTab _selectedTab = SidePanelTab.Queue;

    [ObservableProperty]
    private int _queueBadge;

    [ObservableProperty]
    private int _changesBadge;

    [ObservableProperty]
    private string _queueSummaryText = string.Empty;

    [ObservableProperty]
    private string _workspaceLabel = string.Empty;

    [ObservableProperty]
    private string _changesSummary = string.Empty;

    [ObservableProperty]
    private WorkspaceFileItem? _selectedChangeItem;

    [ObservableProperty]
    private string _selectedChangeDiff = string.Empty;

    [ObservableProperty]
    private string _recentCommitsText = string.Empty;

    [ObservableProperty]
    private string _selectedLogSource = string.Empty;

    [ObservableProperty]
    private string _logContent = string.Empty;

    [ObservableProperty]
    private string _logSourceHint = string.Empty;

    [ObservableProperty]
    private string _noteEditorText = string.Empty;

    [ObservableProperty]
    private SessionNoteItem? _selectedNote;

    [ObservableProperty]
    private string _notesStatusText = string.Empty;

    [ObservableProperty]
    private string _previewMarkdown = string.Empty;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private PreviewArtifact? _selectedArtifact;

    /// <summary>Queue items are surfaced straight from the owner's collection (already wired to QueueChanged).</summary>
    public ObservableCollection<QueuedMessageViewModel> QueuedMessages => _owner.QueuedMessages;

    [ObservableProperty]
    private string _planStatusText = string.Empty;

    [ObservableProperty]
    private double _planProgressPercent;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private string _epistemicSummary = string.Empty;

    [ObservableProperty]
    private string _budgetSummary = string.Empty;

    public ObservableCollection<PlanItemVm> PlanItems { get; } = new();

    public ObservableCollection<WorkspaceFileItem> WorkspaceFiles { get; } = new();
    public ObservableCollection<WorkspaceFileItem> ChangeItems { get; } = new();
    public ObservableCollection<PreviewArtifact> PreviewArtifacts { get; } = new();
    public ObservableCollection<SessionNoteItem> Notes { get; } = new();

    /// <summary>
    /// The model's terminal usage in this chat: every run_command the model executed, rendered
    /// as a bracketed input/output transcript (command + result). NOT debug logs — this is the
    /// model's shell activity, rebuilt on the 2s tick while the tab is visible.
    /// </summary>
    public ObservableCollection<TerminalEntryItem> TerminalEntries { get; } = new();

    [ObservableProperty]
    private string _terminalStatusText = "No terminal activity in this chat yet — the model's shell commands (run_command) and their outputs will appear here.";

    /// <summary>Raised when a new HTML document should be rendered in the preview browser.</summary>
    public event Action<string>? HtmlPreviewRequested;

    public ChatSidePanelViewModel(ChatViewModel owner, MessageStore messageStore, ToolExecutor toolExecutor)
    {
        _owner = owner;
        _messageStore = messageStore;
        _toolExecutor = toolExecutor;

        NotesStatusText = "Notes are injected into the model's context on every message.";

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => Tick();
        _refreshTimer.Start();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
    }

    private void Tick()
    {
        if (_owner == null) return;

        // Queue badge + summary are cheap and useful on every tick.
        QueueBadge = _owner.QueuedMessages.Count;
        QueueSummaryText = QueueBadge == 0
            ? "Nothing queued."
            : $"{QueueBadge} pending message(s) for this chat — the model can incorporate them at an optimal point.";

        if (!IsPanelOpen) return;
        switch (SelectedTab)
        {
            case SidePanelTab.Terminal:
                RefreshTerminalFeed();
                break;
            case SidePanelTab.Plan:
                RefreshPlan();
                break;
            case SidePanelTab.Files:
            case SidePanelTab.Changes:
            case SidePanelTab.Preview:
                // Session activity accrues while the model works — keep the visible tab live.
                RefreshSessionData();
                break;
        }
    }

    partial void OnIsPanelOpenChanged(bool value)
    {
        if (value)
        {
            FireAndForget.Observe(RefreshAllAsync(), operation: nameof(RefreshAllAsync));
        }
    }

    partial void OnSelectedTabChanged(SidePanelTab value)
    {
        if (!IsPanelOpen) return;
        FireAndForget.Observe(RefreshTabAsync(value), operation: nameof(RefreshTabAsync));
    }

    private async Task RefreshTabAsync(SidePanelTab tab)
    {
        switch (tab)
        {
            case SidePanelTab.Plan:
                RefreshPlan();
                break;
            case SidePanelTab.Files:
                await RefreshSessionFilesAsync();
                break;
            case SidePanelTab.Changes:
                await RefreshChangesAsync();
                break;
            case SidePanelTab.Preview:
                await RefreshSessionArtifactsAsync();
                break;
            case SidePanelTab.Terminal:
                RefreshTerminalFeed();
                break;
            case SidePanelTab.Notes:
                await LoadNotesAsync();
                break;
        }
    }

    /// <summary>
    /// Called by the owner whenever the selected session changes (and once at startup), so
    /// notes + queue follow the active chat.
    /// </summary>
    public async Task OnSessionChangedAsync(string? sessionId)
    {
        _currentSessionId = sessionId;
        await LoadNotesAsync();
        RefreshPlan();
        if (IsPanelOpen)
        {
            RefreshSessionData();
        }
    }

    private async Task RefreshAllAsync()
    {
        QueueBadge = _owner.QueuedMessages.Count;
        RefreshPlan();
        RefreshSessionData();
        await LoadNotesAsync();
    }

    // Serializes the Files/Changes/Preview refreshes: the 2s timer tick and tab switches can
    // overlap, and un-serialized async refreshes could complete out of order (A starts, B
    // starts, B finishes, A finishes → the panel ends up showing stale B data). A tick that
    // lands while a refresh chain is still running is skipped; the chain re-reads the latest
    // state at its next invocation anyway.
    private Task _refreshChain = Task.CompletedTask;

    /// <summary>
    /// Rebuilds the Files / Changes / Preview tabs from this session's recorded tool activity
    /// only. Cheap (all in-memory except File.Exists checks) and safe to call on the UI thread.
    /// </summary>
    private void RefreshSessionData()
    {
        if (!_refreshChain.IsCompleted) return;
        _refreshChain = RefreshAllCoreAsync();
    }

    private async Task RefreshAllCoreAsync()
    {
        await RefreshSessionFilesAsync();
        await RefreshChangesAsync();
        await RefreshSessionArtifactsAsync();
    }

    /// <summary>
    /// Pulls the model's current todo list (maintained via the 'plan' tool) for the active
    /// session and mirrors it into the Plan tab. Called on tab switch, session change, and
    /// every 2s tick while the Plan tab is visible so checkmarks update live.
    /// </summary>
    public void RefreshPlan()
    {
        string sessionId = _currentSessionId ?? string.Empty;
        var entries = _toolExecutor.GetSessionPlanEntries(sessionId);
        int progress = _toolExecutor.GetSessionPlanProgress(sessionId);

        PlanItems.Clear();
        foreach (var e in entries)
        {
            PlanItems.Add(new PlanItemVm(e.Text, e.Done));
        }

        int doneCount = entries.Count(e => e.Done);
        double pct = progress >= 0 ? progress : (entries.Count > 0 ? doneCount * 100.0 / entries.Count : 0);
        PlanProgressPercent = pct;
        HasPlan = entries.Count > 0;
        PlanStatusText = entries.Count == 0
            ? "No plan yet — the model generates an execution plan for the objective."
            : $"{doneCount} of {entries.Count} complete · {pct:F0}%";

        string? taskId = _owner?.CurrentTaskId;
        if (!string.IsNullOrEmpty(taskId))
        {
            var epistemic = _owner?.AgentRuntime?.GetEpistemicLedger(taskId);
            if (epistemic != null)
            {
                var facts = epistemic.GetAllFacts();
                int verified = facts.Count(f => f.Authority >= Klydis.Core.Tasks.EpistemicAuthority.Verified);
                EpistemicSummary = $"Facts: {verified} verified · {facts.Count} total";
            }
        }
        else
        {
            EpistemicSummary = string.Empty;
        }
    }

    #region Session data (Files / Changes)

    /// <summary>
    /// Status badge for a path-bearing tool call (shown in the Files tab).
    /// </summary>
    private static string PathToolStatus(string toolName) => toolName switch
    {
        "write_file" => "Written",
        "edit_file" => "Edited",
        "str_replace" => "Edited",
        "read_file" => "Read",
        "list_directory" => "Listed",
        "search_files" => "Searched",
        _ => "Touched"
    };

    /// <summary>
    /// Extracts the file path argument from a recorded tool call's serialized args, or empty.
    /// </summary>
    private static string PathArg(string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            if (root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                return p.GetString() ?? string.Empty;
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Renders a file path relative to the workspace root when it lives underneath it,
    /// otherwise returns the absolute path.
    /// </summary>
    private string ToRelative(string fullPath)
    {
        try
        {
            string root = ResolveWorkspaceRoot();
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return fullPath;
            string full = Path.GetFullPath(fullPath);
            string r = Path.GetFullPath(root);
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(r, full);
            }
            return fullPath;
        }
        catch
        {
            return fullPath;
        }
    }

    private string ResolveWorkspaceRoot()
    {
        if (string.IsNullOrEmpty(_workspaceRoot))
        {
            try { _workspaceRoot = Environment.CurrentDirectory; } catch { _workspaceRoot = string.Empty; }
        }
        return _workspaceRoot;
    }

    /// <summary>
    /// A single-line human summary of a tool call's arguments (most informative arg wins).
    /// </summary>
    private static string SummarizeArgs(ToolActivityRecord r)
    {
        string Collapse(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        if (string.IsNullOrEmpty(r.ArgsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(r.ArgsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var name in new[] { "path", "command", "query", "url", "pattern", "fact", "item", "items", "queue_id", "action" })
            {
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    return Collapse(v.GetString() ?? string.Empty);
                }
            }
            return Collapse(r.ArgsJson);
        }
        catch
        {
            return Collapse(r.ArgsJson);
        }
    }

    [RelayCommand]
    private void RefreshSession() => RefreshSessionData();

    /// <summary>
    /// Files this chat actually worked with — derived from this session's recorded tool calls
    /// (read / write / search), deduplicated with the latest action winning. Never the
    /// workspace-global git state.
    /// </summary>
    private Task RefreshSessionFilesAsync()
    {
        WorkspaceFiles.Clear();

        string sessionId = _currentSessionId ?? string.Empty;
        var records = _toolExecutor.GetSessionToolActivity(sessionId);
        if (records.Count == 0)
        {
            WorkspaceLabel = "No file activity in this chat yet.";
            return Task.CompletedTask;
        }

        var byPath = new Dictionary<string, WorkspaceFileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            string path = PathArg(r.ArgsJson);
            if (string.IsNullOrEmpty(path)) continue;
            byPath[path] = new WorkspaceFileItem
            {
                FullPath = path,
                Name = Path.GetFileName(path),
                RelativePath = ToRelative(path),
                Status = PathToolStatus(r.ToolName),
                LastModified = r.Timestamp
            };
        }

        WorkspaceLabel = $"{byPath.Count} file(s) touched in this chat";
        foreach (var item in byPath.Values.OrderByDescending(i => i.LastModified))
        {
            WorkspaceFiles.Add(item);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Change log for this chat: files the agent actually modified, with REAL diff stats and
    /// diffs from the durable file_changes log (captured around write_file — evidence, not
    /// narration), plus a chronological timeline of the latest tool actions.
    /// </summary>
    private async Task RefreshChangesAsync()
    {
        ChangeItems.Clear();
        ChangesSummary = string.Empty;
        RecentCommitsText = string.Empty;
        SelectedChangeItem = null;
        SelectedChangeDiff = string.Empty;

        string sessionId = _currentSessionId ?? string.Empty;

        // Factual change log — durable and TASK-SCOPED: the active task's changes only, so
        // switching tasks in the same chat never leaks the previous task's diffs into the new
        // task's panel (file_changes carries task_id; the UI read path now honors it). Falls
        // back to the session-wide log only when no task is active.
        List<FileChange> changes = new();
        string? activeTaskId = _owner.CurrentTaskId;
        try
        {
            changes = activeTaskId != null
                ? await _messageStore.GetFileChangesByTaskAsync(activeTaskId)
                : await _messageStore.GetFileChangesAsync(sessionId);
        }
        catch (Exception ex)
        {
            ChangesSummary = "Change log unavailable.";
            System.Diagnostics.Debug.WriteLine($"[SidePanel] Failed to load file changes for {sessionId}: {ex.Message}");
        }

        foreach (var c in changes)
        {
            ChangeItems.Add(new WorkspaceFileItem
            {
                FullPath = c.Path,
                Name = Path.GetFileName(c.Path),
                RelativePath = ToRelative(c.Path),
                Status = c.Tool == "write_file" ? "Written" : "Edited",
                LastModified = c.TimestampUtc.ToLocalTime(),
                Additions = c.AddedLines,
                Deletions = c.DeletedLines,
                DiffText = c.Diff
            });
        }

        ChangesBadge = changes.Count;
        ChangesSummary = changes.Count == 0
            ? "No file changes recorded yet — files the agent writes (write_file) appear here with real diffs."
            : activeTaskId != null
                ? $"{changes.Count} file change(s) for task {activeTaskId}"
                : $"{changes.Count} file change(s) this chat";

        // Chronological timeline of tool actions (kept from the activity feed).
        var records = _toolExecutor.GetSessionToolActivity(sessionId);
        int ok = records.Count(r => r.Success);
        int failed = records.Count - ok;
        var lines = new List<string>();
        foreach (var r in records.TakeLast(14))
        {
            lines.Add($"{r.Timestamp:HH:mm:ss}  [{(r.Success ? "ok" : "ERR")}]  {r.ToolName}  {SummarizeArgs(r)}");
        }
        RecentCommitsText = string.Join("\n", lines);
    }

    partial void OnSelectedChangeItemChanged(WorkspaceFileItem? value)
    {
        SelectedChangeDiff = value?.DiffText ?? string.Empty;
    }

    #endregion

    #region Preview

    /// <summary>
    /// Renderable artifacts produced by THIS chat only — read from the DURABLE artifact
    /// registry (auto-registered by the mutation pipeline after every successful write), so
    /// the panel survives restarts and model switches and always shows the current revision.
    /// Falls back to deriving from recorded tool activity for legacy sessions created before
    /// the registry existed. No workspace-wide directory scans.
    /// </summary>
    private async Task RefreshSessionArtifactsAsync()
    {
        PreviewArtifacts.Clear();

        string sessionId = _currentSessionId ?? string.Empty;
        string? activeTaskId = _owner.CurrentTaskId;

        List<ArtifactRow> artifacts = new();
        try
        {
            artifacts = activeTaskId != null
                ? await _messageStore.GetArtifactsByTaskAsync(activeTaskId)
                : await _messageStore.GetArtifactsBySessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SidePanel] Failed to load artifacts: {ex.Message}");
        }

        if (artifacts.Count > 0)
        {
            foreach (var a in artifacts)
            {
                string kind = a.ArtifactType switch
                {
                    "html" => "html",
                    "md" => "md",
                    _ => "text"
                };
                try
                {
                    if (!File.Exists(a.Path)) continue;
                    var fi = new FileInfo(a.Path);
                    if (fi.Length > 3 * 1024 * 1024) continue;
                    PreviewArtifacts.Add(new PreviewArtifact
                    {
                        Name = ToRelative(a.Path),
                        FullPath = a.Path,
                        Kind = kind
                    });
                }
                catch { /* locked / missing */ }
            }
            return;
        }

        // Legacy fallback: sessions recorded before the artifact registry existed derive
        // previewable files from this session's tool activity (write_file/edit_file).
        var records = _toolExecutor.GetSessionToolActivity(sessionId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records.Where(r => r.ToolName is "write_file" or "str_replace" or "edit_file"))
        {
            string path = PathArg(r.ArgsJson);
            if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
            try
            {
                if (!File.Exists(path)) continue;
                var fi = new FileInfo(path);
                if (fi.Length > 3 * 1024 * 1024) continue;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string kind = ext switch
                {
                    ".html" or ".htm" => "html",
                    ".md" or ".markdown" => "md",
                    ".txt" or ".json" or ".xml" or ".log" or ".cs" or ".ts" or ".py" or ".js" or ".css" or ".xaml" or ".sql" or ".ps1" or ".bat" or ".yml" or ".yaml" or ".toml" => "text",
                    _ => ""
                };
                if (kind.Length == 0) continue;
                PreviewArtifacts.Add(new PreviewArtifact
                {
                    Name = ToRelative(path),
                    FullPath = path,
                    Kind = kind
                });
            }
            catch { /* locked / missing */ }
        }
    }

    partial void OnSelectedArtifactChanged(PreviewArtifact? value)
    {
        if (value == null) return;
        try
        {
            string content = File.ReadAllText(value.FullPath);
            switch (value.Kind)
            {
                case "html":
                    PreviewMarkdown = string.Empty;
                    PreviewText = string.Empty;
                    HtmlPreviewRequested?.Invoke(content);
                    break;
                case "md":
                    PreviewText = string.Empty;
                    PreviewMarkdown = content;
                    break;
                default:
                    PreviewMarkdown = string.Empty;
                    PreviewText = content;
                    break;
            }
        }
        catch (Exception ex)
        {
            PreviewMarkdown = string.Empty;
            PreviewText = $"Unable to read {value.FullPath}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshPreviews() => FireAndForget.Observe(RefreshSessionArtifactsAsync(), operation: nameof(RefreshSessionArtifactsAsync));

    [RelayCommand]
    private void OpenArtifactInBrowser()
    {
        var artifact = SelectedArtifact;
        if (artifact == null || !File.Exists(artifact.FullPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(artifact.FullPath) { UseShellExecute = true });
        }
        catch { /* shell open unavailable */ }
    }

    #endregion

    #region Terminal feed

    /// <summary>
    /// The model's terminal usage in the current session: run_command inputs (command) and
    /// outputs (result), rendered as a bracketed transcript. Derived from this session's
    /// recorded tool activity — never from debug logs. Called on tab switch and on the 2s
    /// tick while the tab is visible, so commands stream in live as the model works.
    /// </summary>
    private void RefreshTerminalFeed()
    {
        TerminalEntries.Clear();

        string sessionId = _currentSessionId ?? string.Empty;
        var records = _toolExecutor.GetSessionToolActivity(sessionId)
            .Where(r => r.ToolName == "run_command")
            .TakeLast(100);

        foreach (var r in records)
        {
            TerminalEntries.Add(new TerminalEntryItem(
                ExtractCommand(r.ArgsJson),
                string.IsNullOrWhiteSpace(r.OutputPreview) ? "(no output)" : r.OutputPreview,
                r.Success,
                r.Timestamp));
        }

        TerminalStatusText = TerminalEntries.Count == 0
            ? "No terminal activity in this chat yet — the model's shell commands (run_command) and their outputs will appear here."
            : $"{TerminalEntries.Count} shell command(s) run by the model in this chat · inputs and outputs below";
    }

    /// <summary>
    /// Pulls the command the model asked the shell to run out of the recorded arguments.
    /// </summary>
    private static string ExtractCommand(string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            if (root.TryGetProperty("command", out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString() ?? string.Empty;
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    #endregion

    #region Notes

    private async Task LoadNotesAsync()
    {
        Notes.Clear();
        if (string.IsNullOrEmpty(_currentSessionId)) return;
        try
        {
            var records = await _messageStore.GetNotesAsync(_currentSessionId);
            foreach (var r in records)
            {
                Notes.Add(new SessionNoteItem
                {
                    Id = r.Id,
                    Content = r.Content,
                    UpdatedAt = r.UpdatedAt
                });
            }
            if (Notes.Count == 0)
            {
                NotesStatusText = "No notes yet — add one below. Notes are injected into the model's context on every message.";
            }
            else
            {
                NotesStatusText = $"{Notes.Count} note(s) — injected into the model's context on every message.";
            }
        }
        catch (Exception ex)
        {
            NotesStatusText = $"Failed to load notes: {ex.Message}";
        }
    }

    partial void OnSelectedNoteChanged(SessionNoteItem? value)
    {
        if (value != null)
        {
            _editingNoteId = value.Id;
            NoteEditorText = value.Content;
        }
    }

    [RelayCommand]
    private void NewNote()
    {
        SelectedNote = null;
        _editingNoteId = null;
        NoteEditorText = string.Empty;
        NotesStatusText = "Writing a new note — it will be injected into the model's context once saved.";
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (string.IsNullOrEmpty(_currentSessionId)) return;
        string text = NoteEditorText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            await _messageStore.SaveNoteAsync(_currentSessionId, _editingNoteId, text);
            _editingNoteId = null;
            NoteEditorText = string.Empty;
            NotesStatusText = "Note saved — the model will read it on its next message.";
            await LoadNotesAsync();
        }
        catch (Exception ex)
        {
            NotesStatusText = $"Failed to save note: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (string.IsNullOrEmpty(_currentSessionId) || SelectedNote == null) return;
        try
        {
            await _messageStore.DeleteNoteAsync(_currentSessionId, SelectedNote.Id);
            SelectedNote = null;
            _editingNoteId = null;
            NoteEditorText = string.Empty;
            await LoadNotesAsync();
        }
        catch (Exception ex)
        {
            NotesStatusText = $"Failed to delete note: {ex.Message}";
        }
    }

    #endregion
}

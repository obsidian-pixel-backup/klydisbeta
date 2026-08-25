using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tracing;

/// <summary>
/// A single row in the chronological execution timeline — the authoritative, event-backed
/// record of what the agent actually did. Every field is projected from a canonical
/// <see cref="ExecutionEvent"/>; the UI never infers activity from text.
/// </summary>
public sealed record ExecutionTimelineItem(
    long Sequence,
    DateTime Timestamp,
    ExecutionEventCategory Category,
    string? ToolName,
    string Title,
    bool Success,
    long DurationMs,
    string? Summary,
    string? FilePath,
    string? Command,
    int? ExitCode,
    string? TaskId);

/// <summary>
/// A completed terminal action group: one TerminalStarted paired with its TerminalCompleted
/// (and any TerminalOutput between them). This is the "one terminal history" shape the
/// right panel renders — a command, its exit status, duration, and output — derived from the
/// canonical event stream rather than a second ad-hoc terminal store.
/// </summary>
public sealed record TerminalActionGroup(
    long StartSequence,
    DateTime Timestamp,
    string Title,
    string? Command,
    bool Success,
    long DurationMs,
    int? ExitCode,
    string? TaskId,
    IReadOnlyList<string> OutputLines);

/// <summary>
/// Per-domain projections derived from the canonical event stream. Each list is the
/// projection the corresponding right-panel tab renders; every entry is backed by an event,
/// so a claim like "Files changed: N" can never outrun the events that produced it.
/// </summary>
public sealed record ExecutionProjections(
    IReadOnlyList<ExecutionTimelineItem> Timeline,
    IReadOnlyList<TerminalActionGroup> TerminalGroups,
    IReadOnlyList<ExecutionTimelineItem> TerminalEvents,
    IReadOnlyList<ExecutionTimelineItem> FileEvents,
    IReadOnlyList<ExecutionTimelineItem> PlanEvents,
    IReadOnlyList<ExecutionTimelineItem> TodoEvents,
    IReadOnlyList<ExecutionTimelineItem> ArtifactEvents);

/// <summary>
/// Pure projection of the canonical <see cref="ExecutionEvent"/> stream into the views the
/// right panel needs (report §41/§58/§66). No I/O, no state, no side effects — given the
/// same event sequence it always produces the same projections, so it is trivially testable
/// and safe to run on any thread.
///
/// The left side of the pipeline is the runtime emitting events into
/// <see cref="IExecutionEventStore"/>; this projector is the "event aggregator" that turns
/// that journal into the timeline and per-domain lists. The UI subscribes to the store and
/// re-projects (or the store broadcasts each event and the projector appends incrementally).
/// </summary>
public static class ExecutionTimelineProjector
{
    /// <summary>
    /// Projects a canonical event sequence into the full set of right-panel views.
    /// Events are ordered by sequence number (monotonic total order) regardless of arrival
    /// order, so a replayed or interleaved feed still yields a stable timeline.
    /// </summary>
    public static ExecutionProjections Project(IEnumerable<ExecutionEvent> events)
    {
        var ordered = (events ?? Array.Empty<ExecutionEvent>())
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        var timeline = new List<ExecutionTimelineItem>(ordered.Count);
        foreach (var evt in ordered)
        {
            timeline.Add(ToTimelineItem(evt));
        }

        return new ExecutionProjections(
            Timeline: timeline,
            TerminalGroups: BuildTerminalGroups(ordered),
            TerminalEvents: Filter(ordered, IsTerminalEvent),
            FileEvents: Filter(ordered, IsFileEvent),
            PlanEvents: Filter(ordered, IsPlanEvent),
            TodoEvents: Filter(ordered, IsTodoEvent),
            ArtifactEvents: Filter(ordered, IsArtifactEvent));
    }

    /// <summary>
    /// Groups the raw terminal event stream into completed <see cref="TerminalActionGroup"/>s
    /// by pairing each TerminalStarted with its TerminalCompleted (matched by ActionId when
    /// present, else by order), collecting the TerminalOutput lines in between. A
    /// TerminalStarted with no matching completion is still surfaced (Success=false,
    /// DurationMs=0) so a hung/failed command is never silently dropped from the panel.
    /// </summary>
    public static IReadOnlyList<TerminalActionGroup> BuildTerminalGroups(IReadOnlyList<ExecutionEvent> events)
    {
        var groups = new List<TerminalActionGroup>();
        if (events == null || events.Count == 0) return groups;

        var starts = events
            .Where(e => e.Category == ExecutionEventCategory.TerminalStarted)
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        foreach (var start in starts)
        {
            var output = new List<string>();
            TerminalActionGroup? group = null;

            // Find the completion for this start: prefer the same ActionId; otherwise the
            // next TerminalCompleted after the start's sequence that is not already consumed.
            var completion = events
                .Where(e => e.Category == ExecutionEventCategory.TerminalCompleted &&
                            e.SequenceNumber > start.SequenceNumber &&
                            (string.IsNullOrEmpty(start.ActionId) || e.ActionId == start.ActionId))
                .OrderBy(e => e.SequenceNumber)
                .FirstOrDefault();

            // Collect output lines between the start and its completion (or the whole tail
            // when the command never completed).
            foreach (var e in events.Where(ev => ev.SequenceNumber > start.SequenceNumber &&
                                                 (completion == null || ev.SequenceNumber < completion.SequenceNumber)))
            {
                if (e.Category == ExecutionEventCategory.TerminalOutput && !string.IsNullOrWhiteSpace(e.Details))
                {
                    output.Add(e.Details);
                }
            }

            group = new TerminalActionGroup(
                StartSequence: start.SequenceNumber,
                Timestamp: start.Timestamp,
                Title: string.IsNullOrWhiteSpace(start.Title) ? "Terminal command" : start.Title,
                Command: start.Command,
                Success: completion?.Success ?? false,
                DurationMs: completion?.DurationMs ?? 0,
                ExitCode: completion?.ExitCode,
                TaskId: start.TaskId,
                OutputLines: output);

            groups.Add(group);
        }

        return groups;
    }

    private static ExecutionTimelineItem ToTimelineItem(ExecutionEvent e)
        => new(
            Sequence: e.SequenceNumber,
            Timestamp: e.Timestamp,
            Category: e.Category,
            ToolName: e.ToolName,
            Title: string.IsNullOrWhiteSpace(e.Title) ? ExecutionEvent.GenerateSemanticTitle(e.ToolName ?? string.Empty) : e.Title,
            Success: e.Success,
            DurationMs: e.DurationMs,
            Summary: e.Summary,
            FilePath: e.FilePath,
            Command: e.Command,
            ExitCode: e.ExitCode,
            TaskId: e.TaskId);

    private static IReadOnlyList<ExecutionTimelineItem> Filter(
        IEnumerable<ExecutionEvent> events,
        Func<ExecutionEventCategory, bool> predicate)
        => events.Where(e => predicate(e.Category)).Select(ToTimelineItem).ToList();

    /// <summary>True for terminal lifecycle events.</summary>
    public static bool IsTerminalEvent(ExecutionEventCategory c)
        => c is ExecutionEventCategory.TerminalStarted
            or ExecutionEventCategory.TerminalOutput
            or ExecutionEventCategory.TerminalCompleted;

    /// <summary>True for filesystem mutation/read events.</summary>
    public static bool IsFileEvent(ExecutionEventCategory c)
        => c is ExecutionEventCategory.FileRead
            or ExecutionEventCategory.FileCreated
            or ExecutionEventCategory.FileModified
            or ExecutionEventCategory.FileDeleted
            or ExecutionEventCategory.FileWritten
            or ExecutionEventCategory.FileEdited;

    /// <summary>True for plan lifecycle events.</summary>
    public static bool IsPlanEvent(ExecutionEventCategory c)
        => c is ExecutionEventCategory.PlanCreated
            or ExecutionEventCategory.PlanUpdated
            or ExecutionEventCategory.PlanRevised;

    /// <summary>True for TODO lifecycle events.</summary>
    public static bool IsTodoEvent(ExecutionEventCategory c)
        => c is ExecutionEventCategory.TodoCreated
            or ExecutionEventCategory.TodoUpdated;

    /// <summary>True for artifact/diff events.</summary>
    public static bool IsArtifactEvent(ExecutionEventCategory c)
        => c is ExecutionEventCategory.ArtifactCreated
            or ExecutionEventCategory.PreviewUpdated
            or ExecutionEventCategory.DiffCreated;
}

using System;
using System.Collections.Generic;

namespace Klydis.Core.Tasks;

/// <summary>
/// Kinds of factual state change a turn can produce (P1.8). The supervisor's notion of
/// \"progress\" is derived from these — never from how much text the model generated.
/// </summary>
public enum StateDeltaKind
{
    /// <summary>A tool was executed (Detail = tool name).</summary>
    ToolExecuted,

    /// <summary>A tool execution succeeded (Detail = tool name).</summary>
    ToolSucceeded,

    /// <summary>The plan checklist changed (Detail = summary of the diff).</summary>
    PlanChanged,

    /// <summary>A plan item was checked off (Detail = step title).</summary>
    StepCompleted,

    /// <summary>A file was written/modified (Detail = path).</summary>
    FileChanged,

    /// <summary>A queued message was incorporated (Detail = queue id).</summary>
    QueueConsumed,

    /// <summary>Verification evidence was recorded (Detail = evidence description).</summary>
    EvidenceAdded
}

/// <summary>A single factual change entry.</summary>
public readonly record struct StateDeltaEntry(StateDeltaKind Kind, string Detail, DateTime TimestampUtc);

/// <summary>
/// The factual state change produced by a generation/turn: tool executions, plan/step
/// changes, file changes, queue consumption, evidence. Empty when the model produced text
/// but nothing actually changed — the supervisor treats that as no progress regardless of
/// output length.
/// </summary>
public sealed record StateDelta(IReadOnlyList<StateDeltaEntry> Entries)
{
    public static readonly StateDelta Empty = new(Array.Empty<StateDeltaEntry>());

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>True when ANY entry of the kind exists. (P1.8-Fix-5: this is the contains
    /// semantics — a turn of ToolExecuted/ToolSucceeded/FileChanged must report
    /// Has(ToolExecuted) == true even though ToolExecuted is not the LAST entry.)</summary>
    public bool Has(StateDeltaKind kind) => Contains(kind);

    /// <summary>True when any entry of the kind exists.</summary>
    public bool Contains(StateDeltaKind kind)
    {
        foreach (var e in Entries)
        {
            if (e.Kind == kind) return true;
        }
        return false;
    }

    public override string ToString()
        => Entries.Count == 0 ? "(no state change)" : string.Join("; ", Entries);
}

/// <summary>
/// Accumulates factual change during a turn so the supervisor can answer \"did anything
/// actually happen?\" from durable observations (tool executions, plan diffs) rather than
/// from the model's narrative. Captured by ChatEngine at the points the facts are known.
/// </summary>
public sealed class TurnStateCollector
{
    private readonly List<StateDeltaEntry> _entries = new();
    private readonly object _lock = new();

    public void RecordTool(string toolName, bool success)
    {
        lock (_lock)
        {
            _entries.Add(new StateDeltaEntry(StateDeltaKind.ToolExecuted, toolName, DateTime.UtcNow));
            if (success)
            {
                _entries.Add(new StateDeltaEntry(StateDeltaKind.ToolSucceeded, toolName, DateTime.UtcNow));
            }
        }
    }

    public void RecordPlanChange(string detail)
    {
        lock (_lock)
        {
            _entries.Add(new StateDeltaEntry(StateDeltaKind.PlanChanged, detail, DateTime.UtcNow));
        }
    }

    public void RecordStepCompleted(string stepTitle)
    {
        lock (_lock)
        {
            _entries.Add(new StateDeltaEntry(StateDeltaKind.StepCompleted, stepTitle, DateTime.UtcNow));
        }
    }

    public void RecordFileChanged(string path)
    {
        lock (_lock)
        {
            _entries.Add(new StateDeltaEntry(StateDeltaKind.FileChanged, path, DateTime.UtcNow));
        }
    }

    public void RecordEvidence(string description)
    {
        lock (_lock)
        {
            _entries.Add(new StateDeltaEntry(StateDeltaKind.EvidenceAdded, description, DateTime.UtcNow));
        }
    }

    public StateDelta Build()
    {
        lock (_lock)
        {
            return _entries.Count == 0 ? StateDelta.Empty : new StateDelta(_entries.ToArray());
        }
    }
}

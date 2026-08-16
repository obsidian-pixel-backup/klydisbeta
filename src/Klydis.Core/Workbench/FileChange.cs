using System;

namespace Klydis.Core.Workbench;

/// <summary>
/// A factual file modification captured by the runtime around a file-mutating tool call
/// (workbench spec §7–§8). The before/after hashes and the diff come from the filesystem —
/// NOT from model-generated descriptions — so the Changes tab is evidence, not narration.
/// Task-scoped: a change always belongs to the task whose run executed the tool.
/// </summary>
public sealed record FileChange(
    string ChangeId,
    string SessionId,
    string? TaskId,
    string Path,
    string Tool,
    string BeforeHash,
    string AfterHash,
    string Diff,
    int AddedLines,
    int DeletedLines,
    DateTime TimestampUtc);

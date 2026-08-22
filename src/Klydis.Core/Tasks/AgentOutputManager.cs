using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Klydis.Core.Tasks;

/// <summary>
/// Output record classifications.
/// Distinguishes intermediate status and tool output from finalized deliverables.
/// </summary>
public enum OutputRecordKind
{
    Draft,
    Intermediate,
    ToolOutput,
    Artifact,
    Final
}

/// <summary>
/// A tracked output record with provenance and evidence lineage.
/// </summary>
public sealed record OutputRecord(
    string OutputId,
    string GoalId,
    string? TurnId,
    OutputRecordKind Kind,
    string Content,
    string? MimeType = null,
    string? ArtifactLocation = null,
    long? SizeBytes = null,
    IReadOnlyList<string>? EvidenceIds = null,
    DateTimeOffset Timestamp = default)
{
    public DateTimeOffset Timestamp { get; init; } = Timestamp == default ? DateTimeOffset.UtcNow : Timestamp;
}

/// <summary>
/// Interface for managing agent output lifecycle and ensuring intermediate updates never overwrite final deliverables.
/// </summary>
public interface IAgentOutputManager
{
    Task<OutputRecord> CreateDraftAsync(string goalId, string turnId, string content);
    Task<OutputRecord> PublishIntermediateAsync(string goalId, string turnId, string content);
    Task<OutputRecord> AttachArtifactAsync(string goalId, string turnId, string artifactPath, string summary, string mimeType);
    Task<OutputRecord> FinalizeAsync(string goalId, string summary, IReadOnlyList<string>? evidenceIds = null);
    IReadOnlyList<OutputRecord> GetTimeline(string goalId);
}

/// <summary>
/// In-memory and event-driven output manager maintaining output provenance.
/// </summary>
public class AgentOutputManager : IAgentOutputManager
{
    private readonly ConcurrentDictionary<string, List<OutputRecord>> _timelineByGoal = new();
    private readonly object _lock = new();

    public Task<OutputRecord> CreateDraftAsync(string goalId, string turnId, string content)
    {
        var record = new OutputRecord(
            OutputId: "out_" + Guid.NewGuid().ToString("N")[..8],
            GoalId: goalId,
            TurnId: turnId,
            Kind: OutputRecordKind.Draft,
            Content: content);
        Append(goalId, record);
        return Task.FromResult(record);
    }

    public Task<OutputRecord> PublishIntermediateAsync(string goalId, string turnId, string content)
    {
        var record = new OutputRecord(
            OutputId: "out_" + Guid.NewGuid().ToString("N")[..8],
            GoalId: goalId,
            TurnId: turnId,
            Kind: OutputRecordKind.Intermediate,
            Content: content);
        Append(goalId, record);
        return Task.FromResult(record);
    }

    public Task<OutputRecord> AttachArtifactAsync(string goalId, string turnId, string artifactPath, string summary, string mimeType)
    {
        var record = new OutputRecord(
            OutputId: "art_" + Guid.NewGuid().ToString("N")[..8],
            GoalId: goalId,
            TurnId: turnId,
            Kind: OutputRecordKind.Artifact,
            Content: summary,
            MimeType: mimeType,
            ArtifactLocation: artifactPath);
        Append(goalId, record);
        return Task.FromResult(record);
    }

    public Task<OutputRecord> FinalizeAsync(string goalId, string summary, IReadOnlyList<string>? evidenceIds = null)
    {
        var record = new OutputRecord(
            OutputId: "fin_" + Guid.NewGuid().ToString("N")[..8],
            GoalId: goalId,
            TurnId: null,
            Kind: OutputRecordKind.Final,
            Content: summary,
            EvidenceIds: evidenceIds);
        Append(goalId, record);
        return Task.FromResult(record);
    }

    public IReadOnlyList<OutputRecord> GetTimeline(string goalId)
    {
        if (_timelineByGoal.TryGetValue(goalId, out var list))
        {
            lock (list)
            {
                return list.ToList();
            }
        }
        return Array.Empty<OutputRecord>();
    }

    private void Append(string goalId, OutputRecord record)
    {
        var list = _timelineByGoal.GetOrAdd(goalId, _ => new List<OutputRecord>());
        lock (list)
        {
            list.Add(record);
        }
    }
}

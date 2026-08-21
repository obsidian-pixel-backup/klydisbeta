using System;

namespace Klydis.Core.Inference.Telemetry;

/// <summary>
/// Detailed telemetry captured for a skill resolution and capability activation event.
/// </summary>
public sealed record SkillSelectionTelemetry(
    string RequestId,
    string? GoalId,
    string Query,
    int TotalSkills,
    int RetrievedSkills,
    int CandidateSkills,
    int ActivatedSkills,
    int ExposedTools,
    string? SelectedSkill,
    double SelectionScore,
    string SelectionMethod,
    bool CapabilityAvailable,
    bool CapabilityExposed,
    bool ToolCalled,
    bool ExecutionSucceeded,
    bool VerificationSucceeded,
    bool IsStarvation = false,
    bool IsOverload = false
);

using System;
using System.Collections.Generic;

namespace Klydis.Core.Skills;

/// <summary>
/// Machine-readable manifest defining a skill's routing triggers, capabilities, entities, and constraints.
/// </summary>
public sealed record SkillManifest
{
    public string SkillId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = "General";
    public IReadOnlyList<string> Provides { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActivateWhen { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DoNotActivateWhen { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Entities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedEnvironments { get; init; } = new[] { "windows" };
    public IReadOnlyList<string> Verification { get; init; } = Array.Empty<string>();
    public string FullBodyPath { get; init; } = string.Empty;
    public string PromptInstruction { get; init; } = string.Empty;
}

/// <summary>
/// An indexed skill record ready for hybrid retrieval and capability-aware scoring.
/// </summary>
public sealed class SkillIndexRecord
{
    public required SkillManifest Manifest { get; init; }
    public float[]? Embedding { get; set; }
    public string SearchableDocument { get; init; } = string.Empty;
}

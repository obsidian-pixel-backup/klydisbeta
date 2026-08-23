namespace Klydis.Core.Orchestration;

/// <summary>
/// A model's empirical capability profile (agent-intelligence stage §17): six capability
/// dimensions on a 0..1 scale, estimated from real execution telemetry and smoothed toward a
/// prior so low-sample models stay conservative. This is what the <see cref="ModelRouter"/>
/// scores candidates against — a router that picks on metadata alone cannot know that model A
/// completes steps in one action while model B needs three repairs.
/// </summary>
public sealed record ModelCapabilityProfile(
    string ModelId,
    string ProtocolKey,
    int SampleCount,

    /// <summary>Quality of first-attempt planning/reasoning (0..1).</summary>
    double Reasoning,

    /// <summary>Quality of file/code production (0..1).</summary>
    double Coding,

    /// <summary>Reliability of executing tools/commands (0..1).</summary>
    double ToolReliability,

    /// <summary>Ability to recover from a failed action/parse (0..1).</summary>
    double RepairAbility,

    /// <summary>Ability to follow the step's instruction precisely (0..1).</summary>
    double InstructionFollowing,

    /// <summary>Reliability of performing/driving verification (0..1).</summary>
    double VerificationReliability,

    /// <summary>Reliability of finishing runs cleanly (0..1).</summary>
    double CompletionReliability,

    /// <summary>Confidence that this model reliably speaks the claimed protocol (0..1).</summary>
    double ProtocolConfidence)
{
    /// <summary>The dominant strength — used for diagnostics and profile summaries.</summary>
    public string StrongestDimension
        => (Reasoning, Coding, ToolReliability, RepairAbility, InstructionFollowing,
            VerificationReliability, CompletionReliability) switch
        {
            var s when s.Reasoning >= s.Coding && s.Reasoning >= s.ToolReliability => "reasoning",
            var s when s.Coding >= s.ToolReliability => "coding",
            _ => "tool-execution"
        };

    /// <summary>Recommended tool projection surface size based on empirical tool reliability.</summary>
    public int RecommendedToolSurfaceSize => ToolReliability switch
    {
        >= 0.80 => 10,
        >= 0.60 => 6,
        _ => 4
    };

    /// <summary>Maximum action batch size per generation.</summary>
    public int MaxActionsPerGeneration => ToolReliability switch
    {
        >= 0.85 => 3,
        >= 0.65 => 2,
        _ => 1
    };

    /// <summary>Whether this model requires immediate verification after every action.</summary>
    public bool RequiresFrequentVerification => VerificationReliability < 0.75;

    /// <summary>Whether deterministic recovery should be executed aggressively before asking the model.</summary>
    public bool AggressiveDeterministicRecovery => RepairAbility < 0.65;

    /// <summary>Short diagnostic line, e.g. "qwen3.6-14b | n=42 | reasoning .82 coding .71 tools .88".</summary>
    public override string ToString()
        => $"{ModelId} | n={SampleCount} | reasoning {Reasoning:0.00} coding {Coding:0.00} " +
           $"tools {ToolReliability:0.00} repair {RepairAbility:0.00} " +
           $"verify {VerificationReliability:0.00} complete {CompletionReliability:0.00}";
}

/// <summary>
/// The starting belief about a model before (or in the absence of) execution evidence.
/// Derived from a <see cref="Klydis.Core.Protocol.ModelProfile"/> when available, so the
/// profile's template/family knowledge seeds the prior; defaults are deliberately
/// conservative (agent-intelligence §2: memory/provenance must not invent confidence).
/// </summary>
public sealed record ModelCapabilityPrior(
    double Reasoning = 0.5,
    double Coding = 0.5,
    double ToolReliability = 0.45,
    double RepairAbility = 0.45,
    double InstructionFollowing = 0.5,
    double VerificationReliability = 0.45,
    double CompletionReliability = 0.5,
    double ProtocolConfidence = 0.4)
{
    /// <summary>The default prior for a model with no profile knowledge at all.</summary>
    public static ModelCapabilityPrior Default { get; } = new();

    /// <summary>Maps a profile capability level to a 0..1 prior score.</summary>
    public static double FromLevel(Klydis.Core.Protocol.CapabilityLevel level)
        => level switch
        {
            Klydis.Core.Protocol.CapabilityLevel.Reliable => 0.9,
            Klydis.Core.Protocol.CapabilityLevel.Usable => 0.7,
            Klydis.Core.Protocol.CapabilityLevel.Experimental => 0.45,
            _ => 0.15
        };
}

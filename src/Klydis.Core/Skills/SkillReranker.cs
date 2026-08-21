using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Capabilities;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Skills;

/// <summary>
/// Scored skill candidate after capability-aware multi-factor reranking.
/// </summary>
public sealed record ScoredSkillCandidate(
    SkillIndexRecord Record,
    double FinalScore,
    double SemanticScore,
    double CapabilityScore,
    double IntentScore,
    double EntityScore,
    double KeywordScore,
    double DependencyScore,
    double EnvironmentScore,
    double Penalty,
    string Explanation);

/// <summary>
/// Capability-aware deterministic reranker that scores candidate skills against intent,
/// capabilities, entities, dependencies, and environment constraints.
/// </summary>
public class SkillReranker
{
    private readonly ICapabilityRegistry? _capabilityRegistry;
    private readonly ILogger<SkillReranker>? _logger;

    public SkillReranker(ICapabilityRegistry? capabilityRegistry = null, ILogger<SkillReranker>? logger = null)
    {
        _capabilityRegistry = capabilityRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Reranks retrieved skill candidate records using multi-signal scoring.
    /// </summary>
    public IReadOnlyList<ScoredSkillCandidate> Rerank(
        string userPrompt,
        IReadOnlyList<SkillIndexRecord> candidates,
        string targetEnvironment = "windows",
        string? activeContinuitySkillId = null)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return Array.Empty<ScoredSkillCandidate>();
        }

        string normalizedPrompt = userPrompt.ToLowerInvariant();
        var promptTokens = normalizedPrompt.Split(new[] { ' ', '.', ',', '!', '?', '-', '_', ':', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var promptTokenSet = new HashSet<string>(promptTokens, StringComparer.OrdinalIgnoreCase);

        var scored = new List<ScoredSkillCandidate>();

        foreach (var cand in candidates)
        {
            var manifest = cand.Manifest;

            // 1. Hard Constraints: Environment
            if (manifest.SupportedEnvironments.Count > 0 &&
                !manifest.SupportedEnvironments.Any(env => string.Equals(env, targetEnvironment, StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogDebug("Rejecting skill {SkillId} due to environment mismatch ({TargetEnv})", manifest.SkillId, targetEnvironment);
                continue;
            }

            // 2. Hard Constraints: Negative activation triggers
            double penalty = 0.0;
            foreach (var neg in manifest.DoNotActivateWhen)
            {
                if (normalizedPrompt.Contains(neg.ToLowerInvariant()))
                {
                    penalty += 0.50;
                }
            }

            // 3. Intent Score (ActivateWhen match)
            double intentScore = 0.0;
            if (manifest.ActivateWhen.Count > 0)
            {
                int matchedTriggers = 0;
                foreach (var trigger in manifest.ActivateWhen)
                {
                    string triggerLower = trigger.ToLowerInvariant();
                    if (normalizedPrompt.Contains(triggerLower) ||
                        triggerLower.Split(' ').All(t => promptTokenSet.Contains(t)))
                    {
                        matchedTriggers++;
                    }
                }
                intentScore = Math.Min(1.0, (double)matchedTriggers / Math.Max(1, manifest.ActivateWhen.Count * 0.5));
            }

            // 4. Capability Match Score
            double capScore = 0.0;
            if (manifest.Provides.Count > 0)
            {
                int matchedCaps = 0;
                foreach (var cap in manifest.Provides)
                {
                    string capLower = cap.ToLowerInvariant();
                    var capParts = capLower.Split('.');
                    if (normalizedPrompt.Contains(capLower) || capParts.Any(p => p.Length > 2 && promptTokenSet.Contains(p)))
                    {
                        matchedCaps++;
                    }
                }
                capScore = Math.Min(1.0, (double)matchedCaps / Math.Max(1, manifest.Provides.Count));
            }

            // 5. Entity Match Score
            double entityScore = 0.0;
            if (manifest.Entities.Count > 0)
            {
                int matchedEntities = manifest.Entities.Count(e => promptTokenSet.Contains(e.ToLowerInvariant()) || normalizedPrompt.Contains(e.ToLowerInvariant()));
                entityScore = Math.Min(1.0, (double)matchedEntities / Math.Max(1, manifest.Entities.Count * 0.5));
            }

            // 6. Keyword / Tag Overlap
            double keywordScore = 0.0;
            if (manifest.Keywords.Count > 0)
            {
                int matchedKeywords = manifest.Keywords.Count(k => promptTokenSet.Contains(k.ToLowerInvariant()) || normalizedPrompt.Contains(k.ToLowerInvariant()));
                keywordScore = Math.Min(1.0, (double)matchedKeywords / Math.Max(1, manifest.Keywords.Count * 0.5));
            }

            // 7. Dependency Satisfaction
            double depScore = 1.0;
            if (manifest.Dependencies.Count > 0 && _capabilityRegistry != null)
            {
                int satisfied = manifest.Dependencies.Count(dep => _capabilityRegistry.Contains(dep));
                depScore = (double)satisfied / manifest.Dependencies.Count;
            }

            // 8. Continuity Score
            double continuityScore = 0.0;
            if (!string.IsNullOrEmpty(activeContinuitySkillId) &&
                string.Equals(manifest.SkillId, activeContinuitySkillId, StringComparison.OrdinalIgnoreCase))
            {
                continuityScore = 1.0;
            }

            // 9. Semantic Score (Baseline estimate or normalized)
            double semanticScore = (cand.Embedding != null) ? 0.80 : 0.50;

            // Compute Final Weighted Score:
            // FinalScore = 0.25 semantic + 0.20 capability + 0.15 intent + 0.10 entity + 0.10 keyword + 0.10 dependency + 0.05 continuity + 0.05 environment - penalty
            double environmentScore = 1.0;
            double finalScore = (0.25 * semanticScore) +
                                (0.20 * capScore) +
                                (0.15 * intentScore) +
                                (0.10 * entityScore) +
                                (0.10 * keywordScore) +
                                (0.10 * depScore) +
                                (0.05 * continuityScore) +
                                (0.05 * environmentScore) - penalty;

            finalScore = Math.Clamp(finalScore, 0.0, 1.0);

            string explanation = $"Score: {finalScore:F2} (Sem:{semanticScore:F2}, Cap:{capScore:F2}, Intent:{intentScore:F2}, Ent:{entityScore:F2}, Key:{keywordScore:F2})";

            scored.Add(new ScoredSkillCandidate(
                Record: cand,
                FinalScore: finalScore,
                SemanticScore: semanticScore,
                CapabilityScore: capScore,
                IntentScore: intentScore,
                EntityScore: entityScore,
                KeywordScore: keywordScore,
                DependencyScore: depScore,
                EnvironmentScore: environmentScore,
                Penalty: penalty,
                Explanation: explanation));
        }

        return scored.OrderByDescending(s => s.FinalScore).ToList();
    }
}

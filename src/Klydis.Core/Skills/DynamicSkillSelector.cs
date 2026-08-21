using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Skills;

public class DynamicSkillSelector : ISkillRouter
{
    private readonly SkillLibraryManager _libraryManager;
    private readonly SkillIndex? _skillIndex;
    private readonly SkillReranker? _reranker;
    private readonly ILogger<DynamicSkillSelector>? _logger;
    private bool _isIndexed;

    public DynamicSkillSelector(
        SkillLibraryManager libraryManager,
        SkillIndex? skillIndex = null,
        SkillReranker? reranker = null,
        ILogger<DynamicSkillSelector>? logger = null)
    {
        _libraryManager = libraryManager;
        _skillIndex = skillIndex;
        _reranker = reranker;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Skill>> ResolveSkillsAsync(
        TaskStep step,
        string? promptContext = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var enabledSkills = _libraryManager.GetEnabledSkills();
        if (enabledSkills.Count == 0 || step == null)
        {
            return Task.FromResult<IReadOnlyList<Skill>>(Array.Empty<Skill>());
        }

        var matched = new List<Skill>();

        // 1. Explicit RequiredSkills on the step take highest precedence
        if (step.RequiredSkills != null && step.RequiredSkills.Count > 0)
        {
            foreach (var req in step.RequiredSkills)
            {
                var found = enabledSkills.FirstOrDefault(s =>
                    string.Equals(s.Id, req, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Name, req, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Category, req, StringComparison.OrdinalIgnoreCase) ||
                    s.Id.Contains(req, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains(req, StringComparison.OrdinalIgnoreCase) ||
                    s.Tags.Any(t => string.Equals(t, req, StringComparison.OrdinalIgnoreCase)));

                if (found != null && !matched.Contains(found))
                {
                    matched.Add(found);
                }
            }
        }

        // 2. If prompt context or step title is provided and we haven't hit quota, evaluate relevance
        if (matched.Count < 3)
        {
            string contextText = $"{step.Title} {promptContext}".Trim();
            if (!string.IsNullOrWhiteSpace(contextText))
            {
                var scored = ReasonAndSelectSkills(contextText, maxSkillsToSelect: 3);
                foreach (var sk in scored.SelectedSkills)
                {
                    if (!matched.Contains(sk) && matched.Count < 3)
                    {
                        matched.Add(sk);
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<Skill>>(matched);
    }

    public string GenerateBrainIndex()
    {
        var skills = _libraryManager.GetEnabledSkills();
        if (skills.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<system_skill_brain_index>");
        sb.AppendLine("You have a Skills Library Brain containing specialized domain knowledge and workflows. You can query, inspect, activate, or learn new skills using skill tools.");
        sb.AppendLine("Available Skills in Brain:");

        var grouped = skills.GroupBy(s => s.Category).OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            sb.AppendLine($"• [{group.Key}]: " + string.Join(", ", group.Select(s => $"{s.Name} (`{s.Id}`)")));
        }
        sb.AppendLine("</system_skill_brain_index>");
        return sb.ToString();
    }

    public SkillReasoningResult ReasonAndSelectSkills(string userPrompt, int maxSkillsToSelect = 3)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return new SkillReasoningResult
            {
                DetectedComplexity = SkillComplexity.Simple,
                ReasoningExplanation = "Empty prompt. No skills activated.",
                FormattedPromptInjection = string.Empty
            };
        }

        var enabledSkills = _libraryManager.GetEnabledSkills();
        if (enabledSkills.Count == 0)
        {
            return new SkillReasoningResult
            {
                DetectedComplexity = SkillComplexity.Simple,
                ReasoningExplanation = "No enabled skills available in library.",
                FormattedPromptInjection = string.Empty
            };
        }

        // Step 1: Detect Task Complexity
        var complexity = AssessTaskComplexity(userPrompt);

        // Step 2: Score Skills by Relevance
        string lowerPrompt = userPrompt.ToLowerInvariant();
        var scoredSkills = new List<(Skill Skill, double Score, List<string> MatchedTerms)>();

        foreach (var skill in enabledSkills)
        {
            double score = 0;
            var matchedTerms = new List<string>();

            // ID / Name exact or partial match
            string cleanId = skill.Id.Replace("-", " ");
            if (lowerPrompt.Contains(skill.Id.ToLower()) || lowerPrompt.Contains(cleanId))
            {
                score += 15.0;
                matchedTerms.Add(skill.Name);
            }

            // Category match
            if (lowerPrompt.Contains(skill.Category.ToLowerInvariant()))
            {
                score += 5.0;
                matchedTerms.Add($"Category: {skill.Category}");
            }

            // Tag matches
            foreach (var tag in skill.Tags)
            {
                if (lowerPrompt.Contains(tag.ToLowerInvariant()))
                {
                    score += 4.0;
                    matchedTerms.Add($"Tag: #{tag}");
                }
            }

            // Keyword match in description
            var descWords = skill.Description.Split(new[] { ' ', ',', '.', ';', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in descWords)
            {
                if (word.Length > 4 && lowerPrompt.Contains(word.ToLowerInvariant()))
                {
                    score += 0.8;
                }
            }

            // Domain trigger heuristics (positive bonuses + negative penalties)
            score += EvaluateDomainHeuristics(skill.Id, lowerPrompt, matchedTerms);

            // Minimum relevance threshold gating: require score >= 12.0 before a skill is candidate for injection
            if (score >= 12.0)
            {
                scoredSkills.Add((skill, score, matchedTerms));
            }
        }

        var topSkills = scoredSkills
            .OrderByDescending(s => s.Score)
            .Take(maxSkillsToSelect)
            .ToList();

        if (topSkills.Count == 0)
        {
            return new SkillReasoningResult
            {
                DetectedComplexity = complexity,
                ReasoningExplanation = $"Task evaluated at {complexity} complexity. No specialized skills triggered (threshold >= 12.0).",
                FormattedPromptInjection = string.Empty
            };
        }

        // Step 3: Format Reasoning Explanation & System Prompt Injection
        var selectedSkillModels = topSkills.Select(s => s.Skill).ToList();
        var explanationSb = new StringBuilder();
        explanationSb.AppendLine($"🧠 **Skill Brain Assessment**");
        explanationSb.AppendLine($"• Task Complexity: `{complexity}`");
        explanationSb.AppendLine($"• Activated Skills ({selectedSkillModels.Count}):");
        foreach (var item in topSkills)
        {
            explanationSb.AppendLine($"  - **{item.Skill.Name}** ({item.Skill.Category}) [Match score: {item.Score:F1}]");
        }

        var promptSb = new StringBuilder();
        promptSb.AppendLine("\n\n<system_active_skills>");
        promptSb.AppendLine("You are equipped with the following active skills and specialized domain knowledge for this task. Follow their directives and workflows:");
        
        foreach (var skill in selectedSkillModels)
        {
            promptSb.AppendLine($"\n--- SKILL: {skill.Name} ({skill.Category}) ---");
            promptSb.AppendLine(skill.PromptInstruction.Trim());
        }
        promptSb.AppendLine("</system_active_skills>\n");

        return new SkillReasoningResult
        {
            DetectedComplexity = complexity,
            SelectedSkills = selectedSkillModels,
            ReasoningExplanation = explanationSb.ToString().Trim(),
            FormattedPromptInjection = promptSb.ToString()
        };
    }

    public async Task EnsureIndexedAsync(CancellationToken ct = default)
    {
        if (_isIndexed || _skillIndex == null) return;
        var skills = _libraryManager.GetEnabledSkills();
        var manifests = skills.Select(SkillManifestParser.FromSkill);
        await _skillIndex.IndexRangeAsync(manifests, ct);
        _isIndexed = true;
    }

    /// <summary>
    /// Executes two-stage retrieve-and-rerank skill resolution when SkillIndex and SkillReranker are available.
    /// </summary>
    public async Task<SkillReasoningResult> ReasonAndSelectSkillsAsync(string userPrompt, int maxSkillsToSelect = 3, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return new SkillReasoningResult
            {
                DetectedComplexity = SkillComplexity.Simple,
                ReasoningExplanation = "Empty prompt. No skills activated.",
                FormattedPromptInjection = string.Empty
            };
        }

        var enabledSkills = _libraryManager.GetEnabledSkills();
        if (enabledSkills.Count == 0)
        {
            return new SkillReasoningResult
            {
                DetectedComplexity = SkillComplexity.Simple,
                ReasoningExplanation = "No enabled skills available in library.",
                FormattedPromptInjection = string.Empty
            };
        }

        var complexity = AssessTaskComplexity(userPrompt);

        if (_skillIndex != null && _reranker != null)
        {
            await EnsureIndexedAsync(ct);
            var candidates = await _skillIndex.SearchCandidatesAsync(userPrompt, topK: 15, ct);
            var ranked = _reranker.Rerank(userPrompt, candidates);

            var selected = new List<Skill>();
            foreach (var sc in ranked)
            {
                if (sc.FinalScore >= 0.35 && selected.Count < maxSkillsToSelect)
                {
                    var found = enabledSkills.FirstOrDefault(s => string.Equals(s.Id, sc.Record.Manifest.SkillId, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        selected.Add(found);
                    }
                }
            }

            if (selected.Count > 0)
            {
                var explanationSb = new StringBuilder();
                explanationSb.AppendLine($"🧠 **Skill Brain Assessment (Two-Stage Reranked)**");
                explanationSb.AppendLine($"• Task Complexity: `{complexity}`");
                explanationSb.AppendLine($"• Activated Skills ({selected.Count}):");
                foreach (var sk in selected)
                {
                    var sc = ranked.FirstOrDefault(r => string.Equals(r.Record.Manifest.SkillId, sk.Id, StringComparison.OrdinalIgnoreCase));
                    explanationSb.AppendLine($"  - **{sk.Name}** ({sk.Category}) [Final score: {sc?.FinalScore ?? 0:F2}]");
                }

                var promptSb = new StringBuilder();
                promptSb.AppendLine("\n\n<system_active_skills>");
                promptSb.AppendLine("You are equipped with the following active skills and specialized domain knowledge for this task. Follow their directives and workflows:");

                foreach (var skill in selected)
                {
                    promptSb.AppendLine($"\n--- SKILL: {skill.Name} ({skill.Category}) ---");
                    promptSb.AppendLine(skill.PromptInstruction.Trim());
                }
                promptSb.AppendLine("</system_active_skills>\n");

                return new SkillReasoningResult
                {
                    DetectedComplexity = complexity,
                    SelectedSkills = selected,
                    ReasoningExplanation = explanationSb.ToString().Trim(),
                    FormattedPromptInjection = promptSb.ToString()
                };
            }
        }

        return ReasonAndSelectSkills(userPrompt, maxSkillsToSelect);
    }

    private static SkillComplexity AssessTaskComplexity(string prompt)
    {
        int wordCount = prompt.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        string lower = prompt.ToLowerInvariant();

        bool hasArchitectureTerms = lower.Contains("architect") || lower.Contains("system") || lower.Contains("refactor") || lower.Contains("pipeline") || lower.Contains("mcp") || lower.Contains("full stack");
        bool hasMultiStepTerms = lower.Contains("step by step") || lower.Contains("build a") || lower.Contains("create a complete") || lower.Contains("integrate");

        if (wordCount > 120 || (hasArchitectureTerms && hasMultiStepTerms))
            return SkillComplexity.Specialized;
        if (wordCount > 50 || hasArchitectureTerms || hasMultiStepTerms)
            return SkillComplexity.Complex;
        if (wordCount > 15)
            return SkillComplexity.Moderate;

        return SkillComplexity.Simple;
    }

    private static double EvaluateDomainHeuristics(string skillId, string lowerPrompt, List<string> matchedTerms)
    {
        double bonus = 0;
        switch (skillId)
        {
            case "webapp-testing":
                // Require explicit web testing keywords (playwright, e2e, browser testing, selenium, headless)
                bool isWebQaContext = lowerPrompt.Contains("playwright") || lowerPrompt.Contains("e2e") || lowerPrompt.Contains("browser test") || lowerPrompt.Contains("selenium") || lowerPrompt.Contains("webapp test");
                if (isWebQaContext)
                {
                    bonus += 15.0;
                    matchedTerms.Add("Web QA Domain");
                }
                else
                {
                    // Heavy penalty for general commands or tool testing prompts to prevent false activation
                    bonus -= 25.0;
                }
                break;

            case "windows-app-launcher":
                if (lowerPrompt.Contains("open") || lowerPrompt.Contains("launch") || lowerPrompt.Contains("start app") || lowerPrompt.Contains("chrome") || lowerPrompt.Contains("steam") || lowerPrompt.Contains("notepad") || lowerPrompt.Contains("vscode") || lowerPrompt.Contains("calculator"))
                {
                    bonus += 14.0;
                    matchedTerms.Add("Windows App Launch Intent");
                }
                break;

            case "windows-browser-navigation":
                if (lowerPrompt.Contains("youtube") || lowerPrompt.Contains("navigate") || lowerPrompt.Contains("open browser") || lowerPrompt.Contains("search video") || lowerPrompt.Contains("cat video") || lowerPrompt.Contains("chrome url"))
                {
                    bonus += 15.0;
                    matchedTerms.Add("Browser Navigation Intent");
                }
                break;

            case "windows-process-manager":
                if (lowerPrompt.Contains("process") || lowerPrompt.Contains("taskkill") || lowerPrompt.Contains("stop-process") || lowerPrompt.Contains("cpu usage") || lowerPrompt.Contains("kill app"))
                {
                    bonus += 14.0;
                    matchedTerms.Add("Process Manager Intent");
                }
                break;

            case "windows-system-settings":
                if (lowerPrompt.Contains("settings") || lowerPrompt.Contains("ms-settings") || lowerPrompt.Contains("display") || lowerPrompt.Contains("sound settings") || lowerPrompt.Contains("wifi settings"))
                {
                    bonus += 14.0;
                    matchedTerms.Add("Windows Settings Intent");
                }
                break;

            case "windows-file-explorer-nav":
                if (lowerPrompt.Contains("explorer") || lowerPrompt.Contains("folder") || lowerPrompt.Contains("appdata") || lowerPrompt.Contains("shortcut") || lowerPrompt.Contains("directory path"))
                {
                    bonus += 12.0;
                    matchedTerms.Add("File Explorer Intent");
                }
                break;

            case "windows-media-audio-control":
                if (lowerPrompt.Contains("volume") || lowerPrompt.Contains("mute") || lowerPrompt.Contains("audio device") || lowerPrompt.Contains("speaker") || lowerPrompt.Contains("sound volume"))
                {
                    bonus += 14.0;
                    matchedTerms.Add("Media Audio Intent");
                }
                break;

            case "windows-gaming-steam-manager":
                if (lowerPrompt.Contains("steam") || lowerPrompt.Contains("play game") || lowerPrompt.Contains("game library") || lowerPrompt.Contains("rungameid"))
                {
                    bonus += 15.0;
                    matchedTerms.Add("Steam Gaming Intent");
                }
                break;

            case "windows-terminal-powershell-expert":
                if (lowerPrompt.Contains("powershell") || lowerPrompt.Contains("cmdlet") || lowerPrompt.Contains("start-process") || lowerPrompt.Contains("script execution"))
                {
                    bonus += 12.0;
                    matchedTerms.Add("PowerShell Expert Intent");
                }
                break;

            case "mcp-builder":
                if (lowerPrompt.Contains("mcp") || lowerPrompt.Contains("context protocol") || lowerPrompt.Contains("fastmcp")) { bonus += 12; matchedTerms.Add("MCP Keyword"); }
                break;

            case "algorithmic-art":
            case "canvas-design":
                if (lowerPrompt.Contains("art") || lowerPrompt.Contains("p5") || lowerPrompt.Contains("canvas") || lowerPrompt.Contains("animation") || lowerPrompt.Contains("drawing")) { bonus += 12; matchedTerms.Add("Creative Art Keyword"); }
                break;

            case "artifacts-builder":
                if (lowerPrompt.Contains("artifact") || lowerPrompt.Contains("widget") || lowerPrompt.Contains("interactive app")) { bonus += 12; matchedTerms.Add("Artifact Keyword"); }
                break;

            case "changelog-generator":
                if (lowerPrompt.Contains("changelog") || lowerPrompt.Contains("release notes") || lowerPrompt.Contains("commits")) { bonus += 12; matchedTerms.Add("Changelog Keyword"); }
                break;

            case "content-research-writer":
                if (lowerPrompt.Contains("article") || lowerPrompt.Contains("blog") || lowerPrompt.Contains("write a report") || lowerPrompt.Contains("essay")) { bonus += 10; matchedTerms.Add("Writing Keyword"); }
                break;

            case "brand-guidelines":
                if (lowerPrompt.Contains("brand") || lowerPrompt.Contains("logo") || lowerPrompt.Contains("palette") || lowerPrompt.Contains("colors")) { bonus += 10; matchedTerms.Add("Brand Keyword"); }
                break;
        }
        return bonus;
    }
}

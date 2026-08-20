using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// The SINGLE owner of step semantics (P1.8). Given a step's text, it deterministically
/// produces the step's ExpectedActionKind, AllowedTools, skills, artifacts, verification
/// criteria and completion condition. Every consumer — TaskStepBuilder, ActionObligation, the
/// Action Gate, the correction directive, the prompt — derives from this one factory, so the
/// loop never phrase-matches step text itself.
///
/// This replaces the old StepToolPolicy bridge. It is stricter than the bridge: a
/// "build the hero section" step now classifies as FileMutation with a restricted tool set,
/// so the model is never handed the full registered surface for a step that has a clear
/// action kind.
///
/// Harness-control tools (plan, task_complete, task_progress, queue tools) are ALWAYS
/// allowed regardless of the step — step scoping governs workspace tools, never the control
/// surface that advances the plan or ends the run.
/// </summary>
public static class StepClassifier
{
    /// <summary>Tools that are never restricted by step scoping.</summary>
    public static readonly IReadOnlySet<string> ControlTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "plan", "task_complete", "task_progress",
            "check_message_queue", "incorporate_queued_message"
        };

    private static readonly string[] VerificationMarkers =
    {
        "verify the result", "verify the implementation", "verify the change", "verify the fix",
        "verify the deliverable", "verify the output", "verify the build",
        "run the build", "run the build and tests", "run the tests", "run a local preview",
        "final verification", "check the build", "check the tests", "confirm the build"
    };

    private static readonly string[] SummaryMarkers =
    {
        "summarize", "summarise", "present the deliverable", "write the final summary",
        "finalize the deliverable"
    };

    private static readonly string[] InspectionMarkers =
    {
        "inspect the", "explore the existing", "review the existing", "analyze the existing",
        "understand the existing", "examine the existing", "inspect the existing",
        "review the codebase", "explore the project", "inspect the project"
    };

    /// <summary>
    /// Requirement / creative-direction steps: the model produces reasoning or a design
    /// direction — NO workspace tools. The first landing-page step is exactly this case:
    /// "Capture the requirements and creative direction" must not expose search_web,
    /// run_command or write_file, so the model cannot invent a web-research workflow the
    /// user never requested (the Qwen export's core failure). AllowedTools stays non-null
    /// (control tools only), so the Action Gate enforces it as a strict empty workspace set.
    /// </summary>
    private static readonly string[] ReasonMarkers =
    {
        "capture the requirements", "capture the creative direction", "creative direction",
        "restate the requirements", "clarify the app's purpose", "define the script's inputs",
        "define the investigation scope", "acceptance criteria", "understand the request",
        "creative lead"
    };

    /// <summary>
    /// Mutation markers are ACTION VERBS ONLY. Generic nouns (design, page, section,
    /// component, hero, style) are deliberately NOT signals: "Design the landing-page
    /// architecture and visual direction" is a PLANNING step (a design direction, not a
    /// file edit), and a bare noun like "page" says nothing about whether the model should
    /// be handed write_file. Classification must never sweep a design/planning step into
    /// the mutation surface — that is exactly the misclassification that handed the Qwen
    /// export write_file on a step whose deliverable is a design decision.
    /// </summary>
    private static readonly string[] MutationMarkers =
    {
        "build the", "implement", "create the", "write the", "add the", "update the",
        "fix the", "modify", "edit", "configure", "refactor", "develop the"
    };

    private static readonly string[] ResearchMarkers =
    {
        "research", "search the web", "competitive analysis", "market research",
        "analyze the business", "analyze the target audience", "analyze the requirements",
        "gather requirements", "gather the"
    };

    /// <summary>
    /// Planning markers include the design verbs: "Design the landing-page architecture and
    /// visual direction", "Design the approach" and "Design the UI layout" are PLANNING
    /// steps (their deliverable is a design direction), so "design the" selects Plan — and
    /// because Planning is evaluated BEFORE Mutation, those steps can never be swept into
    /// the file-mutation surface.
    /// </summary>
    private static readonly string[] PlanningMarkers =
    {
        "plan the", "create a plan", "define the architecture", "design the architecture",
        "design the", "outline the", "generate the plan", "architecture"
    };

    /// <summary>
    /// The verification PREDICATE for a step (P1.10/P1.14): the evidence kinds that actually
    /// satisfy the step's verification obligation, derived from the same text the classifier
    /// owns. The supervisor compares recorded evidence against this set — "some command
    /// succeeded" (CommandSucceeded) never satisfies a step that requires a build/test/
    /// preview result. When no specific kind can be derived, the empty set means "any
    /// verification-capable evidence" (the caller's fallback), never "no evidence needed".
    /// Only meaningful for Verification steps.
    /// </summary>
    public static IReadOnlyList<EvidenceKind> ClassifyEvidenceKinds(string? stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText)) return Array.Empty<EvidenceKind>();
        string t = stepText.ToLowerInvariant();

        if (t.Contains("build") || t.Contains("compile"))
        {
            // "run the build and tests" requires BOTH kinds to be producible.
            return t.Contains("test")
                ? new[] { EvidenceKind.BuildPassed, EvidenceKind.TestPassed }
                : new[] { EvidenceKind.BuildPassed };
        }
        if (t.Contains("test"))
        {
            return new[] { EvidenceKind.TestPassed };
        }
        if (t.Contains("preview") || t.Contains("renders") || t.Contains("render"))
        {
            return new[] { EvidenceKind.PreviewLoaded };
        }
        if (t.Contains("verify"))
        {
            return new[] { EvidenceKind.BuildPassed, EvidenceKind.TestPassed,
                           EvidenceKind.PreviewLoaded, EvidenceKind.ScreenshotCaptured };
        }
        return Array.Empty<EvidenceKind>();
    }

    /// <summary>
    /// The verification predicate as <see cref="VerificationCriterion"/> records (P1.14):
    /// same kinds as <see cref="ClassifyEvidenceKinds"/>, each mapped to a criterion the
    /// supervisor and the completion gate evaluate recorded evidence against. Criteria
    /// support subject-scoped matching, so callers can tighten a kind to a specific file,
    /// project or url.
    /// </summary>
    public static IReadOnlyList<VerificationCriterion> ClassifyCriteria(string? stepText)
    {
        var kinds = ClassifyEvidenceKinds(stepText);
        if (kinds.Count == 0) return Array.Empty<VerificationCriterion>();
        var criteria = new VerificationCriterion[kinds.Count];
        for (int i = 0; i < kinds.Count; i++)
        {
            criteria[i] = new VerificationCriterion(kinds[i]);
        }
        return criteria;
    }

    /// <summary>
    /// Classifies a step's text into its execution contract. Deterministic and conservative:
    /// explicit markers select the kind and restrict the tool set; everything else stays
    /// existence-gated (AllowedTools = null) with Kind=None so the model keeps its full
    /// legitimate surface rather than being boxed out by an uncertain guess.
    /// </summary>
    public static StepClassification Classify(string stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText))
        {
            return StepClassification.Default;
        }
        string t = stepText.ToLowerInvariant();

        if (VerificationMarkers.Any(t.Contains))
        {
            return Make(StepActionKind.Verification,
                new[] { "run_command", "read_file", "list_directory", "search_files" },
                skills: new[] { "verification", "evidence" },
                artifacts: Array.Empty<string>(),
                criteria: new[] { "Build/tests/commands succeed", "Evidence recorded", "Result reported factually" },
                condition: "verification evidence produced");
        }
        if (SummaryMarkers.Any(t.Contains))
        {
            return Make(StepActionKind.Summary,
                new[] { "write_file", "read_file" },
                skills: new[] { "delivery" },
                artifacts: new[] { "Deliverable" },
                criteria: new[] { "Deliverable produced", "Result presented" },
                condition: "deliverable presented");
        }
        // Reason steps come BEFORE inspection/mutation so "Capture the requirements and
        // creative direction" is never swept into a tool-demanding kind: it gets NO workspace
        // tools, only the control surface (plan / task_complete / task_progress / queue).
        if (ReasonMarkers.Any(t.Contains))
        {
            return Make(StepActionKind.Reason,
                Array.Empty<string>(), // no workspace tools — control tools only
                skills: new[] { "requirement-capture", "design-direction" },
                artifacts: new[] { "Requirements / design direction" },
                criteria: new[] { "Requirements analyzed", "Creative direction established" },
                condition: "requirements captured / creative direction produced");
        }
        // PLANNING comes BEFORE inspection/mutation (semantic precedence, P1.8-Fix-1): a
        // design/architecture step is a Plan (its deliverable is a design direction), never
        // a FileMutation. "Design the landing-page architecture and visual direction" must
        // yield AllowedTools = { plan } + control tools — NOT write_file/edit_file — or the
        // model is handed the mutation surface for a step whose output is a decision.
        if (PlanningMarkers.Any(t.Contains))
        {
            return Make(StepActionKind.Plan,
                new[] { "plan" },
                skills: new[] { "planning" },
                artifacts: Array.Empty<string>(),
                criteria: new[] { "Plan reflects reality" },
                condition: "plan created or revised");
        }
        if (InspectionMarkers.Any(t.Contains))
        {
            return Make(StepActionKind.Inspect,
                new[] { "list_directory", "read_file", "search_files" },
                skills: new[] { "workspace-navigation" },
                artifacts: Array.Empty<string>(),
                criteria: new[] { "Workspace inspected", "Findings factual (no invented contents)" },
                condition: "workspace inspection evidence");
        }
        if (ResearchMarkers.Any(t.Contains))
        {
            return Make(StepActionKind.Research,
                new[] { "search_web", "crawl_url", "read_file", "list_directory" },
                skills: new[] { "research" },
                artifacts: new[] { "Research notes" },
                criteria: new[] { "Findings from real tool results only" },
                condition: "research evidence");
        }
        if (MutationMarkers.Any(t.Contains))
        {
            return Make(StepActionKind.FileMutation,
                new[] { "read_file", "write_file", "edit_file", "replace_lines", "apply_patch", "structural_replace", "list_directory", "search_files" },
                skills: new[] { "file-mutation" },
                artifacts: new[] { "Code/Document changed" },
                criteria: new[] { "Files actually changed", "No syntax errors" },
                condition: "file mutation evidence");
        }

        // No marker matched: no restriction (existence-gated only), no specific action kind.
        return StepClassification.Default;
    }

    private static StepClassification Make(
        StepActionKind kind,
        string[] workspaceTools,
        string[]? skills = null,
        string[]? artifacts = null,
        string[]? criteria = null,
        string? condition = null)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in workspaceTools) allowed.Add(tool);
        allowed.UnionWith(ControlTools);
        return new StepClassification(
            kind,
            allowed,
            skills ?? Array.Empty<string>(),
            artifacts ?? Array.Empty<string>(),
            criteria ?? Array.Empty<string>(),
            condition);
    }
}

/// <summary>
/// The deterministic result of <see cref="StepClassifier.Classify"/>: a step's execution
/// contract. Default = no restriction (null AllowedTools), Kind=None, no criteria.
/// </summary>
public sealed record StepClassification(
    StepActionKind ExpectedActionKind,
    IReadOnlySet<string>? AllowedTools,
    IReadOnlyList<string> RequiredSkills,
    IReadOnlyList<string> ExpectedArtifacts,
    IReadOnlyList<string> VerificationCriteria,
    string? CompletionCondition)
{
    public static readonly StepClassification Default = new(
        StepActionKind.None, null,
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);
}

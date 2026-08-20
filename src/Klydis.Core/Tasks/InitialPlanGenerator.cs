using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Deterministic harness-owned initial planner (workbench spec §2). The runtime establishes the
/// baseline plan for an actionable request BEFORE the model's first turn, so the task has a
/// durable backbone without depending on the model remembering to call 'plan'. The plan is a
/// task-specific checklist (not a generic four-item scaffold): for common request domains the
/// generator produces real implementation steps.
///
/// The plan is deliberately SMALL and high-level: the old twelve-step landing-page plan
/// over-prescribed sections (hero/services/gallery/CTA) before the workspace or requirements
/// existed, which incentivized the model to manufacture missing information and plan its own
/// sub-execution. Six well-defined steps with a single executable current step are much
/// safer for weaker models than a large plan plus the full tool catalog. The model may
/// refine or expand it via the 'plan' tool AFTER the design step exists.
/// </summary>
public static class InitialPlanGenerator
{
    /// <summary>
    /// Generates a domain-appropriate baseline plan for a substantive actionable request.
    /// Pure and deterministic — no I/O, no model calls — so it is trivially testable.
    /// </summary>
    public static IReadOnlyList<string> Generate(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return DefaultSteps();

        string lower = userMessage.ToLowerInvariant();

        // Web / landing-page work is the most common autonomous build request. The plan is
        // six high-level steps: capture (reason, no tools) → inspect (filesystem) → design
        // (plan) → implement (file mutation) → preview/verify (evidence) → finalize. Detailed
        // sections (hero, services, gallery, CTA, responsive) EMERGE from the design step
        // once the workspace is known — they are never pre-asserted.
        if (ContainsAny(lower, "landing page", "website", "web page", "web site", "homepage",
            "portfolio site", "react site", "frontend", "vite", "html page", "landing-page"))
        {
            return LandingPageSteps();
        }

        if (ContainsAny(lower, "api", "backend", "server", "endpoint", "rest api", "graphql",
            "webhook", "microservice"))
        {
            return ApiSteps();
        }

        if (ContainsAny(lower, "desktop app", "wpf", "windows app", "winforms", "gui",
            "desktop application", "winui", "uwp"))
        {
            return DesktopAppSteps();
        }

        if (ContainsAny(lower, "script", "cli", "command line tool", "automation",
            "powershell script", "batch", "utility"))
        {
            return ScriptSteps();
        }

        // Research / analysis / review requests get an investigation plan, not an implementation
        // plan (the reviewer's MSCI example: scope → retrieve → analyze → compare → assess →
        // recommend → verify).
        if (ContainsAny(lower, "analyze", "research", "investigate", "review", "compare",
            "evaluate", "diagnose", "find out", "assess", "why is", "why did", "report", "machine report", "system report"))
        {
            return ResearchSteps();
        }

        return DefaultSteps();
    }

    private static bool ContainsAny(string text, params string[] phrases)
        => phrases.Any(p => text.Contains(p, StringComparison.Ordinal));

    private static IReadOnlyList<string> LandingPageSteps() => new[]
    {
        "Capture the requirements and creative direction",
        "Inspect the workspace and existing project structure",
        "Design the landing-page architecture and visual direction",
        "Implement the landing page (hero, services, gallery, CTA, responsive)",
        "Run a local preview and verify the result",
        "Finalize the deliverable and summarize"
    };

    private static IReadOnlyList<string> ApiSteps() => new[]
    {
        "Define the data model and API contract",
        "Inspect the existing project structure and storage",
        "Design the endpoint surface",
        "Implement the core endpoints",
        "Wire in persistence / storage",
        "Handle validation and errors",
        "Write integration tests",
        "Run the build and tests",
        "Verify against the API contract"
    };

    private static IReadOnlyList<string> DesktopAppSteps() => new[]
    {
        "Clarify the app's purpose, users and acceptance criteria",
        "Inspect the existing project structure and framework",
        "Design the UI layout and navigation",
        "Implement the main window and core views",
        "Wire in application logic and state",
        "Add the supporting features",
        "Build the application",
        "Run the app and test the flows",
        "Fix issues and final verification"
    };

    private static IReadOnlyList<string> ScriptSteps() => new[]
    {
        "Define the script's inputs, outputs and success criteria",
        "Inspect the environment and existing related files",
        "Implement the core logic",
        "Handle edge cases and errors",
        "Test the script against real inputs",
        "Verify the output and finalize"
    };

    private static IReadOnlyList<string> ResearchSteps() => new[]
    {
        "Define the investigation scope and what 'answered' looks like",
        "Gather the current data and relevant sources",
        "Identify the key drivers and contributing factors",
        "Analyze the evidence (sector, trends, comparisons)",
        "Assess risks and implications",
        "Formulate the recommendation or conclusion",
        "Verify data freshness and sources"
    };

    private static IReadOnlyList<string> DefaultSteps() => new[]
    {
        "Restate the requirements and define acceptance criteria",
        "Inspect the relevant files and current state",
        "Design the approach",
        "Implement the solution",
        "Verify the result (build, tests, evidence)",
        "Summarize the deliverable"
    };
}

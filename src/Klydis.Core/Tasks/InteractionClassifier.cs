using System;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// The operating mode for a user message — decided BEFORE task resolution and prompt
/// construction. This is the boundary the harness was missing: without it, a greeting like
/// "good evening" became an AgentTask with a task contract, the full tool schema, and the
/// agentic workflow — so the model treated a conversational turn as an agent turn (observed:
/// the model reasoned about read_file/run_command in response to a greeting). The mode
/// controls what the runtime exposes:
///   Conversation → no task, no tools, no plan, no skills, no workbench — a minimal prompt.
///   Task         → tools + task contract for inspection/analysis/research work.
///   Autonomous   → the full runtime: plan, skills, artifacts, verification, continuation.
/// The classifier is intentionally deterministic (heuristic) for now; the review allows a
/// small classifier model to evolve it later.
/// </summary>
public enum InteractionMode
{
    /// <summary>Casual chat, greetings, explanations. No execution state, no tools.</summary>
    Conversation,

    /// <summary>Inspection / analysis / research that needs tools but is bounded work.</summary>
    Task,

    /// <summary>Build / implement / fix: full agentic runtime with plan and verification.</summary>
    Autonomous
}

/// <summary>
/// Fine-grained subtype of the interaction to enforce specialized epistemic contracts.
/// </summary>
public enum InteractionSubtype
{
    /// <summary>Standard task or conversation.</summary>
    General,

    /// <summary>Hardware, OS, or environment inspection — requires tool evidence, simulation forbidden.</summary>
    SystemInspection,

    /// <summary>Requirements gathering / scoping — strictly isolates confirmed facts, unknowns, and suggestions.</summary>
    RequirementExtraction,

    /// <summary>Brainstorming or creative ideation.</summary>
    CreativeExpansion
}

/// <summary>
/// Deterministic interaction classifier. Priority order: explicit conversational intent
/// (greetings, gratitude, explanation requests) wins; then strong build/fix verbs
/// (Autonomous); then analysis/research verbs (Task); short messages fall back to
/// Conversation. Deliberately heuristic — the review's examples are the contract:
/// "good evening"/"hello"/"explain X" → Conversation, "analyze this repository" → Task,
/// "build X"/"fix this project" → Autonomous.
/// </summary>
public static class InteractionClassifier
{
    private static readonly string[] GreetingMarkers =
    {
        "hello", "hi", "hey", "yo", "greetings", "good morning", "good afternoon",
        "good evening", "good night", "how are you", "hows it going", "how is it going",
        "how are things", "whats up", "whats going on", "hi there", "hello there",
        "thanks", "thank you", "thankyou", "thx", "appreciate", "bye", "goodbye",
        "see you", "have a good", "welcome", "nice to meet", "long time"
    };

    // Live-data requests: weather/news/prices read like plain questions but REQUIRE the
    // web tools ("weather please" is a search_web job, not a chat reply). The reviewer's
    // own rule — "requires external action? yes → Task" — applies here. Checked before the
    // explanation tier so "what's the weather" gets tools instead of being treated as a
    // knowledge question; pure explanations that merely mention a data word ("explain how
    // forecasting works") may land in Task too, where tools are available but optional.
    private static readonly string[] LiveDataMarkers =
    {
        "weather", "forecast", "temperature", "news", "headlines", "stock price",
        "stock market", "exchange rate", "price of", "bitcoin", "crypto", "score",
        "who won", "what time", "is it raining", "traffic", "flight", "delays"
    };

    // System, hardware, environment, and tool execution queries REQUIRE tools — Task mode.
    private static readonly string[] SystemInspectionMarkers =
    {
        "system report", "full system report", "machine report", "hardware report",
        "system info", "system information", "system status", "system specs",
        "hardware specs", "cpu usage", "ram usage", "disk space", "disk usage",
        "what os", "what operating system", "which os", "which operating system",
        "confirm what os", "my os", "current machine", "my machine", "my system",
        "this machine", "test tools", "test all tools", "test all your tools",
        "test your tools", "run tool", "list files", "show files", "inspect system",
        "diagnose system", "check system", "check hardware", "memory usage", "disk load",
        "cpu utilization", "gpu utilization", "system metrics", "hardware metrics",
        "software report", "inspect cpu", "inspect gpu", "inspect ram", "check cpu",
        "check gpu", "check ram", "check disk", "check os", "system diagnostics"
    };

    // Explicit execution commands and markers — imperative execution requests REQUIRE tools.
    private static readonly string[] ExecutionMarkers =
    {
        "execute", "run", "perform", "inspect", "check", "scan", "investigate",
        "retrieve", "gather", "determine", "measure", "monitor", "benchmark",
        "diagnose", "query", "fetch", "extract", "audit"
    };

    // Informational / explanation requests. These are CONVERSATION even when they mention
    // verbs ("explain how to build an autonomous agent" is a question, not a build task).
    private static readonly string[] ExplanationMarkers =
    {
        "explain", "what is", "what are", "whats", "what does", "how do", "how does",
        "how can", "how to", "why", "tell me about", "tell me", "describe", "define",
        "meaning of", "difference between", "whats the difference", "how would",
        "can you explain", "can you tell", "elaborate", "clarify", "more about",
        "in detail", "walk me through", "overview of", "introduction to"
    };

    // Imperative / continuation commands: explicit "do the work" instructions. These must
    // get tools even when no strong verb is present — "i want you to begin building the
    // project" previously fell through every verb list and hit the short-message fallback,
    // degrading to Conversation (no tools, no task contract), and the model just chatted
    // instead of working. Checked before the verb lists so "begin", "start", "continue"
    // always route to tool-using Task mode; the task resolver then continues the same task.
    private static readonly string[] CommandMarkers =
    {
        "begin", "start", "proceed", "continue", "go ahead", "get started",
        "lets start", "lets begin", "start working", "start building",
        "begin building", "begin work", "get to work", "do it", "do it now",
        "keep going", "keep working", "carry on", "start now", "right away",
        "immediately", "execute", "execute the", "run the", "perform the"
    };

    // Strong build/fix verbs: unambiguous executable work, regardless of message length.
    private static readonly string[] AutonomousVerbs =
    {
        "build", "implement", "develop", "fix", "refactor", "migrate", "port",
        "debug", "optimize", "deploy", "configure", "integrate", "install",
        "automate", "scaffold"
    };

    // Weaker creation verbs: Autonomous only for substantial requests (a long prompt implies
    // real deliverable work; "make a joke" should never spin up the full runtime).
    private static readonly string[] WeakAutonomousVerbs =
    {
        "create", "make", "write", "produce", "design", "set up"
    };

    // Analysis / research verbs: bounded tool-using work → Task mode.
    private static readonly string[] TaskVerbs =
    {
        "analyze", "inspect", "review", "compare", "research", "investigate",
        "summarize", "evaluate", "test", "examine", "explore", "diagnose",
        "trace", "monitor", "benchmark", "profile", "identify", "find",
        "read", "search for", "go through", "look at", "execute", "run",
        "determine", "measure", "scan", "audit", "gather", "retrieve"
    };

    /// <summary>
    /// Classifies a user message into an interaction mode.
    /// </summary>
    public static InteractionMode Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return InteractionMode.Conversation;



        string normalized = Normalize(message);
        string[] tokens = Tokenize(normalized);

        // 0. System inspection / environment / tool testing queries ALWAYS route to Task mode with tools.
        if (ContainsAny(tokens, SystemInspectionMarkers)) return InteractionMode.Task;

        // 1. Explicit conversational intent wins over everything when NOT accompanied by explicit execution commands.
        if (ContainsAny(tokens, GreetingMarkers) && !ContainsAny(tokens, ExecutionMarkers) && !ContainsAny(tokens, AutonomousVerbs))
            return InteractionMode.Conversation;

        // 1b. Live-data requests need the web/search tools — Task mode, never Conversation.
        if (ContainsAny(tokens, LiveDataMarkers)) return InteractionMode.Task;

        // 2. Explanation / informational questions are conversation — even with verbs inside
        //    ("explain how to build X" asks for an explanation, it does not build X).
        if (ContainsAny(tokens, ExplanationMarkers) && !HasImperativeExecutionCommand(normalized))
            return InteractionMode.Conversation;

        // 2b. COMPOUND imperative: a command marker FOLLOWED by an autonomous verb
        //     ("begin building", "start implementing", "continue developing",
        //     "proceed with fixing") is an execution command, not a bounded steer — the
        //     autonomous verb dominates the marker, so it routes to Autonomous (goal mode on,
        //     full execution framing) instead of a plain Tool-using Task turn.
        if (ContainsAny(tokens, CommandMarkers) && HasAutonomousVerbAfterCommand(normalized))
        {
            return InteractionMode.Autonomous;
        }

        // 3. Strong build/fix verbs → Autonomous.
        if (ContainsAny(tokens, AutonomousVerbs)) return InteractionMode.Autonomous;

        // 4. Weak creation verbs → Autonomous only when the request is substantial.
        if (normalized.Length >= 40 && ContainsAny(tokens, WeakAutonomousVerbs))
        {
            return InteractionMode.Autonomous;
        }

        // 2c. Plain imperative / continuation commands WITHOUT an execution verb ("continue",
        //     "go ahead", "start working on it") → Task (tools available). Checked after the
        //     verb tiers so "build the site, then start" still hits Autonomous via "build".
        if (ContainsAny(tokens, CommandMarkers)) return InteractionMode.Task;

        // 5. Analysis / research / execution verbs → Task.
        if (ContainsAny(tokens, TaskVerbs) || ContainsAny(tokens, ExecutionMarkers)) return InteractionMode.Task;

        // 6. Fallback: short messages are conversation; anything substantial defaults to Task.
        return normalized.Length < 40 ? InteractionMode.Conversation : InteractionMode.Task;
    }

    /// <summary>
    /// Checks if the message starts with or clearly contains an imperative execution directive.
    /// </summary>
    private static bool HasImperativeExecutionCommand(string normalized)
    {
        string[] imperatives = { "execute ", "run ", "perform ", "inspect ", "check ", "scan ", "determine " };
        return imperatives.Any(imp => normalized.StartsWith(imp, StringComparison.Ordinal) || normalized.Contains(" " + imp, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when an autonomous execution verb (build/implement/fix/...) appears AFTER the
    /// earliest command marker in the normalized message — "begin building" yes, "build a
    /// site, then start" no (the latter is caught by the strong-verb tier instead).
    /// </summary>
    private static bool HasAutonomousVerbAfterCommand(string normalized)
    {
        int commandPos = int.MaxValue;
        foreach (var marker in CommandMarkers)
        {
            int idx = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0 && idx < commandPos) commandPos = idx;
        }
        if (commandPos == int.MaxValue) return false;

        foreach (var verb in AutonomousVerbs)
        {
            int idx = normalized.IndexOf(verb, StringComparison.Ordinal);
            if (idx > commandPos) return true;
        }
        return false;
    }

    private static string Normalize(string message)
    {
        // Lowercase, strip apostrophes ("what's" → "whats") so markers match, collapse runs of
        // whitespace.
        string s = message.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal);
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static string[] Tokenize(string normalized)
        => normalized.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '"', '-', '_', '/', '\\', '&', '|', '+', '=', '#', '@', '$', '%', '^', '*', '~', '`', '<', '>', '[', ']', '{', '}' },
            StringSplitOptions.RemoveEmptyEntries);

    private static bool ContainsAny(string[] tokens, string[] markers)
        => markers.Any(m => m.Contains(' ') ? HasPhrase(tokens, m) : tokens.Contains(m));

    private static readonly string[] RequirementMarkers =
    {
        "want to build", "want to create", "want a landing page", "landing page for",
        "website for", "app for", "application for", "requirements for", "spec for",
        "specification for", "design a page for", "build a page for", "scope out",
        "feature list", "user stories"
    };

    /// <summary>
    /// Classifies the interaction subtype to determine epistemic constraints and output format.
    /// </summary>
    public static InteractionSubtype ClassifySubtype(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return InteractionSubtype.General;
        string normalized = Normalize(message);
        string[] tokens = Tokenize(normalized);

        if (ContainsAny(tokens, SystemInspectionMarkers)) return InteractionSubtype.SystemInspection;
        if (ContainsAny(tokens, RequirementMarkers)) return InteractionSubtype.RequirementExtraction;
        return InteractionSubtype.General;
    }

    /// <summary>
    /// Generates the strict requirement extraction prompt contract to prevent creative completion leaking into user requirements.
    /// </summary>
    public static string FormatRequirementExtractionContract()
    {
        return @"[EPISTEMIC CONTRACT — REQUIREMENTS EXTRACTION]
You are extracting requirements from the user's request.
You MUST distinguish confirmed requirements from unknowns and creative suggestions.
Return your extraction strictly in this structured form:
{
  ""confirmedRequirements"": [ ""List only facts explicitly stated by the user"" ],
  ""unknowns"": [ ""List missing specifications such as company name, pricing, branding, domain, etc."" ],
  ""suggestions"": [ ""List recommended ideas, UI sections, or optional features without treating them as facts"" ]
}
DO NOT invent technical specifications or treat creative ideas as confirmed requirements.";
    }

    private static bool HasPhrase(string[] tokens, string phrase)
    {
        var words = phrase.Split(' ');
        if (words.Length == 1) return tokens.Contains(words[0]);
        for (int i = 0; i + words.Length <= tokens.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < words.Length; j++)
            {
                if (!string.Equals(tokens[i + j], words[j], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }
}

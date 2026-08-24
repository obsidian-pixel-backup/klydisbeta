using System;
using System.Collections.Generic;

namespace Klydis.Core.Tasks;

/// <summary>
/// How much side effect a tool call has, used by recovery/retry policy (review: never
/// blindly re-execute an action whose side effects you cannot undo). ReadOnly tools may be
/// re-attempted automatically after a failure; Destructive and ExternalSideEffect tools must
/// never be re-executed without explicit supervision — a timed-out delete_file or an
/// ambiguous run_command is NOT safe to re-run just because the harness wants progress.
/// </summary>
public enum ToolSideEffectLevel
{
    /// <summary>No state change: safe to re-run after failure (reads, listings, queries).</summary>
    ReadOnly,

    /// <summary>Safe to re-run because the operation is naturally idempotent (writes are
    /// content-replacements; re-running with the same content converges to the same state).</summary>
    Idempotent,

    /// <summary>Has real external effects (processes, network, persistence side effects);
    /// re-running may double the effect. Re-run only with explicit justification.</summary>
    ExternalSideEffect,

    /// <summary>Destructive: cannot be undone (deletes). NEVER auto-retried.</summary>
    Destructive
}

/// <summary>
/// The single deterministic owner of tool side-effect classification. Unknown tools classify
/// as <see cref="ToolSideEffectLevel.ExternalSideEffect"/> (conservative: never auto-retry a
/// tool whose effects are not understood).
/// </summary>
public static class ToolSideEffectClassifier
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "view_file", "list_directory", "list_dir", "search_files", "grep_search",
        "get_system_info", "system_report", "system_cpu_metrics", "system_gpu_metrics", "system_memory_metrics",
        "system_disk_metrics", "system_os_info", "system_processes",
        "search_web", "crawl_url", "check_message_queue", "retrieve_memory",
        "list_skills", "search_skills", "get_skill_details", "search_rag", "list_rag_collections",
        "recall_lessons", "list_custom_tools", "get_custom_tool"
    };

    private static readonly HashSet<string> IdempotentTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "replace_lines", "apply_patch", "structural_replace",
        "str_replace", "store_memory", "index_folder_rag",
        "activate_skill", "learn_skill", "learn_lesson", "task_progress", "plan",
        "incorporate_queued_message", "summarize_context"
    };

    private static readonly HashSet<string> DestructiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete_skill", "delete_custom_tool", "delete_file", "remove_file", "clear_rag"
    };

    /// <summary>
    /// Command prefixes that are safe to re-run because they only observe state (queries,
    /// listings, telemetry). A run_command whose first token is one of these is classified
    /// ReadOnly, so the replay guard lets the model re-run it for a fresh reading instead of
    /// hard-blocking an identical diagnostic (the observed 9x REPLAY_DETECTED loop on
    /// read-only commands). This is a POSITIVE allowlist — a command NOT on it stays
    /// ExternalSideEffect and keeps the replay guard, so a mutating command can never be
    /// misclassified as safe.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyCommandPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // PowerShell read-only cmdlets: Get-* never mutates, and Select/Test/Measure/Format/
        // ConvertTo/Compare/Where-Object/ForEach-Object are pure pipeline transforms. These
        // prefixes are SAFE because PowerShell Get-* is guaranteed read-only; a wrapped
        // `powershell -Command "..."` is NOT allowlisted because the inner command could be
        // anything.
        "get-", "select-", "test-", "measure-", "compare-", "convertto-", "format-",
        "where-object", "foreach-object", "out-string", "write-output", "write-host",
        "get-ciminstance", "get-wmiobject", "get-process", "get-service", "get-counter",
        "get-computerinfo", "get-volume", "get-physicaldisk", "get-disk", "get-netadapter",
        "get-netipaddress", "get-date", "get-childitem", "get-content", "get-location",
        "get-item", "get-itemproperty", "get-alias", "get-command", "get-help",
        "get-executionpolicy", "get-windowsfeature", "get-hotfix", "get-eventlog",
        "get-smbshare", "get-printer", "get-scheduledtask", "get-storage",
        "get-psdrive", "get-timezone", "get-environment", "get-netfirewallprofile",
        "get-culture", "get-host", "get-variable", "test-connection", "test-path",
        "test-netconnection", "get-uptime", "get-time",
        // Legacy / cross-shell read-only utilities (no file writes, no process control).
        "systeminfo", "tasklist", "netstat", "ipconfig", "tracert", "nslookup", "whoami",
        "hostname", "ver", "dir", "ls", "type", "cat", "findstr", "find ", "where ",
        "wmic", "git status", "git diff", "git log", "git show", "git branch",
        "git remote", "git tag", "git blame", "git ls-files", "git rev-parse", "git config --list"
    };

    /// <summary>
    /// Mutating markers that disqualify a command from being read-only even when it STARTS
    /// with a read-only prefix (e.g. "Get-Process | Out-File procs.txt" starts with Get- but
    /// writes a file). Conservative: a false positive here only means the command stays
    /// replay-blocked (the pre-existing behavior) — it can never wrongly allow a mutating
    /// replay.
    /// </summary>
    private static readonly string[] MutatingCommandMarkers =
    {
        // PowerShell file/state writes reachable via a pipeline.
        "out-file", "set-content", "add-content", "clear-content", "export-csv",
        "export-clixml", "tee-object", "new-item", "remove-item", "move-item",
        "copy-item", "rename-item", "set-item", "set-acl", "new-psdrive",
        "remove-psdrive", "set-executionpolicy",
        // Process / service / machine control.
        "taskkill", "stop-process", "start-process", "stop-service", "start-service",
        "restart-service", "set-service", "new-service", "shutdown",
        "restart-computer", "stop-computer", "format-volume", "initialize-disk",
        "clear-disk", "set-volume",
        // Registry / installer / package writes.
        "reg add", "reg delete", "reg import", "install-", "uninstall-",
        "npm install", "npm publish", "npm run", "pip install", "pip uninstall",
        "pip download", "dotnet add", "dotnet remove", "dotnet new ", "dotnet publish",
        "dotnet pack", "dotnet restore",
        // Git writes.
        "git push", "git commit", "git reset", "git rebase", "git merge", "git clean",
        "git checkout", "git restore", "git stash", "git rm", "git mv", "git cherry-pick",
        // Legacy file / env mutation verbs.
        "del ", "rm ", "rmdir", "mkdir", "copy ", "move ", "ren ", "erase ", "setx ",
        "remove-", "delete-", "clear-", "new-", "mount-", "dismount-", "enable-",
        "disable-", "write-"
    };

    /// <summary>
    /// True when a shell command only observes state (no writes, deletes, installs, process
    /// control or network mutations). Conservative: only the allowlisted read-only prefixes
    /// qualify, and any mutating marker anywhere in the command disqualifies it — so the
    /// replay guard can never be bypassed by an ambiguous command.
    /// </summary>
    public static bool IsReadOnlyCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string trimmed = command.Trim();
        // A redirect to a file (>, >>) is a write — treat as side-effectful immediately.
        // (The '=>' lambda operator is not a redirect.)
        if (trimmed.Contains(">") && !trimmed.Contains("=>")) return false;
        // Must start with a known read-only prefix.
        bool startsReadOnly = ReadOnlyCommandPrefixes.Any(prefix =>
            trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (!startsReadOnly) return false;
        // The prefix only says how it STARTS — a pipeline into a mutating cmdlet (e.g.
        // "Get-Process | Out-File") is still a write.
        return !MutatingCommandMarkers.Any(m =>
            trimmed.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Classifies a tool by its registered name and optional arguments. Conservative for unknown names.</summary>
    public static ToolSideEffectLevel Classify(string? toolName, IDictionary<string, object>? args = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return ToolSideEffectLevel.ExternalSideEffect;

        if (string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
        {
            // run_command's side effect depends entirely on the command text: a read-only
            // diagnostic (Get-*, systeminfo, tasklist, git status, ...) is safe to re-run
            // and must not trip the replay guard; a mutating command stays ExternalSideEffect.
            string? command = null;
            if (args != null)
            {
                foreach (var kvp in args)
                {
                    if (string.Equals(kvp.Key, "command", StringComparison.OrdinalIgnoreCase))
                    {
                        command = kvp.Value?.ToString();
                        break;
                    }
                }
            }
            return IsReadOnlyCommand(command)
                ? ToolSideEffectLevel.ReadOnly
                : ToolSideEffectLevel.ExternalSideEffect;
        }

        if (string.Equals(toolName, "manage_process", StringComparison.OrdinalIgnoreCase))
        {
            if (args != null && (args.TryGetValue("action", out var actionObj) || args.TryGetValue("act", out actionObj)))
            {
                string? action = actionObj?.ToString();
                if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolSideEffectLevel.ReadOnly;
                }
                if (string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolSideEffectLevel.Destructive;
                }
            }
            return ToolSideEffectLevel.ExternalSideEffect;
        }

        if (ReadOnlyTools.Contains(toolName)) return ToolSideEffectLevel.ReadOnly;
        if (IdempotentTools.Contains(toolName)) return ToolSideEffectLevel.Idempotent;
        if (DestructiveTools.Contains(toolName)) return ToolSideEffectLevel.Destructive;
        return ToolSideEffectLevel.ExternalSideEffect;
    }

    /// <summary>
    /// True when a failed/ambiguous execution of this tool may be re-attempted automatically:
    /// only pure reads qualify. Writes (even idempotent ones) and commands need a reasoned
    /// retry, never an automatic one.
    /// </summary>
    public static bool IsSafeToAutoRetry(string? toolName)
        => Classify(toolName) == ToolSideEffectLevel.ReadOnly;
}

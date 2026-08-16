using System;
using System.IO;
using System.Reflection;

namespace Klydis.Core.Diagnostics;

/// <summary>
/// Structured crash/session diagnostics: session-start banners, clean-shutdown markers, and
/// full forensic exception dumps written to <c>fatal_error.txt</c> and mirrored into
/// <c>llama_native.log</c>. A dump includes the ENTIRE exception chain (all inner exceptions
/// and stack traces), process/version context, and the native log tail so a single file is
/// self-contained. Never throws — diagnostics must not crash the process that is already
/// failing.
/// </summary>
public static class CrashLog
{
    /// <summary>Writes a session-start banner so multi-session logs are separable.</summary>
    public static void WriteSessionBanner()
    {
        WriteBanner("SESSION START");
    }

    /// <summary>Writes a clean-exit marker so a crash is distinguishable from a shutdown.</summary>
    public static void WriteShutdown()
    {
        WriteBanner("CLEAN SHUTDOWN");
    }

    /// <summary>
    /// Writes a full forensic dump of an unhandled exception: timestamp, process info,
    /// the complete exception chain, and the native log tail. Never throws.
    /// </summary>
    /// <param name="ex">The exception to dump. May be an AggregateException (inner exceptions are included).</param>
    /// <param name="context">Where the failure surfaced (e.g. the handler name), for grep-ability.</param>
    public static void WriteFatal(Exception ex, string context)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Banner("UNHANDLED EXCEPTION (" + context + ")"));
            sb.AppendLine(ex.ToString());
            sb.AppendLine("Native log tail (last 4 KiB):");
            var tail = KlydisLog.ReadNativeLogTail(4096);
            sb.AppendLine(string.IsNullOrWhiteSpace(tail) ? "(empty)" : tail);
            sb.AppendLine("--- end of crash dump ---");
            KlydisLog.AppendFatalError(sb.ToString());

            // Compact marker in the native log so a frozen tail has a visible crash point
            // (the 2026-08-16 access violation simply STOPPED llama_native.log mid-line).
            KlydisLog.AppendNativeLog($"[FATAL] {context}: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}");
        }
        catch
        {
            // Crash logging must never throw.
        }
    }

    private static void WriteBanner(string title)
    {
        var line = Banner(title) + Environment.NewLine;
        KlydisLog.AppendFatalError(line);
        KlydisLog.AppendNativeLog(line);
        try { KlydisLog.AppendBounded(KlydisLog.AppLogPath, line); } catch { }
    }

    private static string Banner(string title)
    {
        var asm = Assembly.GetEntryAssembly()?.GetName();
        string version = asm?.Version?.ToString() ?? "?";
        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ===== {title} ===== PID={Environment.ProcessId} app={asm?.Name} v{version} runtime={Environment.Version} engine={ReadNativeEngineVersion()}";
    }

    private static string ReadNativeEngineVersion()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".klydis", "native", "version.json");
            if (!File.Exists(path)) return "bundled";

            var json = File.ReadAllText(path);
            var tagStart = json.IndexOf("\"tag\"", StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0) return "unknown";
            var colon = json.IndexOf(':', tagStart);
            var quote1 = json.IndexOf('"', colon);
            var quote2 = json.IndexOf('"', quote1 + 1);
            return quote1 < 0 || quote2 < 0 ? "unknown" : json.Substring(quote1 + 1, quote2 - quote1 - 1);
        }
        catch
        {
            return "unknown";
        }
    }
}

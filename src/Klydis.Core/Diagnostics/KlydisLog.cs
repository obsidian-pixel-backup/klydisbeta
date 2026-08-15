using System;
using System.IO;
using System.Threading;

namespace Klydis.Core.Diagnostics;

/// <summary>
/// Central file logging for Klydis with size-based rotation.
///
/// Logs live in <c>%LOCALAPPDATA%\Klydis\logs</c> (falling back to the working directory when
/// that path is unwritable), instead of accumulating unbounded files in the app working
/// directory. Each file rotates once it exceeds <see cref="MaxFileBytes"/>: the current file is
/// copied to <c>&lt;name&gt;.old</c> (overwriting a previous backup) and a fresh file is started.
/// The rotation size check is throttled to every 256 appends because the native llama.cpp log
/// callback is a very hot writer.
/// </summary>
public static class KlydisLog
{
    /// <summary>8 MB per log file before it rotates to <c>.old</c>.</summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>Rotation size check runs every N appends (the native log callback is hot).</summary>
    private const int RotationCheckInterval = 256;

    private static int _appendCounter;

    /// <summary>
    /// The directory logs are written to. Resolved once: <c>%LOCALAPPDATA%\Klydis\logs</c> when
    /// writable, otherwise the current working directory.
    /// </summary>
    public static string LogDirectory { get; } = ResolveLogDirectory();

    public static string NativeLogPath => Path.Combine(LogDirectory, "llama_native.log");
    public static string ChatDebugLogPath => Path.Combine(LogDirectory, "chat_debug.log");
    public static string HardLogPath => Path.Combine(LogDirectory, "hard_log.txt");
    public static string FatalErrorPath => Path.Combine(LogDirectory, "fatal_error.txt");

    /// <summary>
    /// Appends a line to the given path, rotating the file first when it exceeds
    /// <see cref="MaxFileBytes"/>. Never throws.
    /// </summary>
    public static void AppendBounded(string path, string line)
    {
        try
        {
            if (Interlocked.Increment(ref _appendCounter) % RotationCheckInterval == 0)
            {
                TryRotateIfOversized(path);
            }
            File.AppendAllText(path, line);
        }
        catch
        {
            // Logging must never crash the caller.
        }
    }

    /// <summary>Appends a line to <c>llama_native.log</c> with rotation. Never throws.</summary>
    public static void AppendNativeLog(string line) => AppendBounded(NativeLogPath, line);

    /// <summary>Appends a line to <c>chat_debug.log</c> with rotation. Never throws.</summary>
    public static void AppendChatDebug(string line) => AppendBounded(ChatDebugLogPath, line);

    /// <summary>Appends a line to <c>hard_log.txt</c> with rotation. Never throws.</summary>
    public static void AppendHardLog(string line) => AppendBounded(HardLogPath, line);

    /// <summary>Appends a line to <c>fatal_error.txt</c> with rotation. Never throws.</summary>
    public static void AppendFatalError(string line) => AppendBounded(FatalErrorPath, line);

    /// <summary>
    /// Reads the last <paramref name="maxBytes"/> of <c>llama_native.log</c>. Returns empty when
    /// the file is missing or unreadable.
    /// </summary>
    public static string ReadNativeLogTail(int maxBytes = 4096)
    {
        try
        {
            if (!File.Exists(NativeLogPath)) return string.Empty;

            using var fs = new FileStream(NativeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return string.Empty;

            long offset = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveLogDirectory()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                var dir = Path.Combine(baseDir, "Klydis", "logs");
                Directory.CreateDirectory(dir);

                // Probe writability so a locked/read-only app-data dir falls back to CWD.
                var probe = Path.Combine(dir, ".write_probe");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return dir;
            }
        }
        catch
        {
            // Fall through to the working directory.
        }
        return Directory.GetCurrentDirectory();
    }

    private static void TryRotateIfOversized(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= MaxFileBytes) return;

            var oldPath = path + ".old";
            try { File.Copy(path, oldPath, overwrite: true); } catch { /* keep current file on copy failure */ }
            try { File.Delete(path); } catch { /* rotation is best-effort */ }
        }
        catch
        {
            // Rotation must never crash the caller.
        }
    }
}

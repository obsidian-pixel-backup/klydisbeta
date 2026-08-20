using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Tasks;

namespace Klydis.Core.Processes;

/// <summary>
/// Options for starting a managed background process.
/// </summary>
public sealed record ProcessStartOptions
{
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public IDictionary<string, string>? EnvironmentVariables { get; init; }
    public string? ProcessId { get; init; }
    public int MaxRingBufferLines { get; init; } = 500;
    public int MaxRingBufferBytes { get; init; } = 64 * 1024; // 64 KB
    public bool UseShell { get; init; } = true;
    public string? WorkspaceRoot { get; init; }
}

/// <summary>
/// Snapshot report of a managed process state and output deltas.
/// </summary>
public sealed record ProcessStatusReport(
    string ProcessId,
    int? NativePid,
    string Command,
    string WorkingDirectory,
    bool IsRunning,
    int? ExitCode,
    TimeSpan Elapsed,
    DateTime StartTimeUtc,
    DateTime? ExitTimeUtc,
    string StdoutDelta,
    string StderrDelta,
    string FullStdout,
    string FullStderr,
    bool HasNewOutput
);

/// <summary>
/// Summary item for listing processes.
/// </summary>
public sealed record ProcessSummary(
    string ProcessId,
    int? NativePid,
    string Command,
    string WorkingDirectory,
    bool IsRunning,
    int? ExitCode,
    TimeSpan Elapsed,
    DateTime StartTimeUtc
);

/// <summary>
/// Contract for managing background process lifecycles.
/// </summary>
public interface IProcessManager : IDisposable
{
    ProcessStatusReport StartProcess(ProcessStartOptions options);
    ProcessStatusReport? GetStatus(string processId, bool includeFullOutput = false);
    bool SendInput(string processId, string input, bool addNewline = true);
    bool KillProcess(string processId, bool entireTree = true);
    IReadOnlyList<ProcessSummary> ListProcesses();
    bool RemoveProcess(string processId);
}

/// <summary>
/// Thread-safe bounded circular line buffer with monotonic sequence tracking for delta reads.
/// </summary>
public sealed class BoundedOutputRingBuffer(int maxLines = 500, int maxBytes = 64 * 1024)
{
    private readonly int _maxLines = maxLines > 0 ? maxLines : 500;
    private readonly int _maxBytes = maxBytes > 0 ? maxBytes : 65536;
    private readonly object _lock = new();

    private readonly LinkedList<BufferedLine> _lines = new();
    private int _currentByteCount = 0;
    private long _currentSequence = 0;
    private long _totalDroppedLines = 0;

    private readonly record struct BufferedLine(long SequenceId, string Text, int ByteLength, DateTime TimestampUtc);

    public void AppendLine(string line)
    {
        if (line == null) return;
        lock (_lock)
        {
            _currentSequence++;
            int lineBytes = Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
            var node = new BufferedLine(_currentSequence, line, lineBytes, DateTime.UtcNow);
            _lines.AddLast(node);
            _currentByteCount += lineBytes;

            // Enforce constraints (both line count and byte capacity)
            while (_lines.Count > _maxLines || _currentByteCount > _maxBytes)
            {
                if (_lines.First == null) break;
                var first = _lines.First.Value;
                _currentByteCount -= first.ByteLength;
                _lines.RemoveFirst();
                _totalDroppedLines++;
            }
        }
    }

    public string ReadDelta(ref long lastSequenceId, out bool hasNewLines)
    {
        lock (_lock)
        {
            if (_currentSequence == lastSequenceId)
            {
                hasNewLines = false;
                return string.Empty;
            }

            var sb = new StringBuilder();
            long oldestSequence = _lines.First?.Value.SequenceId ?? (_currentSequence + 1);

            if (lastSequenceId > 0 && lastSequenceId < oldestSequence)
            {
                long gap = oldestSequence - lastSequenceId - 1;
                if (gap > 0)
                {
                    sb.AppendLine($"[... {gap} lines dropped due to buffer capacity ...]");
                }
            }

            int count = 0;
            foreach (var line in _lines)
            {
                if (line.SequenceId > lastSequenceId)
                {
                    sb.AppendLine(line.Text);
                    count++;
                }
            }

            lastSequenceId = _currentSequence;
            hasNewLines = count > 0;
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }

    public string ReadAll(bool includeTruncationNotice = true)
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            if (includeTruncationNotice && _totalDroppedLines > 0)
            {
                sb.AppendLine($"[... {_totalDroppedLines} earlier lines truncated ...]");
            }
            foreach (var line in _lines)
            {
                sb.AppendLine(line.Text);
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }

    public int LineCount
    {
        get { lock (_lock) return _lines.Count; }
    }

    public long TotalDroppedLines
    {
        get { lock (_lock) return _totalDroppedLines; }
    }
}

/// <summary>
/// A managed background process instance with continuous output draining and input capabilities.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdinWriter;
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private readonly BoundedOutputRingBuffer _stdoutBuffer;
    private readonly BoundedOutputRingBuffer _stderrBuffer;

    private long _lastStdoutSequence = 0;
    private long _lastStderrSequence = 0;

    private DateTime? _exitTimeUtc;
    private int? _exitCode;
    private bool _disposed;

    public string ProcessId { get; }
    public string Command { get; }
    public string WorkingDirectory { get; }
    public DateTime StartTimeUtc { get; }
    public int? NativePid { get; }

    public BoundedOutputRingBuffer StdoutBuffer => _stdoutBuffer;
    public BoundedOutputRingBuffer StderrBuffer => _stderrBuffer;

    public ManagedProcess(
        string processId,
        Process process,
        string command,
        string workingDirectory,
        int maxLines,
        int maxBytes)
    {
        ProcessId = processId;
        _process = process ?? throw new ArgumentNullException(nameof(process));
        Command = command;
        WorkingDirectory = workingDirectory;
        StartTimeUtc = DateTime.UtcNow;

        int pid = 0;
        try { pid = process.Id; } catch { /* process may have already exited */ }
        NativePid = pid > 0 ? pid : null;

        _stdoutBuffer = new BoundedOutputRingBuffer(maxLines, maxBytes);
        _stderrBuffer = new BoundedOutputRingBuffer(maxLines, maxBytes);

        _stdinWriter = process.StandardInput;

        // Start background async pumping for stdout and stderr
        _ = Task.Run(() => PumpStreamAsync(process.StandardOutput, _stdoutBuffer, _cts.Token));
        _ = Task.Run(() => PumpStreamAsync(process.StandardError, _stderrBuffer, _cts.Token));
    }

    private async Task PumpStreamAsync(StreamReader reader, BoundedOutputRingBuffer buffer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                buffer.AppendLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Stream closed or process terminated
        }
    }

    public bool IsRunning
    {
        get
        {
            if (_disposed) return false;
            try
            {
                if (_process.HasExited)
                {
                    if (!_exitTimeUtc.HasValue)
                    {
                        try { _exitTimeUtc = _process.ExitTime.ToUniversalTime(); }
                        catch { _exitTimeUtc = DateTime.UtcNow; }
                    }
                    if (!_exitCode.HasValue)
                    {
                        try { _exitCode = _process.ExitCode; }
                        catch { _exitCode = -1; }
                    }
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            if (_exitCode.HasValue) return _exitCode;
            if (!IsRunning)
            {
                try { _exitCode = _process.ExitCode; }
                catch { }
            }
            return _exitCode;
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            var end = _exitTimeUtc ?? DateTime.UtcNow;
            return end >= StartTimeUtc ? end - StartTimeUtc : TimeSpan.Zero;
        }
    }

    public bool SendInput(string input, bool addNewline = true)
    {
        if (!IsRunning) return false;
        try
        {
            _stdinLock.Wait(1000);
            try
            {
                if (addNewline)
                {
                    _stdinWriter.WriteLine(input);
                }
                else
                {
                    _stdinWriter.Write(input);
                }
                _stdinWriter.Flush();
                return true;
            }
            finally
            {
                _stdinLock.Release();
            }
        }
        catch
        {
            return false;
        }
    }

    public bool Kill(bool entireTree = true)
    {
        if (!IsRunning) return true;

        try
        {
            _cts.Cancel();

            if (entireTree)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Fallback on Windows if Process.Kill fails
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && NativePid.HasValue)
                    {
                        try
                        {
                            using var killProc = Process.Start(new ProcessStartInfo
                            {
                                FileName = "taskkill.exe",
                                Arguments = $"/PID {NativePid.Value} /T /F",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                            killProc?.WaitForExit(2000);
                        }
                        catch { }
                    }
                }
            }
            else
            {
                _process.Kill();
            }

            _process.WaitForExit(3000);
            _exitTimeUtc = DateTime.UtcNow;
            try { _exitCode = _process.ExitCode; } catch { _exitCode = -1; }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public ProcessStatusReport GetStatusReport(bool includeFullOutput = false)
    {
        bool running = IsRunning;
        string stdoutDelta = _stdoutBuffer.ReadDelta(ref _lastStdoutSequence, out bool hasNewStdout);
        string stderrDelta = _stderrBuffer.ReadDelta(ref _lastStderrSequence, out bool hasNewStderr);

        string fullStdout = includeFullOutput ? _stdoutBuffer.ReadAll() : string.Empty;
        string fullStderr = includeFullOutput ? _stderrBuffer.ReadAll() : string.Empty;

        return new ProcessStatusReport(
            ProcessId: ProcessId,
            NativePid: NativePid,
            Command: Command,
            WorkingDirectory: WorkingDirectory,
            IsRunning: running,
            ExitCode: ExitCode,
            Elapsed: Elapsed,
            StartTimeUtc: StartTimeUtc,
            ExitTimeUtc: _exitTimeUtc,
            StdoutDelta: stdoutDelta,
            StderrDelta: stderrDelta,
            FullStdout: fullStdout,
            FullStderr: fullStderr,
            HasNewOutput: hasNewStdout || hasNewStderr
        );
    }

    public ProcessSummary GetSummary()
    {
        return new ProcessSummary(
            ProcessId: ProcessId,
            NativePid: NativePid,
            Command: Command,
            WorkingDirectory: WorkingDirectory,
            IsRunning: IsRunning,
            ExitCode: ExitCode,
            Elapsed: Elapsed,
            StartTimeUtc: StartTimeUtc
        );
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Kill(entireTree: true); } catch { }
        try { _cts.Cancel(); _cts.Dispose(); } catch { }
        try { _stdinLock.Dispose(); } catch { }
        try { _process.Dispose(); } catch { }
    }
}

/// <summary>
/// Singleton/Instance process manager that tracks, sandboxes, and supervises background processes.
/// </summary>
public sealed class ProcessManager : IProcessManager
{
    private static readonly Lazy<ProcessManager> _defaultInstance = new(() => new ProcessManager());
    public static ProcessManager Default => _defaultInstance.Value;

    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ProcessStatusReport StartProcess(ProcessStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Command))
            throw new ArgumentException("Command cannot be empty.", nameof(options));

        string workingDir = !string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? options.WorkingDirectory
            : (!string.IsNullOrWhiteSpace(options.WorkspaceRoot) ? options.WorkspaceRoot : Directory.GetCurrentDirectory());

        // Validate workspace containment if workspace root is defined
        if (!string.IsNullOrWhiteSpace(options.WorkspaceRoot))
        {
            if (!WorkspaceBoundaryValidator.IsWithinWorkspace(workingDir, options.WorkspaceRoot, out string resolved, out string canonicalRoot))
            {
                throw new InvalidOperationException(
                    $"working directory '{workingDir}' resolves to '{resolved}', which is OUTSIDE the task workspace root '{canonicalRoot}'. Background processes must execute within the workspace.");
            }
            workingDir = resolved;
        }
        else
        {
            workingDir = Path.GetFullPath(workingDir);
        }

        string processId = !string.IsNullOrWhiteSpace(options.ProcessId)
            ? options.ProcessId
            : $"proc_{Guid.NewGuid():N}"[..13];

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.FileName = "powershell.exe";
            psi.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{options.Command}\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.Arguments = $"-c \"{options.Command.Replace("\"", "\\\"")}\"";
        }

        if (options.EnvironmentVariables != null)
        {
            foreach (var kvp in options.EnvironmentVariables)
            {
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process for command: {options.Command}");
        }

        var managed = new ManagedProcess(
            processId,
            process,
            options.Command,
            workingDir,
            options.MaxRingBufferLines,
            options.MaxRingBufferBytes
        );

        _processes[processId] = managed;
        return managed.GetStatusReport(includeFullOutput: false);
    }

    public ProcessStatusReport? GetStatus(string processId, bool includeFullOutput = false)
    {
        if (string.IsNullOrWhiteSpace(processId)) return null;
        if (_processes.TryGetValue(processId, out var proc))
        {
            return proc.GetStatusReport(includeFullOutput);
        }
        return null;
    }

    public bool SendInput(string processId, string input, bool addNewline = true)
    {
        if (string.IsNullOrWhiteSpace(processId)) return false;
        if (_processes.TryGetValue(processId, out var proc))
        {
            return proc.SendInput(input, addNewline);
        }
        return false;
    }

    public bool KillProcess(string processId, bool entireTree = true)
    {
        if (string.IsNullOrWhiteSpace(processId)) return false;
        if (_processes.TryGetValue(processId, out var proc))
        {
            return proc.Kill(entireTree);
        }
        return false;
    }

    public IReadOnlyList<ProcessSummary> ListProcesses()
    {
        return _processes.Values.Select(p => p.GetSummary()).ToList();
    }

    public bool RemoveProcess(string processId)
    {
        if (string.IsNullOrWhiteSpace(processId)) return false;
        if (_processes.TryRemove(processId, out var proc))
        {
            proc.Dispose();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var proc in _processes.Values)
        {
            try { proc.Dispose(); } catch { }
        }
        _processes.Clear();
    }
}

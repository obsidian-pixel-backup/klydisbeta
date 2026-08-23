using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Tasks;
using Klydis.Core.Tracing;

namespace Klydis.Core.Processes;

/// <summary>
/// Request parameters for a terminal command execution.
/// </summary>
public sealed record TerminalCommandRequest(
    string Command,
    string Shell = "powershell",
    string? WorkingDirectory = null,
    int TimeoutSeconds = 60,
    string? SessionId = null,
    string? TaskId = null,
    string? RunId = null,
    string? StepId = null);

/// <summary>
/// Result of a completed terminal command execution.
/// </summary>
public sealed record TerminalExecutionResult(
    string ActionId,
    bool Success,
    int ExitCode,
    string Stdout,
    string Stderr,
    long DurationMs,
    bool TimedOut,
    string FormattedEnvelope,
    CommandErrorClassification ErrorClassification,
    string? RecoveryGuidance);

/// <summary>
/// Live chunk of terminal output (stdout or stderr).
/// </summary>
public sealed record TerminalChunk(
    string ActionId,
    string SessionId,
    string? TaskId,
    string Text,
    bool IsError,
    DateTime TimestampUtc);

/// <summary>
/// Authoritative terminal execution wrapper. Captures command intent before execution,
/// streams live output chunks, and captures duration, exit codes, and taxonomy.
/// </summary>
public sealed class TerminalExecutionService
{
    private readonly IExecutionEventStore? _eventStore;
    private readonly ILogger<TerminalExecutionService>? _logger;
    private long _actionCounter = 0;

    public event Action<TerminalChunk>? OutputChunkReceived;

    public TerminalExecutionService(
        IExecutionEventStore? eventStore = null,
        ILogger<TerminalExecutionService>? logger = null)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Executes a shell command with pre-execution event logging, streaming output chunk capture,
    /// and post-execution completion event emission.
    /// </summary>
    public async Task<TerminalExecutionResult> ExecuteAsync(
        TerminalCommandRequest request,
        Action<string>? onChunk = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string actionId = $"A-{Interlocked.Increment(ref _actionCounter):D6}";
        string shell = string.IsNullOrWhiteSpace(request.Shell) ? "powershell" : request.Shell.ToLowerInvariant();
        string workingDir = string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : request.WorkingDirectory;

        int timeoutMs = Math.Clamp(request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 60, 5, 600) * 1000;
        string command = request.Command.Trim();

        _logger?.LogInformation("[Terminal] Starting ActionId {ActionId}: `{Command}` in {WorkingDir} ({Shell})",
            actionId, command, workingDir, shell);

        // Pre-execution event capture
        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = request.SessionId ?? string.Empty,
            TaskId = request.TaskId,
            RunId = request.RunId,
            StepId = request.StepId,
            ActionId = actionId,
            Category = ExecutionEventCategory.TerminalStarted,
            ToolName = "run_command",
            Command = command,
            WorkingDirectory = workingDir,
            Title = $"$ {command}",
            Summary = $"Starting {shell} command in {Path.GetFileName(workingDir)}"
        });

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var psi = CreateProcessStartInfo(shell, command, workingDir);
        var sw = Stopwatch.StartNew();
        bool timedOut = false;
        int exitCode = -1;

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return CreateFailureResult(actionId, command, workingDir, shell, "Failed to start process", -1, 0, false);
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var readStdoutTask = Task.Run(async () =>
            {
                try
                {
                    char[] buffer = new char[1024];
                    int read;
                    while ((read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        string chunk = new string(buffer, 0, read);
                        stdoutBuilder.Append(chunk);
                        onChunk?.Invoke(chunk);
                        EmitChunk(actionId, request.SessionId, request.TaskId, chunk, isError: false);
                    }
                }
                catch { /* stream closed */ }
            });

            var readStderrTask = Task.Run(async () =>
            {
                try
                {
                    char[] buffer = new char[1024];
                    int read;
                    while ((read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        string chunk = new string(buffer, 0, read);
                        stderrBuilder.Append(chunk);
                        onChunk?.Invoke(chunk);
                        EmitChunk(actionId, request.SessionId, request.TaskId, chunk, isError: true);
                    }
                }
                catch { /* stream closed */ }
            });

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
                await Task.WhenAll(readStdoutTask, readStderrTask);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                    exitCode = -1;
                    stderrBuilder.AppendLine($"\n[Execution Timed Out after {timeoutMs / 1000}s]");
                }
                else
                {
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderrBuilder.AppendLine($"Process execution error: {ex.Message}");
        }
        finally
        {
            sw.Stop();
        }

        string rawStdout = stdoutBuilder.ToString();
        string rawStderr = stderrBuilder.ToString();

        if (!string.IsNullOrEmpty(rawStderr) && rawStderr.Contains("CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            rawStderr = Regex.Replace(rawStderr, @"#<\s*CLIXML[\s\S]*?</Objs>", "", RegexOptions.IgnoreCase).Trim();
        }

        var classification = timedOut
            ? CommandErrorClassification.Timeout
            : CommandExecution.ClassifyError(rawStderr, rawStdout, exitCode, false, false);
        string? guidance = CommandExecution.GetGuidance(classification);

        var envelope = new
        {
            action_id = actionId,
            status = exitCode == 0 ? "succeeded" : "failed",
            exit_code = exitCode,
            stdout = string.IsNullOrWhiteSpace(rawStdout) ? null : rawStdout.TrimEnd(),
            stderr = string.IsNullOrWhiteSpace(rawStderr) ? null : rawStderr.TrimEnd(),
            command = command,
            working_directory = workingDir,
            shell = shell,
            duration_ms = sw.ElapsedMilliseconds,
            timed_out = timedOut,
            failure_class = exitCode == 0 ? null : classification.ToString(),
            recovery_guidance = exitCode == 0 ? null : guidance
        };

        string envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        string formattedOutput = $"[COMMAND EXECUTION RESULT]\n```json\n{envelopeJson}\n```";
        if (!string.IsNullOrWhiteSpace(rawStdout))
        {
            formattedOutput += $"\n\n--- STDOUT ---\n{rawStdout.TrimEnd()}";
        }
        if (!string.IsNullOrWhiteSpace(rawStderr))
        {
            formattedOutput += $"\n\n--- STDERR ---\n{rawStderr.TrimEnd()}";
        }

        // Post-execution event capture
        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = request.SessionId ?? string.Empty,
            TaskId = request.TaskId,
            RunId = request.RunId,
            StepId = request.StepId,
            ActionId = actionId,
            Category = ExecutionEventCategory.TerminalCompleted,
            ToolName = "run_command",
            Command = command,
            WorkingDirectory = workingDir,
            ExitCode = exitCode,
            Success = exitCode == 0,
            DurationMs = sw.ElapsedMilliseconds,
            Title = $"Exit {exitCode}: {command}",
            Summary = exitCode == 0 ? $"Completed in {sw.ElapsedMilliseconds}ms" : $"Failed with exit code {exitCode}",
            Details = formattedOutput
        });

        return new TerminalExecutionResult(
            ActionId: actionId,
            Success: exitCode == 0,
            ExitCode: exitCode,
            Stdout: rawStdout,
            Stderr: rawStderr,
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: timedOut,
            FormattedEnvelope: formattedOutput,
            ErrorClassification: classification,
            RecoveryGuidance: guidance);
    }

    private void EmitChunk(string actionId, string? sessionId, string? taskId, string text, bool isError)
    {
        var chunk = new TerminalChunk(actionId, sessionId ?? string.Empty, taskId, text, isError, DateTime.UtcNow);
        OutputChunkReceived?.Invoke(chunk);

        _eventStore?.RecordEvent(new ExecutionEvent
        {
            SessionId = sessionId ?? string.Empty,
            TaskId = taskId,
            ActionId = actionId,
            Category = ExecutionEventCategory.TerminalOutput,
            ToolName = "run_command",
            Details = text,
            Success = !isError
        });
    }

    private static ProcessStartInfo CreateProcessStartInfo(string shell, string command, string workingDir)
    {
        if (shell == "cmd")
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        if (shell == "bash")
        {
            return new ProcessStartInfo
            {
                FileName = "bash.exe",
                Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        // Default: powershell
        var sanitizedCmd = Regex.Replace(command, @"(?<=\s|^)&&(?=\s|$)", ";");
        var matchStartFlags = Regex.Match(sanitizedCmd, @"^(?:start|Start-Process)\s+([a-zA-Z0-9_\-\.\:\\]+)\s+(--?[a-zA-Z0-9_\-\.]+.*)$", RegexOptions.IgnoreCase);
        if (matchStartFlags.Success)
        {
            string appName = matchStartFlags.Groups[1].Value;
            string rawArgs = matchStartFlags.Groups[2].Value;
            sanitizedCmd = $"Start-Process -FilePath \"{appName}\" -ArgumentList {rawArgs}";
        }
        var encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(sanitizedCmd));

        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCmd}",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static TerminalExecutionResult CreateFailureResult(
        string actionId, string command, string workingDir, string shell, string error, int exitCode, long durationMs, bool timedOut)
    {
        return new TerminalExecutionResult(
            ActionId: actionId,
            Success: false,
            ExitCode: exitCode,
            Stdout: string.Empty,
            Stderr: error,
            DurationMs: durationMs,
            TimedOut: timedOut,
            FormattedEnvelope: $"[COMMAND FAILED]: {error}",
            ErrorClassification: CommandErrorClassification.ToolUnavailable,
            RecoveryGuidance: "Verify executable path and system permissions.");
    }
}

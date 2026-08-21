using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

namespace Klydis.Core.Capabilities.Providers;

/// <summary>
/// Capability: shell.powershell
/// Structured fallback escape hatch executing PowerShell scripts.
/// </summary>
public sealed class ShellPowershellCapability : ICapability
{
    public string Id => "shell.powershell";
    public CapabilityDomain Domain => CapabilityDomain.Shell;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Executes a PowerShell script or cmdlet and returns structured JSON execution metrics, stdout, stderr, and exit code.",
        Parameters: new List<CapabilityParameter>
        {
            new("script", "string", "PowerShell script or command line to execute.", true),
            new("working_directory", "string", "Optional working directory.", false),
            new("timeout_ms", "integer", "Timeout in milliseconds (default: 30000).", false)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? script = request.GetParam<string>("script");
        if (string.IsNullOrWhiteSpace(script))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'script' is required."));
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string script = request.GetParam<string>("script")!;
            string? workingDir = request.GetParam<string>("working_directory");
            int timeoutMs = Math.Clamp(request.GetParam<int>("timeout_ms", 30000), 1000, 120000);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
            {
                psi.WorkingDirectory = workingDir;
            }

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            int pid = proc.Id;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            bool timedOut = false;
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { proc.Kill(true); } catch { }
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            sw.Stop();

            int exitCode = timedOut ? -1 : proc.ExitCode;
            var data = new
            {
                ExitCode = exitCode,
                Stdout = stdout.TrimEnd(),
                Stderr = stderr.TrimEnd(),
                DurationMs = (long)sw.Elapsed.TotalMilliseconds,
                TimedOut = timedOut,
                ProcessId = pid
            };

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?>
                {
                    ["ExitCode"] = exitCode,
                    ["DurationMs"] = data.DurationMs,
                    ["TimedOut"] = timedOut
                }
            );

            return exitCode == 0 && !timedOut
                ? CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence, exitCode: exitCode)
                : CapabilityResult.Failed(Id, string.IsNullOrWhiteSpace(stderr) ? $"Command exited with code {exitCode}" : stderr, sw.Elapsed, evidence, exitCode: exitCode);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        return Task.FromResult(result.Success
            ? VerificationResult.Verified("PowerShell command succeeded.")
            : VerificationResult.Failed("PowerShell command failed with non-zero exit code."));
    }
}

/// <summary>
/// Capability: shell.cmd
/// Structured fallback escape hatch executing CMD commands.
/// </summary>
public sealed class ShellCmdCapability : ICapability
{
    public string Id => "shell.cmd";
    public CapabilityDomain Domain => CapabilityDomain.Shell;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Executes a Windows Command Prompt (cmd.exe) command and returns structured JSON metrics, stdout, stderr, and exit code.",
        Parameters: new List<CapabilityParameter>
        {
            new("command", "string", "Command line to execute.", true),
            new("working_directory", "string", "Optional working directory.", false),
            new("timeout_ms", "integer", "Timeout in milliseconds (default: 30000).", false)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? cmd = request.GetParam<string>("command");
        if (string.IsNullOrWhiteSpace(cmd))
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'command' is required."));
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string command = request.GetParam<string>("command")!;
            string? workingDir = request.GetParam<string>("working_directory");
            int timeoutMs = Math.Clamp(request.GetParam<int>("timeout_ms", 30000), 1000, 120000);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
            {
                psi.WorkingDirectory = workingDir;
            }

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            int pid = proc.Id;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            bool timedOut = false;
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { proc.Kill(true); } catch { }
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            sw.Stop();

            int exitCode = timedOut ? -1 : proc.ExitCode;
            var data = new
            {
                ExitCode = exitCode,
                Stdout = stdout.TrimEnd(),
                Stderr = stderr.TrimEnd(),
                DurationMs = (long)sw.Elapsed.TotalMilliseconds,
                TimedOut = timedOut,
                ProcessId = pid
            };

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                CollectedAtUtc: DateTime.UtcNow
            );

            return exitCode == 0 && !timedOut
                ? CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence, exitCode: exitCode)
                : CapabilityResult.Failed(Id, string.IsNullOrWhiteSpace(stderr) ? $"Command exited with code {exitCode}" : stderr, sw.Elapsed, evidence, exitCode: exitCode);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        return Task.FromResult(result.Success
            ? VerificationResult.Verified("CMD command succeeded.")
            : VerificationResult.Failed("CMD command failed with non-zero exit code."));
    }
}

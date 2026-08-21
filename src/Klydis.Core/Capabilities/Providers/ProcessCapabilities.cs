using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

namespace Klydis.Core.Capabilities.Providers;

/// <summary>
/// Capability: process.start
/// Spawns a background or system process.
/// </summary>
public sealed class ProcessStartCapability : ICapability
{
    public string Id => "process.start";
    public CapabilityDomain Domain => CapabilityDomain.Process;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Starts an operating system process or application with optional arguments and working directory.",
        Parameters: new List<CapabilityParameter>
        {
            new("command", "string", "Executable name, command, or file path to run.", true),
            new("arguments", "string", "Command-line arguments string.", false),
            new("working_directory", "string", "Optional working directory.", false),
            new("use_shell_execute", "boolean", "Whether to use operating system shell execute (default: false).", false)
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

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string command = request.GetParam<string>("command")!;
            string? args = request.GetParam<string>("arguments");
            string? workingDir = request.GetParam<string>("working_directory");
            bool useShell = request.GetParam<bool>("use_shell_execute", false);

            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args ?? string.Empty,
                UseShellExecute = useShell,
                CreateNoWindow = !useShell,
                RedirectStandardOutput = !useShell,
                RedirectStandardError = !useShell
            };

            if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
            {
                psi.WorkingDirectory = workingDir;
            }

            var proc = Process.Start(psi);
            if (proc == null)
            {
                return Task.FromResult(CapabilityResult.Failed(Id, $"Failed to launch process '{command}'.", sw.Elapsed));
            }

            int pid = proc.Id;
            sw.Stop();

            var sideEffects = new List<SideEffect>
            {
                new(SideEffectKind.ProcessSpawned, pid.ToString(), $"Spawned process '{command}' (PID: {pid})")
            };

            var data = new
            {
                ProcessId = pid,
                Command = command,
                Arguments = args,
                HasExited = proc.HasExited,
                StartTimeUtc = DateTime.UtcNow
            };

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: $"Launched process '{command}' with PID {pid}",
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?> { ["ProcessId"] = pid }
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence, sideEffects));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null)
            return Task.FromResult(VerificationResult.Failed("Process start failed."));

        return Task.FromResult(VerificationResult.Verified("Process start verified."));
    }
}

/// <summary>
/// Capability: process.kill
/// Terminates a process by PID with postcondition verification.
/// </summary>
public sealed class ProcessKillCapability : ICapability
{
    public string Id => "process.kill";
    public CapabilityDomain Domain => CapabilityDomain.Process;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Terminates a running process by its Process ID (PID) and verifies termination.",
        Parameters: new List<CapabilityParameter>
        {
            new("pid", "integer", "Process ID to terminate.", true),
            new("entire_process_tree", "boolean", "Terminate child processes in the process tree (default: true).", false)
        },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        int pid = request.GetParam<int>("pid");
        if (pid <= 0)
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'pid' must be a positive integer."));

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                return Task.FromResult(PreconditionCheckResult.Failed($"Process with PID {pid} has already exited."));
            }
        }
        catch (ArgumentException)
        {
            return Task.FromResult(PreconditionCheckResult.Failed($"Process with PID {pid} was not found."));
        }

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            int pid = request.GetParam<int>("pid");
            bool entireTree = request.GetParam<bool>("entire_process_tree", true);

            using var proc = Process.GetProcessById(pid);
            string name = proc.ProcessName;
            proc.Kill(entireTree);

            await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(SideEffectKind.ProcessTerminated, pid.ToString(), $"Terminated process '{name}' (PID: {pid})")
            };

            var data = new { ProcessId = pid, ProcessName = name, Terminated = true };
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: $"Terminated process '{name}' (PID: {pid})",
                CollectedAtUtc: DateTime.UtcNow
            );

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence, sideEffects);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success)
            return Task.FromResult(VerificationResult.Failed("Process kill failed."));

        int pid = request.GetParam<int>("pid");
        try
        {
            using var check = Process.GetProcessById(pid);
            if (!check.HasExited)
            {
                return Task.FromResult(VerificationResult.Failed($"Postcondition failed: Process with PID {pid} is still running."));
            }
        }
        catch (ArgumentException)
        {
            // Process no longer exists — verified
        }

        var facts = new List<FactAssertion>();
        return Task.FromResult(VerificationResult.Verified($"Process {pid} termination verified.", facts));
    }
}

/// <summary>
/// Capability: process.inspect
/// Inspects runtime metrics, memory, CPU time, and thread count for a specific PID.
/// </summary>
public sealed class ProcessInspectCapability : ICapability
{
    public string Id => "process.inspect";
    public CapabilityDomain Domain => CapabilityDomain.Process;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Inspects memory working set, CPU execution time, thread count, responding state, and details of a running process PID.",
        Parameters: new List<CapabilityParameter>
        {
            new("pid", "integer", "Process ID to inspect.", true)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        int pid = request.GetParam<int>("pid");
        if (pid <= 0)
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'pid' must be a positive integer."));

        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            int pid = request.GetParam<int>("pid");
            using var proc = Process.GetProcessById(pid);

            var data = new
            {
                ProcessId = proc.Id,
                ProcessName = proc.ProcessName,
                Responding = proc.Responding,
                WorkingSetMb = Math.Round((double)proc.WorkingSet64 / (1024 * 1024), 2),
                PrivateMemoryMb = Math.Round((double)proc.PrivateMemorySize64 / (1024 * 1024), 2),
                VirtualMemoryMb = Math.Round((double)proc.VirtualMemorySize64 / (1024 * 1024), 2),
                ThreadCount = proc.Threads.Count,
                StartTimeUtc = TryGetStartTime(proc),
                TotalProcessorTimeSeconds = TryGetProcessorTime(proc)
            };

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Process inspect failed."));
        return Task.FromResult(VerificationResult.Verified("Process state inspected."));
    }

    private static DateTime? TryGetStartTime(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); } catch { return null; }
    }

    private static double? TryGetProcessorTime(Process p)
    {
        try { return p.TotalProcessorTime.TotalSeconds; } catch { return null; }
    }
}

/// <summary>
/// Capability: process.wait
/// Bounded wait for a process to exit.
/// </summary>
public sealed class ProcessWaitCapability : ICapability
{
    public string Id => "process.wait";
    public CapabilityDomain Domain => CapabilityDomain.Process;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Waits up to timeout_ms for a process to finish execution and returns its exit code.",
        Parameters: new List<CapabilityParameter>
        {
            new("pid", "integer", "Process ID to wait on.", true),
            new("timeout_ms", "integer", "Max wait timeout in milliseconds (default: 5000).", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        int pid = request.GetParam<int>("pid");
        if (pid <= 0)
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'pid' must be a positive integer."));
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            int pid = request.GetParam<int>("pid");
            int timeoutMs = Math.Clamp(request.GetParam<int>("timeout_ms", 5000), 500, 60000);

            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                return CapabilityResult.Succeeded(Id, new { ProcessId = pid, HasExited = true, ExitCode = proc.ExitCode }, sw.Elapsed);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
                sw.Stop();
                return CapabilityResult.Succeeded(Id, new { ProcessId = pid, HasExited = true, ExitCode = proc.ExitCode }, sw.Elapsed);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return CapabilityResult.Succeeded(Id, new { ProcessId = pid, HasExited = false, TimedOut = true }, sw.Elapsed);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Process wait failed."));
        return Task.FromResult(VerificationResult.Verified("Process wait completed."));
    }
}

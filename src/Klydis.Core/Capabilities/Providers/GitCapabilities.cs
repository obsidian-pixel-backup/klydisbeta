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
/// Capability: git.status
/// Inspects Git working tree status, active branch, and modified files.
/// </summary>
public sealed class GitStatusCapability : ICapability
{
    public string Id => "git.status";
    public CapabilityDomain Domain => CapabilityDomain.Development;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Returns Git branch, upstream status, and lists of modified, untracked, deleted, and staged files.",
        Parameters: new List<CapabilityParameter>
        {
            new("working_directory", "string", "Git repository working directory (default: current directory).", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? workingDir = request.GetParam<string>("working_directory");
            string root = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)
                ? Path.GetFullPath(workingDir)
                : Directory.GetCurrentDirectory();

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain=v1 -b",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return CapabilityResult.Failed(Id, "Failed to invoke git process.", sw.Elapsed);

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                return CapabilityResult.Failed(Id, $"git status returned exit code {proc.ExitCode}: {stderr}", sw.Elapsed);
            }

            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string branch = "HEAD";
            var modified = new List<string>();
            var untracked = new List<string>();
            var staged = new List<string>();
            var deleted = new List<string>();

            foreach (var line in lines)
            {
                if (line.StartsWith("## "))
                {
                    branch = line.Substring(3).Trim();
                }
                else if (line.Length >= 3)
                {
                    char x = line[0];
                    char y = line[1];
                    string path = line.Substring(3).Trim();

                    if (x is 'M' or 'A' or 'D' or 'R') staged.Add(path);
                    if (y == 'M') modified.Add(path);
                    if (y == 'D') deleted.Add(path);
                    if (x == '?' && y == '?') untracked.Add(path);
                }
            }

            sw.Stop();
            var data = new
            {
                Branch = branch,
                Clean = modified.Count == 0 && untracked.Count == 0 && staged.Count == 0 && deleted.Count == 0,
                ModifiedFiles = modified,
                UntrackedFiles = untracked,
                StagedFiles = staged,
                DeletedFiles = deleted
            };

            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: stdout,
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?> { ["Branch"] = branch, ["IsClean"] = data.Clean }
            );

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null) return Task.FromResult(VerificationResult.Failed("Git status inspection failed."));
        var facts = new List<FactAssertion>
        {
            new("git", "repository", "status", result.Data, TimeSpan.FromSeconds(30), Id)
        };
        return Task.FromResult(VerificationResult.Verified("Git status verified.", facts));
    }
}

/// <summary>
/// Capability: git.diff
/// Returns working tree or staged diff.
/// </summary>
public sealed class GitDiffCapability : ICapability
{
    public string Id => "git.diff";
    public CapabilityDomain Domain => CapabilityDomain.Development;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Returns unified diff format changes for working directory or staged index.",
        Parameters: new List<CapabilityParameter>
        {
            new("working_directory", "string", "Git repository working directory.", false),
            new("staged", "boolean", "If true, shows staged (--cached) diff (default: false).", false),
            new("file_path", "string", "Optional specific file to limit diff to.", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? workingDir = request.GetParam<string>("working_directory");
            bool staged = request.GetParam<bool>("staged", false);
            string? filePath = request.GetParam<string>("file_path");

            string root = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)
                ? Path.GetFullPath(workingDir)
                : Directory.GetCurrentDirectory();

            string args = "diff";
            if (staged) args += " --cached";
            if (!string.IsNullOrEmpty(filePath)) args += $" -- \"{filePath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return CapabilityResult.Failed(Id, "Failed to invoke git process.", sw.Elapsed);

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            sw.Stop();
            var data = new { Diff = stdout, Length = stdout.Length, IsEmpty = string.IsNullOrWhiteSpace(stdout) };
            var evidence = new CapabilityEvidence(Id, stdout, DateTime.UtcNow);

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(VerificationResult.Verified("Git diff executed."));
}

/// <summary>
/// Capability: git.log
/// Returns recent commit history.
/// </summary>
public sealed class GitLogCapability : ICapability
{
    public string Id => "git.log";
    public CapabilityDomain Domain => CapabilityDomain.Development;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Returns recent Git commit history with hashes, authors, commit dates, and commit messages.",
        Parameters: new List<CapabilityParameter>
        {
            new("working_directory", "string", "Git repository directory.", false),
            new("max_count", "integer", "Number of commits to return (default: 10).", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? workingDir = request.GetParam<string>("working_directory");
            int maxCount = Math.Clamp(request.GetParam<int>("max_count", 10), 1, 100);

            string root = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)
                ? Path.GetFullPath(workingDir)
                : Directory.GetCurrentDirectory();

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"log -n {maxCount} --pretty=format:\"%H|%an|%ad|%s\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return CapabilityResult.Failed(Id, "Failed to invoke git process.", sw.Elapsed);

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var commits = new List<object>();
            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length >= 4)
                {
                    commits.Add(new
                    {
                        CommitHash = parts[0],
                        Author = parts[1],
                        Date = parts[2],
                        Message = string.Join('|', parts.Skip(3))
                    });
                }
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(Id, stdout, DateTime.UtcNow);
            return CapabilityResult.Succeeded(Id, commits, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(VerificationResult.Verified("Git log executed."));
}

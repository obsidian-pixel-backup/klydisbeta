using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Tasks;

namespace Klydis.Core.Diagnostics;

/// <summary>
/// Result of an automated test execution suite.
/// </summary>
public sealed record TestRunSummary(
    bool Success,
    int TotalTests,
    int Passed,
    int Failed,
    int Skipped,
    TimeSpan Duration,
    string Output,
    IReadOnlyList<TestFailureDetail> Failures
);

/// <summary>
/// Detailed diagnostic for a failed test case.
/// </summary>
public sealed record TestFailureDetail(
    string TestName,
    string? Message,
    string? StackTrace,
    string? FilePath,
    int? LineNumber
);

/// <summary>
/// Discovers and executes unit tests across multi-language projects (.NET, TypeScript, Python, Rust, Go)
/// and parses test outcomes into structured verification evidence for the agent loop.
/// </summary>
public sealed class TestRunnerService
{
    private static readonly Regex DotNetSummaryRegex = new(
        @"Passed:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PytestSummaryRegex = new(
        @"(\d+)\s+passed(?:,\s*(\d+)\s+failed)?(?:,\s*(\d+)\s+skipped)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JestSummaryRegex = new(
        @"Tests:\s*(?:(\d+)\s*failed,\s*)?(?:(\d+)\s*passed,\s*)?(\d+)\s*total",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Executes test suite in the specified working directory and returns parsed summary.
    /// </summary>
    public async Task<TestRunSummary> RunTestsAsync(
        string workingDirectory,
        string? testFramework = null,
        string? testFilter = null,
        CancellationToken ct = default)
    {
        var (command, args) = DetermineTestCommand(workingDirectory, testFramework, testFilter);

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await ExecuteProcessAsync(command, args, workingDirectory, ct);
        sw.Stop();

        string fullOutput = stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr);
        var parsed = ParseTestOutput(fullOutput, exitCode, sw.Elapsed);
        return parsed;
    }

    /// <summary>
    /// Converts a test run summary into structured Evidence for the AgentSupervisor.
    /// </summary>
    public Evidence ToEvidence(TestRunSummary summary, string? stepId = null)
    {
        var kind = summary.Success ? EvidenceKind.TestPassed : EvidenceKind.TestFailed;
        string desc = summary.Success
            ? $"Tests passed: {summary.Passed}/{summary.TotalTests} assertions passed in {summary.Duration.TotalSeconds:F2}s."
            : $"Tests failed: {summary.Failed} failed, {summary.Passed} passed out of {summary.TotalTests} tests.";

        return new Evidence(
            Kind: kind,
            Description: desc,
            TimestampUtc: DateTime.UtcNow,
            Subject: "test_suite",
            StepId: stepId,
            ExitCode: summary.Success ? 0 : 1,
            Payload: summary.Output
        );
    }

    private static (string Command, string Args) DetermineTestCommand(string workingDir, string? framework, string? filter)
    {
        if (File.Exists(Path.Combine(workingDir, "package.json")))
        {
            string filterArg = string.IsNullOrEmpty(filter) ? "" : $" -- -t \"{filter}\"";
            return ("npm", $"test -- {filterArg}".Trim());
        }

        if (Directory.GetFiles(workingDir, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.GetFiles(workingDir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
        {
            string filterArg = string.IsNullOrEmpty(filter) ? "" : $" --filter \"{filter}\"";
            return ("dotnet", $"test --verbosity normal{filterArg}");
        }

        if (File.Exists(Path.Combine(workingDir, "pytest.ini")) || File.Exists(Path.Combine(workingDir, "setup.py")) || File.Exists(Path.Combine(workingDir, "requirements.txt")))
        {
            string filterArg = string.IsNullOrEmpty(filter) ? "" : $" -k \"{filter}\"";
            return ("pytest", filterArg.Trim());
        }

        if (File.Exists(Path.Combine(workingDir, "Cargo.toml")))
        {
            string filterArg = string.IsNullOrEmpty(filter) ? "" : $" {filter}";
            return ("cargo", $"test{filterArg}");
        }

        if (File.Exists(Path.Combine(workingDir, "go.mod")))
        {
            string filterArg = string.IsNullOrEmpty(filter) ? "./..." : $"-run \"{filter}\" ./...";
            return ("go", $"test {filterArg}");
        }

        // Default to dotnet test
        return ("dotnet", "test");
    }

    private static TestRunSummary ParseTestOutput(string output, int exitCode, TimeSpan duration)
    {
        var failures = new List<TestFailureDetail>();
        int passed = 0, failed = 0, skipped = 0, total = 0;

        // 1. Check .NET output
        var dotNetMatch = DotNetSummaryRegex.Match(output);
        if (dotNetMatch.Success)
        {
            int.TryParse(dotNetMatch.Groups[1].Value, out passed);
            int.TryParse(dotNetMatch.Groups[2].Value, out failed);
            int.TryParse(dotNetMatch.Groups[3].Value, out skipped);
            int.TryParse(dotNetMatch.Groups[4].Value, out total);
        }
        else
        {
            // 2. Check Pytest output
            var pytestMatch = PytestSummaryRegex.Match(output);
            if (pytestMatch.Success)
            {
                int.TryParse(pytestMatch.Groups[1].Value, out passed);
                if (pytestMatch.Groups[2].Success) int.TryParse(pytestMatch.Groups[2].Value, out failed);
                if (pytestMatch.Groups[3].Success) int.TryParse(pytestMatch.Groups[3].Value, out skipped);
                total = passed + failed + skipped;
            }
            else
            {
                // 3. Check Jest output
                var jestMatch = JestSummaryRegex.Match(output);
                if (jestMatch.Success)
                {
                    if (jestMatch.Groups[1].Success) int.TryParse(jestMatch.Groups[1].Value, out failed);
                    if (jestMatch.Groups[2].Success) int.TryParse(jestMatch.Groups[2].Value, out passed);
                    int.TryParse(jestMatch.Groups[3].Value, out total);
                }
                else
                {
                    // Fallback to exit code
                    if (exitCode == 0)
                    {
                        passed = 1;
                        total = 1;
                    }
                    else
                    {
                        failed = 1;
                        total = 1;
                    }
                }
            }
        }

        bool success = exitCode == 0 && failed == 0;
        return new TestRunSummary(success, total, passed, failed, skipped, duration, output, failures);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteProcessAsync(
        string command,
        string args,
        string workingDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Klydis.Core.Tasks;

/// <summary>
/// Result of evaluating a tool execution for structured verification evidence.
/// </summary>
public readonly record struct VerificationAnalysisResult(
    bool IsApplicable,
    Evidence? Evidence,
    string? FailureReason = null);

/// <summary>
/// Interface for typed evidence verification adapters.
/// Ensures commands like 'echo build' cannot masquerade as valid build evidence.
/// </summary>
public interface IEvidenceVerifier
{
    VerificationAnalysisResult Evaluate(
        string toolName,
        IDictionary<string, object>? arguments,
        string? output,
        int? exitCode,
        string? stepId = null,
        int workspaceVersion = 0);
}

/// <summary>
/// Verifier for .NET CLI builds and test runs ('dotnet build', 'dotnet test').
/// </summary>
public sealed class DotnetBuildVerifier : IEvidenceVerifier
{
    public VerificationAnalysisResult Evaluate(
        string toolName,
        IDictionary<string, object>? arguments,
        string? output,
        int? exitCode,
        string? stepId = null,
        int workspaceVersion = 0)
    {
        if (!string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
            return default;

        string? cmd = TryGetCommand(arguments);
        if (string.IsNullOrWhiteSpace(cmd))
            return default;

        bool isBuild = Regex.IsMatch(cmd, @"\bdotnet\s+build\b", RegexOptions.IgnoreCase);
        bool isTest = Regex.IsMatch(cmd, @"\bdotnet\s+test\b", RegexOptions.IgnoreCase);

        if (!isBuild && !isTest)
            return default;

        int code = exitCode ?? 0;
        bool hasErrorInOutput = output != null && (
            output.Contains(": error CS", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Failed!", StringComparison.OrdinalIgnoreCase));

        if (isBuild)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.BuildPassed : EvidenceKind.BuildFailed;
            var desc = passed ? "Dotnet build succeeded with zero errors" : "Dotnet build failed with compiler errors or non-zero exit code";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "dotnet_build", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        if (isTest)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.TestPassed : EvidenceKind.TestFailed;
            var desc = passed ? "Dotnet test suite passed" : "Dotnet test suite failed";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "dotnet_test", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        return default;
    }

    private static string? TryGetCommand(IDictionary<string, object>? args)
    {
        if (args == null) return null;
        if (args.TryGetValue("command", out var c) || args.TryGetValue("cmd", out c))
            return c?.ToString();
        return null;
    }
}

/// <summary>
/// Verifier for NPM and Node.js test and build commands.
/// </summary>
public sealed class NpmVerifier : IEvidenceVerifier
{
    public VerificationAnalysisResult Evaluate(
        string toolName,
        IDictionary<string, object>? arguments,
        string? output,
        int? exitCode,
        string? stepId = null,
        int workspaceVersion = 0)
    {
        if (!string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
            return default;

        string? cmd = TryGetCommand(arguments);
        if (string.IsNullOrWhiteSpace(cmd))
            return default;

        bool isBuild = Regex.IsMatch(cmd, @"\b(npm\s+run\s+build|npx\s+tsc|yarn\s+build)\b", RegexOptions.IgnoreCase);
        bool isTest = Regex.IsMatch(cmd, @"\b(npm\s+test|npx\s+jest|npx\s+vitest|yarn\s+test)\b", RegexOptions.IgnoreCase);

        if (!isBuild && !isTest)
            return default;

        int code = exitCode ?? 0;
        bool hasErrorInOutput = output != null && (
            output.Contains("ERR!", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("error TS", StringComparison.OrdinalIgnoreCase));

        if (isBuild)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.BuildPassed : EvidenceKind.BuildFailed;
            var desc = passed ? "NPM build succeeded" : "NPM build failed";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "npm_build", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        if (isTest)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.TestPassed : EvidenceKind.TestFailed;
            var desc = passed ? "NPM tests passed" : "NPM tests failed";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "npm_test", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        return default;
    }

    private static string? TryGetCommand(IDictionary<string, object>? args)
    {
        if (args == null) return null;
        if (args.TryGetValue("command", out var c) || args.TryGetValue("cmd", out c))
            return c?.ToString();
        return null;
    }
}

/// <summary>
/// Verifier for Python test runners ('pytest', 'python -m pytest', 'python -m unittest').
/// </summary>
public sealed class PytestVerifier : IEvidenceVerifier
{
    public VerificationAnalysisResult Evaluate(
        string toolName,
        IDictionary<string, object>? arguments,
        string? output,
        int? exitCode,
        string? stepId = null,
        int workspaceVersion = 0)
    {
        if (!string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
            return default;

        string? cmd = TryGetCommand(arguments);
        if (string.IsNullOrWhiteSpace(cmd))
            return default;

        bool isTest = Regex.IsMatch(cmd, @"\b(pytest|python\s+-m\s+pytest|python\s+-m\s+unittest|python3\s+-m\s+pytest)\b", RegexOptions.IgnoreCase);
        if (!isTest)
            return default;

        int code = exitCode ?? 0;
        bool hasErrorInOutput = output != null && (
            output.Contains("FAILED ", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("FAIL:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ERRORS:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Traceback (most recent call last):", StringComparison.OrdinalIgnoreCase));

        bool passed = code == 0 && !hasErrorInOutput;
        var kind = passed ? EvidenceKind.TestPassed : EvidenceKind.TestFailed;
        var desc = passed ? "Python/Pytest test suite passed" : "Python/Pytest test suite failed";

        return new VerificationAnalysisResult(true, new Evidence(
            kind, desc, DateTime.UtcNow, Subject: "pytest", ToolName: toolName,
            StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
    }

    private static string? TryGetCommand(IDictionary<string, object>? args)
    {
        if (args == null) return null;
        if (args.TryGetValue("command", out var c) || args.TryGetValue("cmd", out c))
            return c?.ToString();
        return null;
    }
}

/// <summary>
/// Verifier for Rust Cargo builds and test suites ('cargo build', 'cargo test', 'cargo check').
/// </summary>
public sealed class CargoVerifier : IEvidenceVerifier
{
    public VerificationAnalysisResult Evaluate(
        string toolName,
        IDictionary<string, object>? arguments,
        string? output,
        int? exitCode,
        string? stepId = null,
        int workspaceVersion = 0)
    {
        if (!string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
            return default;

        string? cmd = TryGetCommand(arguments);
        if (string.IsNullOrWhiteSpace(cmd))
            return default;

        bool isBuild = Regex.IsMatch(cmd, @"\b(cargo\s+build|cargo\s+check)\b", RegexOptions.IgnoreCase);
        bool isTest = Regex.IsMatch(cmd, @"\b(cargo\s+test)\b", RegexOptions.IgnoreCase);

        if (!isBuild && !isTest)
            return default;

        int code = exitCode ?? 0;
        bool hasErrorInOutput = output != null && (
            output.Contains("error[E", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("error: aborting due to", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("test result: FAILED", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains("FAILED", StringComparison.Ordinal) && !output.Contains("0 failed", StringComparison.OrdinalIgnoreCase)));

        if (isBuild)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.BuildPassed : EvidenceKind.BuildFailed;
            var desc = passed ? "Cargo build/check succeeded" : "Cargo build/check failed";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "cargo_build", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        if (isTest)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.TestPassed : EvidenceKind.TestFailed;
            var desc = passed ? "Cargo test suite passed" : "Cargo test suite failed";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "cargo_test", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        return default;
    }

    private static string? TryGetCommand(IDictionary<string, object>? args)
    {
        if (args == null) return null;
        if (args.TryGetValue("command", out var c) || args.TryGetValue("cmd", out c))
            return c?.ToString();
        return null;
    }
}

/// <summary>
/// Verifier for Go builds and test suites ('go build', 'go test', 'go vet').
/// </summary>
public sealed class GoVerifier : IEvidenceVerifier
{
    public VerificationAnalysisResult Evaluate(
        string toolName,
        IDictionary<string, object>? arguments,
        string? output,
        int? exitCode,
        string? stepId = null,
        int workspaceVersion = 0)
    {
        if (!string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
            return default;

        string? cmd = TryGetCommand(arguments);
        if (string.IsNullOrWhiteSpace(cmd))
            return default;

        bool isBuild = Regex.IsMatch(cmd, @"\b(go\s+build|go\s+vet)\b", RegexOptions.IgnoreCase);
        bool isTest = Regex.IsMatch(cmd, @"\b(go\s+test)\b", RegexOptions.IgnoreCase);

        if (!isBuild && !isTest)
            return default;

        int code = exitCode ?? 0;
        bool hasErrorInOutput = output != null && (
            output.Contains("FAIL\t", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("[build failed]", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("--- FAIL:", StringComparison.OrdinalIgnoreCase));

        if (isBuild)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.BuildPassed : EvidenceKind.BuildFailed;
            var desc = passed ? "Go build/vet succeeded" : "Go build/vet failed";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "go_build", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        if (isTest)
        {
            bool passed = code == 0 && !hasErrorInOutput;
            var kind = passed ? EvidenceKind.TestPassed : EvidenceKind.TestFailed;
            var desc = passed ? "Go test suite passed" : "Go test suite failed";
            return new VerificationAnalysisResult(true, new Evidence(
                kind, desc, DateTime.UtcNow, Subject: "go_test", ToolName: toolName,
                StepId: stepId, ExitCode: code, Payload: output, WorkspaceVersion: workspaceVersion));
        }

        return default;
    }

    private static string? TryGetCommand(IDictionary<string, object>? args)
    {
        if (args == null) return null;
        if (args.TryGetValue("command", out var c) || args.TryGetValue("cmd", out c))
            return c?.ToString();
        return null;
    }
}

/// <summary>
/// Composite engine applying all registered evidence verifiers.
/// </summary>
public static class EvidenceVerificationEngine
{
    private static readonly IEvidenceVerifier[] Verifiers = new IEvidenceVerifier[]
    {
        new DotnetBuildVerifier(),
        new NpmVerifier(),
        new PytestVerifier(),
        new CargoVerifier(),
        new GoVerifier()
    };

    public static Evidence? ClassifyToolEvidence(
        string toolName,
        IDictionary<string, object>? arguments,
        string? output,
        int? exitCode,
        string? stepId = null,
        int workspaceVersion = 0)
    {
        foreach (var verifier in Verifiers)
        {
            var res = verifier.Evaluate(toolName, arguments, output, exitCode, stepId, workspaceVersion);
            if (res.IsApplicable && res.Evidence != null)
            {
                return res.Evidence;
            }
        }

        // Fallback default for generic commands with exit code
        if (string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase))
        {
            int code = exitCode ?? 0;
            return new Evidence(
                code == 0 ? EvidenceKind.CommandSucceeded : EvidenceKind.CommandFailed,
                code == 0 ? "Command executed successfully" : $"Command failed with exit code {code}",
                DateTime.UtcNow,
                Subject: "generic_command",
                ToolName: toolName,
                StepId: stepId,
                ExitCode: code,
                WorkspaceVersion: workspaceVersion);
        }

        return null;
    }
}

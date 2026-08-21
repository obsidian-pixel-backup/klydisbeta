using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// A normalized fingerprint representing a specific tool execution failure or schema rejection.
/// Used to detect pathological failure-recovery loops and block repetitive failing strategies.
/// </summary>
public sealed record FailureFingerprint(
    string FingerprintHash,
    string ToolName,
    string ErrorCode,
    string ArgumentsHash,
    string? StepId,
    int AttemptCount,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc)
{
    /// <summary>
    /// A strategy is blocked when an identical failure fingerprint has been repeated 2 or more times.
    /// </summary>
    public bool IsStrategyBlocked => AttemptCount >= 2;
}

/// <summary>
/// Tracks failure fingerprints within an execution run and detects when a model is caught
/// in an endless failure-recovery loop.
/// </summary>
public sealed class FailureFingerprintTracker
{
    private readonly ConcurrentDictionary<string, FailureFingerprint> _fingerprints = new(StringComparer.Ordinal);
    private string? _lastFailureHash;

    /// <summary>
    /// Computes a stable hash for a tool failure.
    /// </summary>
    public static string ComputeHash(
        string toolName,
        string errorCode,
        IDictionary<string, object>? arguments,
        string? stepId)
    {
        var sortedArgs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (arguments != null)
        {
            foreach (var kvp in arguments)
            {
                var val = ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString() ?? string.Empty;
                sortedArgs[kvp.Key] = val.Trim();
            }
        }

        string rawKey = $"{toolName.Trim().ToLowerInvariant()}|{errorCode.Trim().ToUpperInvariant()}|{stepId ?? string.Empty}|{JsonSerializer.Serialize(sortedArgs)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Records a failure and returns the updated fingerprint.
    /// </summary>
    public FailureFingerprint RecordFailure(
        string toolName,
        string errorCode,
        IDictionary<string, object>? arguments,
        string? stepId)
    {
        string hash = ComputeHash(toolName, errorCode, arguments, stepId);
        _lastFailureHash = hash;

        return _fingerprints.AddOrUpdate(
            hash,
            _ => new FailureFingerprint(
                FingerprintHash: hash,
                ToolName: toolName,
                ErrorCode: errorCode,
                ArgumentsHash: hash,
                StepId: stepId,
                AttemptCount: 1,
                FirstSeenUtc: DateTime.UtcNow,
                LastSeenUtc: DateTime.UtcNow),
            (_, existing) => existing with
            {
                AttemptCount = existing.AttemptCount + 1,
                LastSeenUtc = DateTime.UtcNow
            });
    }

    /// <summary>
    /// Checks whether the current failure represents a blocked strategy.
    /// </summary>
    public bool IsCurrentStrategyBlocked()
    {
        if (_lastFailureHash == null) return false;
        return _fingerprints.TryGetValue(_lastFailureHash, out var fp) && fp.IsStrategyBlocked;
    }

    /// <summary>
    /// Generates compact feedback for the model when a strategy is blocked.
    /// </summary>
    public string FormatBlockedFeedback(string toolName, string errorCode)
    {
        return $@"[SYSTEM — STRATEGY BLOCKED]
The following invocation has failed repeatedly and is now BLOCKED from further retries:
  Tool: {toolName}
  Error: {errorCode}
  Attempts: 2
Do NOT repeat this identical tool call or arguments. Choose a materially different tool, inspect existing evidence, or declare the step blocked.";
    }

    /// <summary>
    /// Resets the tracker for a new run or clean step transition.
    /// </summary>
    public void Reset()
    {
        _fingerprints.Clear();
        _lastFailureHash = null;
    }
}

using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// Phase 24 — Concurrency and Ownership Lease.
/// Guarantees that exactly one runtime instance owns and advances an active run.
/// </summary>
public sealed record RunLease(
    string RunId,
    string OwnerId,
    DateTime AcquiredAtUtc,
    DateTime ExpiresAtUtc,
    int Version)
{
    /// <summary>Checks whether the lease has expired.</summary>
    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;

    /// <summary>Validates that the lease is currently held by the specified owner.</summary>
    public bool IsValidFor(string ownerId, DateTime utcNow)
        => !IsExpired(utcNow) && string.Equals(OwnerId, ownerId, StringComparison.Ordinal);

    /// <summary>Renews the lease with an updated expiry and bumped version.</summary>
    public RunLease Renew(TimeSpan duration, DateTime utcNow)
        => this with
        {
            AcquiredAtUtc = utcNow,
            ExpiresAtUtc = utcNow.Add(duration),
            Version = Version + 1
        };
}

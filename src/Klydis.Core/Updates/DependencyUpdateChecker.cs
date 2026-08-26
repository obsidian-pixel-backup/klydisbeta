using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Updates;

/// <summary>
/// Describes one dependency and whether a newer stable release is available on NuGet.
/// </summary>
public sealed record DependencyUpdateInfo(
    string PackageId,
    string InstalledVersion,
    string LatestVersion)
{
    /// <summary>
    /// True when the latest stable NuGet version is newer than the version Klydis pins.
    /// </summary>
    public bool IsUpdateAvailable => CompareVersions(LatestVersion, InstalledVersion) > 0;

    /// <summary>
    /// Numeric NuGet/SemVer-ish comparison (ignores prerelease/build suffixes beyond the first
    /// hyphen). Returns &gt;0 when a is newer than b, 0 when equal, &lt;0 when older.
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        // NuGet treats "1.0" as "1.0.0" and "1.0.0.0"; normalize to 4 numeric parts.
        static int[] Parts(string v)
        {
            var core = v.Split('-')[0];
            var nums = core.Split('.');
            var parts = new int[4];
            for (int i = 0; i < Math.Min(nums.Length, 4); i++)
            {
                if (!int.TryParse(nums[i], out var n))
                {
                    // Non-numeric component (e.g. "1.0-beta" handled above; unusual numeric tails):
                    // treat as 0 so we never throw on odd-but-valid NuGet versions.
                    n = 0;
                }
                parts[i] = n;
            }
            return parts;
        }

        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < 4; i++)
        {
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        }
        return 0;
    }
}

/// <summary>
/// Queries NuGet for newer stable releases of every package Klydis depends on, so the app can
/// notify the user when an updated dependency is available. Mirrors the native-engine update
/// pattern: throttled to once per day, state kept under %USERPROFILE%\.klydis\updates\.
/// </summary>
public static class DependencyUpdateChecker
{
    /// <summary>
    /// Every top-level NuGet package Klydis references, with the version pinned in the .csproj
    /// files. Keep this manifest in sync whenever a csproj version changes (the App bumps these
    /// in the same commits; the checker compares pinned vs latest-stable on NuGet).
    /// </summary>
    private static readonly (string Id, string Version)[] Manifest =
    {
        // Klydis.Core
        ("HtmlAgilityPack", "1.13.0"),
        ("ReverseMarkdown", "6.2.1"),
        ("LLamaSharp.Backend.Cpu", "0.27.0"),
        ("LLamaSharp.Backend.Cuda12", "0.27.0"),
        ("LLamaSharp.Backend.Vulkan", "0.27.0"),
        ("Microsoft.Data.Sqlite", "10.0.11"),
        ("Microsoft.Extensions.Logging.Abstractions", "10.0.11"),
        ("SQLitePCLRaw.lib.e_sqlite3", "3.53.3"),
        ("System.Management", "10.0.11"),
        ("Microsoft.Playwright", "1.62.0"),
        ("ManagedCode.Playwright.Stealth", "1.0.1"),
        // Klydis.App
        ("MdXaml", "1.27.0"),
        ("CommunityToolkit.Mvvm", "8.4.2"),
        ("Microsoft.Extensions.DependencyInjection", "10.0.11"),
        ("Microsoft.Extensions.Logging", "10.0.11"),
        ("Microsoft.Extensions.Logging.Console", "10.0.11"),
        // Klydis.McpServer
        ("Microsoft.Extensions.Hosting", "10.0.11"),
        ("ModelContextProtocol", "2.2.0"),
        // Tests
        ("Microsoft.NET.Test.Sdk", "18.9.0"),
        ("NUnit", "4.6.1"),
        ("NUnit3TestAdapter", "6.3.0")
    };

    private static readonly string UpdatesStateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".klydis", "updates"
    );

    private static readonly string LastCheckFilePath = Path.Combine(UpdatesStateDir, "dependency-check.json");

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static int _stateInitGuard;

    /// <summary>
    /// The manifest entries (package id + pinned version), exposed for diagnostics/tests.
    /// </summary>
    public static IReadOnlyList<(string Id, string Version)> Dependencies => Manifest;

    /// <summary>
    /// True when the daily NuGet check is due (no record yet, or older than 24h).
    /// </summary>
    public static bool IsCheckDue()
    {
        try
        {
            if (!File.Exists(LastCheckFilePath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(LastCheckFilePath));
            if (!doc.RootElement.TryGetProperty("lastCheckUtc", out var prop) ||
                !DateTime.TryParse(prop.GetString(), out var lastCheck))
            {
                return true;
            }
            return DateTime.UtcNow - lastCheck >= CheckInterval;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Records the last successful NuGet check so the next one is throttled to a day later.
    /// Only called after a successful lookup, so a network failure retries on next launch.
    /// </summary>
    public static void RecordCheck()
    {
        try
        {
            Interlocked.Exchange(ref _stateInitGuard, 1);
            Directory.CreateDirectory(UpdatesStateDir);
            var payload = JsonSerializer.Serialize(new { lastCheckUtc = DateTime.UtcNow.ToString("O") });
            File.WriteAllText(LastCheckFilePath, payload);
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// Checks NuGet for newer stable versions of every dependency. Throttled to once per day
    /// unless <paramref name="force"/> is true. Never throws: on any failure it returns an
    /// empty list so the background task can fail silently and retry next launch.
    /// </summary>
    /// <param name="force">When true, bypasses the daily throttle.</param>
    /// <param name="logger">Optional logger for telemetry.</param>
    /// <returns>All manifest entries with their latest stable NuGet version (whether newer or not),
    /// or an empty list when the check could not run.</returns>
    public static async Task<IReadOnlyList<DependencyUpdateInfo>> CheckForUpdatesAsync(bool force = false, ILogger? logger = null)
    {
        if (!force && !IsCheckDue())
        {
            logger?.LogInformation("Dependency update check not yet due (checked within the last 24h).");
            return Array.Empty<DependencyUpdateInfo>();
        }

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KlydisApp/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(20);

            var results = new DependencyUpdateInfo[Manifest.Length];

            // Query packages in parallel; NuGet flat-container index returns the full version list.
            await Parallel.ForEachAsync(
                Manifest.Select((entry, idx) => (entry, idx)),
                new ParallelOptions { MaxDegreeOfParallelism = 6 },
                async (item, ct) =>
                {
                    string? latest = await FetchLatestStableAsync(httpClient, item.entry.Id, ct);
                    results[item.idx] = new DependencyUpdateInfo(item.entry.Id, item.entry.Version, latest ?? item.entry.Version);
                });

            if (results.Any(r => r == null))
            {
                // At least one lookup failed; don't record the check so we retry soon.
                logger?.LogWarning("Dependency update check incomplete (some NuGet lookups failed).");
                return results.Where(r => r != null).ToList();
            }

            RecordCheck();
            var updates = results.Where(r => r.IsUpdateAvailable).ToList();
            logger?.LogInformation("Dependency update check complete: {Count} update(s) available.", updates.Count);
            foreach (var u in updates)
            {
                logger?.LogInformation("  {Id}: {Installed} -> {Latest}", u.PackageId, u.InstalledVersion, u.LatestVersion);
            }
            return results;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Dependency update check failed.");
            return Array.Empty<DependencyUpdateInfo>();
        }
    }

    /// <summary>
    /// Returns the newest stable (non-prerelease) version of a package from NuGet's flat
    /// container index, or null when the lookup fails.
    /// </summary>
    private static async Task<string?> FetchLatestStableAsync(HttpClient httpClient, string packageId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
            using var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? best = null;
            foreach (var v in versions.EnumerateArray())
            {
                var candidate = v.GetString();
                if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('-')) continue; // prerelease
                if (best == null || DependencyUpdateInfo.CompareVersions(candidate, best) > 0)
                {
                    best = candidate;
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }
}

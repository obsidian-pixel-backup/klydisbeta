using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Updates;

/// <summary>
/// Result of updating a single dependency's pinned version across the project files.
/// </summary>
public sealed record DependencyUpdateResult(
    string PackageId,
    string InstalledVersion,
    string UpdatedVersion,
    bool Succeeded,
    string? ProjectPath = null,
    string Message = "");

/// <summary>
/// Batch result of <see cref="DependencyUpdater.UpdateAllAsync"/>.
/// </summary>
public sealed record DependencyUpdateBatchResult(
    IReadOnlyList<DependencyUpdateResult> Results,
    IReadOnlyList<string> ChangedProjectFiles,
    string? RestoreOutput = null,
    int? RestoreExitCode = null);

/// <summary>
/// Applies the versions reported by <see cref="DependencyUpdateChecker"/>: rewrites the pinned
/// PackageReference versions in the repository's .csproj files, keeps the checker's manifest in
/// sync so the next daily check does not re-flag the same packages, and runs `dotnet restore` so
/// the new packages are actually fetched. The new versions take effect on the next launch
/// (`dotnet run` rebuilds from the updated project files).
/// </summary>
public static class DependencyUpdater
{
    /// <summary>Known repository project files, relative to the repo root.</summary>
    private static readonly string[] KnownProjectRelativePaths =
    {
        Path.Combine("src", "Klydis.Core", "Klydis.Core.csproj"),
        Path.Combine("src", "Klydis.App", "Klydis.App.csproj"),
        Path.Combine("Klydis.McpServer", "Klydis.McpServer.csproj"),
        Path.Combine("tests", "Klydis.Core.Tests", "Klydis.Core.Tests.csproj")
    };

    /// <summary>Checker source file whose manifest pins must stay in sync after an update.</summary>
    private const string CheckerSourceRelativePath = "src/Klydis.Core/Updates/DependencyUpdateChecker.cs";

    /// <summary>Solution file at the repo root, used as the restore target when present.</summary>
    private const string SolutionRelativePath = "KlydisBeta.sln";

    /// <summary>
    /// Discovers the repository's project files. Searches the given root (or the current working
    /// directory), then walks up from the application base directory so it works whether the app
    /// is launched from the repo root (Start-Klydis.bat) or from anywhere else in the tree.
    /// </summary>
    public static IReadOnlyList<string> FindProjectFiles(string? searchRoot = null)
    {
        var found = new List<string>();
        foreach (var root in GetSearchRoots(searchRoot))
        {
            foreach (var relative in KnownProjectRelativePaths)
            {
                var full = Path.Combine(root, relative);
                if (File.Exists(full))
                {
                    found.Add(Path.GetFullPath(full));
                }
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Locates DependencyUpdateChecker.cs (whose manifest pins must be kept in sync), or null when
    /// running from a published build without the source tree.
    /// </summary>
    public static string? FindCheckerSource(string? searchRoot = null)
    {
        foreach (var root in GetSearchRoots(searchRoot))
        {
            var full = Path.Combine(root, CheckerSourceRelativePath);
            if (File.Exists(full))
            {
                return Path.GetFullPath(full);
            }
        }
        return null;
    }

    /// <summary>
    /// Rewrites the pinned <c>Version</c> attribute of a package reference in a project file.
    /// Returns true when the file was changed; false when the package is not referenced (or the
    /// file cannot be read/written).
    /// </summary>
    public static bool UpdateProjectFile(string projectPath, string packageId, string newVersion)
    {
        string text;
        try
        {
            text = File.ReadAllText(projectPath);
        }
        catch
        {
            return false;
        }

        var id = Regex.Escape(packageId);
        // Standard layout: <PackageReference Include="Id" Version="x" /> and the reversed order.
        string[] patterns =
        {
            $@"(<PackageReference\s+Include=""{id}""\s+Version="")[^""]*("")",
            $@"(<PackageReference\s+Version="")[^""]*(""\s+Include=""{id}"")"
        };

        foreach (var pattern in patterns)
        {
            if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) continue;
            // ${1}...${2} (braced) so a numeric version like "2.0.0" is never parsed as part
            // of a group reference ($12 would be read as group 12).
            var updated = Regex.Replace(text, pattern, $"${{1}}{newVersion}${{2}}", RegexOptions.IgnoreCase);
            try
            {
                File.WriteAllText(projectPath, updated);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Keeps the <see cref="DependencyUpdateChecker"/> manifest pin in sync with the new version.
    /// Returns true when the source file was changed.
    /// </summary>
    public static bool UpdateCheckerManifest(string checkerSourcePath, string packageId, string newVersion)
    {
        string text;
        try
        {
            text = File.ReadAllText(checkerSourcePath);
        }
        catch
        {
            return false;
        }

        var id = Regex.Escape(packageId);
        var pattern = $@"(\(""{id}"",\s*"")[^""]*(""\))";
        if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) return false;

        // ${1}...${2} (braced) so a numeric version is never parsed as part of a group reference.
        var updated = Regex.Replace(text, pattern, $"${{1}}{newVersion}${{2}}", RegexOptions.IgnoreCase);
        try
        {
            File.WriteAllText(checkerSourcePath, updated);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Updates every dependency to its latest version: rewrites the pinned versions in the
    /// project files (and the checker manifest), then runs `dotnet restore` so the new packages
    /// are fetched. Never throws — per-package failures are reported in the results.
    /// </summary>
    public static async Task<DependencyUpdateBatchResult> UpdateAllAsync(
        IReadOnlyList<DependencyUpdateInfo> updates,
        ILogger? logger = null,
        bool runRestore = true,
        string? searchRoot = null,
        CancellationToken ct = default)
    {
        var projectFiles = FindProjectFiles(searchRoot);
        var checkerSource = FindCheckerSource(searchRoot);
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DependencyUpdateResult>();

        foreach (var update in updates)
        {
            ct.ThrowIfCancellationRequested();

            string? targetProject = null;
            foreach (var project in projectFiles)
            {
                string content;
                try
                {
                    content = File.ReadAllText(project);
                }
                catch
                {
                    continue;
                }

                if (content.Contains($"Include=\"{update.PackageId}\"", StringComparison.OrdinalIgnoreCase))
                {
                    targetProject = project;
                    break;
                }
            }

            bool projectUpdated = false;
            if (targetProject != null)
            {
                projectUpdated = UpdateProjectFile(targetProject, update.PackageId, update.LatestVersion);
                if (projectUpdated)
                {
                    changedFiles.Add(targetProject);
                    logger?.LogInformation("Updated {Package} {Old} -> {New} in {Project}",
                        update.PackageId, update.InstalledVersion, update.LatestVersion, Path.GetFileName(targetProject));
                }
            }

            bool manifestUpdated = false;
            if (checkerSource != null)
            {
                manifestUpdated = UpdateCheckerManifest(checkerSource, update.PackageId, update.LatestVersion);
                if (manifestUpdated)
                {
                    changedFiles.Add(checkerSource);
                }
            }

            if (!projectUpdated && !manifestUpdated)
            {
                results.Add(new DependencyUpdateResult(
                    update.PackageId, update.InstalledVersion, update.LatestVersion, false,
                    Message: "Package is not referenced in any project file or the checker manifest."));
                continue;
            }

            var where = new List<string>();
            if (projectUpdated && targetProject != null) where.Add(Path.GetFileName(targetProject));
            if (manifestUpdated) where.Add("checker manifest");
            results.Add(new DependencyUpdateResult(
                update.PackageId, update.InstalledVersion, update.LatestVersion, true,
                ProjectPath: targetProject, Message: string.Join(" + ", where)));
        }

        string? restoreOutput = null;
        int? restoreExitCode = null;
        if (runRestore && changedFiles.Count > 0)
        {
            (restoreExitCode, restoreOutput) = await RunDotnetRestoreAsync(changedFiles, logger, ct).ConfigureAwait(false);
            if (restoreExitCode == 0)
            {
                logger?.LogInformation("Dependency restore completed after updating {Count} package(s).", results.Count(r => r.Succeeded));
            }
            else
            {
                logger?.LogWarning("Dependency restore finished with exit code {ExitCode}: {Output}",
                    restoreExitCode, restoreOutput);
            }
        }

        return new DependencyUpdateBatchResult(
            results,
            changedFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            restoreOutput,
            restoreExitCode);
    }

    private static IReadOnlyList<string> GetSearchRoots(string? searchRoot)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(searchRoot))
        {
            try { roots.Add(Path.GetFullPath(searchRoot)); } catch { }
        }
        else
        {
            try { roots.Add(Directory.GetCurrentDirectory()); } catch { }

            // Walk up from the app base directory to the filesystem root.
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null)
                {
                    roots.Add(dir.FullName);
                    dir = dir.Parent;
                }
            }
            catch
            {
                // Ignore and rely on the current directory root.
            }
        }
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<(int? ExitCode, string Output)> RunDotnetRestoreAsync(
        IEnumerable<string> changedFiles,
        ILogger? logger,
        CancellationToken ct)
    {
        // Prefer restoring the solution so every project (Core, App, McpServer, tests) is
        // resolved in one pass; fall back to each changed .csproj individually.
        var targets = new List<string>();
        string? solution = null;
        foreach (var root in GetSearchRoots(null))
        {
            var candidate = Path.Combine(root, SolutionRelativePath);
            if (File.Exists(candidate))
            {
                solution = candidate;
                break;
            }
        }
        if (solution != null)
        {
            targets.Add(solution);
        }
        else
        {
            targets.AddRange(changedFiles.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
        }

        if (targets.Count == 0) return (null, string.Empty);

        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("restore");
            psi.ArgumentList.Add(targets[0]);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return (null, "Failed to start 'dotnet restore'.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (null, "Restore timed out after 3 minutes.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to run 'dotnet restore' after dependency update.");
            return (null, $"Failed to run 'dotnet restore': {ex.Message}");
        }
    }
}

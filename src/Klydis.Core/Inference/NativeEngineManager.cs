using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference;

/// <summary>
/// Manages local native llama.dll execution engines and override directories in %USERPROFILE%\.klydis\native\.
/// Ensures in-process GGUF inference can utilize updated native C++ builds without external 3rd-party services.
/// </summary>
public static class NativeEngineManager
{
    private static readonly string KlydisHomeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".klydis"
    );

    /// <summary>
    /// Path to user native DLL override folder (%USERPROFILE%\.klydis\native).
    /// </summary>
    public static string CustomNativeDirectory => Path.Combine(KlydisHomeDir, "native");

    /// <summary>
    /// Checks whether custom/updated native llama.dll binaries are present in the override folder.
    /// </summary>
    public static bool HasCustomNativeEngine()
    {
        try
        {
            if (!Directory.Exists(CustomNativeDirectory))
                return false;

            string nativeDll = Path.Combine(CustomNativeDirectory, "llama.dll");
            return File.Exists(nativeDll) && new FileInfo(nativeDll).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deploys custom native binaries from %USERPROFILE%\.klydis\native\ to the target application directory if available.
    /// </summary>
    /// <param name="targetDirectory">Target directory (defaults to AppDomain BaseDirectory).</param>
    /// <param name="logger">Optional logger for telemetry.</param>
    /// <returns>Number of native DLL files copied to the execution root.</returns>
    public static int SyncCustomNativeEngine(string? targetDirectory = null, ILogger? logger = null)
    {
        targetDirectory ??= AppDomain.CurrentDomain.BaseDirectory;
        int copiedCount = 0;

        try
        {
            if (!HasCustomNativeEngine())
            {
                logger?.LogDebug("No custom native engine override found in {CustomNativeDirectory}", CustomNativeDirectory);
                return 0;
            }

            logger?.LogInformation("Deploying custom in-process native engine from {CustomNativeDirectory} to {TargetDirectory}", CustomNativeDirectory, targetDirectory);

            foreach (var file in Directory.GetFiles(CustomNativeDirectory, "*.*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                var destPath = Path.Combine(targetDirectory, fileName);

                try
                {
                    var srcInfo = new FileInfo(file);
                    var destInfo = File.Exists(destPath) ? new FileInfo(destPath) : null;

                    if (destInfo == null || destInfo.Length != srcInfo.Length || destInfo.LastWriteTimeUtc < srcInfo.LastWriteTimeUtc)
                    {
                        File.Copy(file, destPath, overwrite: true);
                        copiedCount++;
                        logger?.LogInformation("Copied custom native binary: {FileName}", fileName);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Could not overwrite native file {FileName} (file may already be loaded in memory)", fileName);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error synchronizing custom native engine binaries");
        }

        return copiedCount;
    }

    /// <summary>
    /// Ensures the custom native directory structure exists.
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        try
        {
            if (!Directory.Exists(CustomNativeDirectory))
            {
                Directory.CreateDirectory(CustomNativeDirectory);
            }
        }
        catch
        {
            // Non-critical
        }
    }
}

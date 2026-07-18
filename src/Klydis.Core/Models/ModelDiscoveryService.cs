using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Models;

/// <summary>
/// Scans the system for existing GGUF models.
/// </summary>
public class ModelDiscoveryService : IDisposable
{
    private readonly ILogger<ModelDiscoveryService> _logger;
    private readonly FileSystemWatcher? _modelsWatcher;
    private readonly string _defaultModelsPath;
    
    /// <summary>
    /// Event fired when a new model file is discovered.
    /// </summary>
    public event Action<string>? ModelDiscovered;

    /// <summary>
    /// Configurable paths to scan.
    /// </summary>
    public List<string> ScanPaths { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDiscoveryService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ModelDiscoveryService(ILogger<ModelDiscoveryService> logger)
    {
        _logger = logger;
        
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _defaultModelsPath = Path.Combine(userHome, ".klydis", "models");
        
        // Setup default scan paths
        ScanPaths.Add(_defaultModelsPath);
        ScanPaths.Add(userHome); // Implicitly covers Documents, Downloads, Desktop when scanned recursively

        Directory.CreateDirectory(_defaultModelsPath);

        // Setup file watcher for hot-reload on the default models directory
        try
        {
            _modelsWatcher = new FileSystemWatcher(_defaultModelsPath, "*.gguf")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size
            };
            
            _modelsWatcher.Created += OnFileCreated;
            _modelsWatcher.Renamed += OnFileRenamed;
            _modelsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize FileSystemWatcher for {Path}", _defaultModelsPath);
        }
    }

    /// <summary>
    /// Scans the configured paths for GGUF files.
    /// </summary>
    /// <param name="maxDepth">Maximum directory depth to scan.</param>
    /// <returns>A list of discovered file paths.</returns>
    public Task<List<string>> ScanForModelsAsync(int maxDepth = 3)
    {
        _logger.LogInformation("Scanning system for GGUF models...");
        var discoveredFiles = new ConcurrentBag<string>();

        Parallel.ForEach(ScanPaths, path =>
        {
            if (Directory.Exists(path))
            {
                try
                {
                    ScanDirectoryRecursive(path, maxDepth, 0, discoveredFiles);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error scanning path {Path}", path);
                }
            }
        });

        var result = discoveredFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _logger.LogInformation("Scan complete. Found {Count} GGUF files.", result.Count);
        return Task.FromResult(result);
    }

    private void ScanDirectoryRecursive(string path, int maxDepth, int currentDepth, ConcurrentBag<string> discoveredFiles)
    {
        if (currentDepth > maxDepth) return;

        try
        {
            var files = Directory.EnumerateFiles(path, "*.gguf", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                discoveredFiles.Add(file);
                ModelDiscovered?.Invoke(file);
            }

            var directories = Directory.EnumerateDirectories(path);
            foreach (var dir in directories)
            {
                // Skip common system or hidden directories to avoid UnauthAccessException and loops
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.Attributes.HasFlag(FileAttributes.Hidden) || dirInfo.Attributes.HasFlag(FileAttributes.System))
                    continue;

                ScanDirectoryRecursive(dir, maxDepth, currentDepth + 1, discoveredFiles);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore access denied errors
        }
        catch (PathTooLongException)
        {
            // Ignore paths that are too long
        }
        catch (IOException)
        {
            // Ignore other I/O errors (e.g. locked files)
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("New model file detected via watcher: {Path}", e.FullPath);
        ModelDiscovered?.Invoke(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (e.FullPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Model file renamed/moved into directory: {Path}", e.FullPath);
            ModelDiscovered?.Invoke(e.FullPath);
        }
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_modelsWatcher != null)
        {
            _modelsWatcher.Created -= OnFileCreated;
            _modelsWatcher.Renamed -= OnFileRenamed;
            _modelsWatcher.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

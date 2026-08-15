using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Models;

/// <summary>
/// Manages the registry of installed and discovered models.
/// </summary>
public class ModelRegistry
{
    private readonly ILogger<ModelRegistry> _logger;
    private readonly string _registryFilePath;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    private ConcurrentDictionary<string, ModelInfo> _models = new();

    /// <summary>
    /// Event fired when the model registry changes.
    /// </summary>
    public event Action? RegistryChanged;

    /// <summary>
    /// Gets the directory where models are stored.
    /// </summary>
    public string ModelsDirectory => _modelsDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelRegistry"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ModelRegistry(ILogger<ModelRegistry> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".klydis", "models"))
    {
    }

    /// <summary>
    /// Test seam: points the registry at an arbitrary models directory instead of the user's
    /// real ~/.klydis/models.
    /// </summary>
    internal ModelRegistry(ILogger<ModelRegistry> logger, string modelsDirectory)
    {
        _logger = logger;
        _modelsDirectory = modelsDirectory;
        _registryFilePath = Path.Combine(_modelsDirectory, "registry.json");
        
        Directory.CreateDirectory(_modelsDirectory);
    }

    /// <summary>
    /// Loads the registry from disk asynchronously.
    /// </summary>
    public async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_registryFilePath))
            {
                _models = new ConcurrentDictionary<string, ModelInfo>();
                return;
            }

            string json = await File.ReadAllTextAsync(_registryFilePath);
            var models = JsonSerializer.Deserialize<List<ModelInfo>>(json);
            
            if (models != null)
            {
                var dict = new ConcurrentDictionary<string, ModelInfo>();
                foreach (var m in models)
                {
                    if (m != null && !string.IsNullOrEmpty(m.Id))
                    {
                        dict[m.Id] = m;
                    }
                }
                _models = dict;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model registry.");
            _models = new ConcurrentDictionary<string, ModelInfo>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves the current registry state to disk asynchronously.
    /// </summary>
    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await SaveInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveInternalAsync()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_models.Values, options);
            await File.WriteAllTextAsync(_registryFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save model registry.");
        }
    }

    public async Task AddActiveDownloadAsync(ActiveDownloadRecord record)
    {
        await _lock.WaitAsync();
        try
        {
            string path = Path.Combine(_modelsDirectory, "active_downloads.json");
            var list = await GetActiveDownloadsInternalAsync();
            list.RemoveAll(d => d.FileName == record.FileName && d.RepoId == record.RepoId);
            list.Add(record);
            
            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list, options));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add active download record for {FileName}", record.FileName);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveActiveDownloadAsync(string repoId, string fileName)
    {
        await _lock.WaitAsync();
        try
        {
            string path = Path.Combine(_modelsDirectory, "active_downloads.json");
            var list = await GetActiveDownloadsInternalAsync();
            int removed = list.RemoveAll(d => d.FileName == fileName && d.RepoId == repoId);
            
            if (removed > 0)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list, options));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove active download record for {FileName}", fileName);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ActiveDownloadRecord>> GetActiveDownloadsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await GetActiveDownloadsInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Resume records older than this are dropped: a download that has been failing for a week
    /// (e.g. the file was removed from the repo) would otherwise be retried on every app start
    /// forever. The partial .download file is left in place, so a manual re-download still
    /// resumes from it.
    /// </summary>
    private static readonly TimeSpan ActiveDownloadMaxAge = TimeSpan.FromDays(7);

    private async Task<List<ActiveDownloadRecord>> GetActiveDownloadsInternalAsync()
    {
        string path = Path.Combine(_modelsDirectory, "active_downloads.json");
        if (!File.Exists(path)) return new List<ActiveDownloadRecord>();

        try
        {
            string json = await File.ReadAllTextAsync(path);
            var list = JsonSerializer.Deserialize<List<ActiveDownloadRecord>>(json) ?? new List<ActiveDownloadRecord>();

            var fresh = list
                .Where(d => d.StartedAt >= DateTime.UtcNow - ActiveDownloadMaxAge)
                .ToList();
            if (fresh.Count != list.Count)
            {
                _logger.LogInformation("Dropping {Count} stale active-download record(s) older than {Days} days.",
                    list.Count - fresh.Count, ActiveDownloadMaxAge.TotalDays);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true }));
            }
            return fresh;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read active downloads.");
            return new List<ActiveDownloadRecord>();
        }
    }

    /// <summary>
    /// Gets all registered models.
    /// </summary>
    public IEnumerable<ModelInfo> GetAllModels() => _models.Values;

    /// <summary>
    /// Gets a model by its ID.
    /// </summary>
    /// <param name="id">The model ID.</param>
    /// <returns>The model info if found, otherwise null.</returns>
    public ModelInfo? GetModel(string id)
    {
        _models.TryGetValue(id, out var model);
        return model;
    }

    /// <summary>
    /// Adds or updates a model in the registry.
    /// </summary>
    /// <param name="model">The model to add or update.</param>
    public async Task UpsertModelAsync(ModelInfo model)
    {
        _models[model.Id] = model;
        await SaveAsync();
        RegistryChanged?.Invoke();
    }

    /// <summary>
    /// Removes a model from the registry.
    /// </summary>
    /// <param name="id">The ID of the model to remove.</param>
    public async Task RemoveModelAsync(string id)
    {
        if (_models.TryRemove(id, out _))
        {
            await SaveAsync();
            RegistryChanged?.Invoke();
        }
    }

    /// <summary>
    /// Updates the role of a model.
    /// </summary>
    /// <param name="id">The model ID.</param>
    /// <param name="role">The new role.</param>
    public async Task UpdateModelRoleAsync(string id, string? role)
    {
        if (_models.TryGetValue(id, out var model))
        {
            _models[id] = model with { Role = role };
            await SaveAsync();
            RegistryChanged?.Invoke();
        }
    }

    /// <summary>
    /// Scans the models directory, registers new .gguf files, and removes entries for missing files.
    /// </summary>
    public async Task SyncWithDiskAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _logger.LogInformation("Syncing model registry with disk...");

            bool changed = false;

            // Deduplicate existing entries by FilePath (case-insensitive)
            var duplicates = _models.Values
                .GroupBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicates)
            {
                // Keep the best model in the group:
                // 1. Prefer HuggingFace source, then Local, then Discovered
                // 2. Prefer the one with a defined Role
                // 3. Prefer the one with the newest InstalledAt
                var bestModel = group
                    .OrderByDescending(m => m.Source == ModelSource.HuggingFace)
                    .ThenByDescending(m => m.Source == ModelSource.Local)
                    .ThenByDescending(m => !string.IsNullOrEmpty(m.Role))
                    .ThenByDescending(m => m.InstalledAt)
                    .First();

                foreach (var duplicateModel in group)
                {
                    if (duplicateModel.Id != bestModel.Id)
                    {
                        _logger.LogInformation("Removing duplicate model registration for path {Path} (ID: {Id})", duplicateModel.FilePath, duplicateModel.Id);
                        if (_models.TryRemove(duplicateModel.Id, out _))
                        {
                            changed = true;
                        }
                    }
                }
            }

            var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            if (Directory.Exists(_modelsDirectory))
            {
                // Recursive scan: downloads are scoped to per-repo subdirectories (see
                // HuggingFaceClient.SanitizeRepoIdForPath) to avoid filename collisions, so the
                // registry must find .gguf files at any depth. The flat top-level files from
                // older layouts are still picked up.
                var ggufFiles = Directory.EnumerateFiles(_modelsDirectory, "*.gguf", SearchOption.AllDirectories);
                
                foreach (var file in ggufFiles)
                {
                    existingFiles.Add(file);
                    
                    // Check if file is already registered
                    var existingModel = _models.Values.FirstOrDefault(m => m.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (existingModel == null)
                    {
                        _logger.LogInformation("Found new GGUF file: {File}", file);
                        await RegisterDiscoveredModelAsync(file);
                        changed = true;
                    }
                }
            }

            // Remove models that no longer exist on disk
            var modelsToRemove = _models.Values
                .Where(m => (m.Source == ModelSource.Local || m.Source == ModelSource.Discovered) && !File.Exists(m.FilePath))
                .ToList();

            foreach (var missingModel in modelsToRemove)
            {
                _logger.LogInformation("Removing missing model from registry: {Id}", missingModel.Id);
                if (_models.TryRemove(missingModel.Id, out _))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveInternalAsync();
                RegistryChanged?.Invoke();
            }
            
            _logger.LogInformation("Sync complete.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RegisterDiscoveredModelAsync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            GgufMetadata? metadata = null;
            try
            {
                metadata = GgufMetadataReader.Parse(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse GGUF metadata for {File}. Model will be loaded without metadata.", filePath);
            }
            
            
            string id = Guid.NewGuid().ToString();
            string fileName = fileInfo.Name;
            
            var model = new ModelInfo(
                Id: id,
                DisplayName: fileName,
                FilePath: filePath,
                FileName: fileName,
                FileSizeBytes: fileInfo.Length,
                Architecture: metadata?.Architecture,
                ParameterCount: null,
                QuantizationType: metadata?.QuantizationType,
                BlockCount: metadata?.BlockCount,
                ContextLength: metadata?.ContextLength,
                EstimatedVramMb: CalculateEstimatedVram(fileInfo.Length),
                Source: ModelSource.Discovered,
                InstalledAt: DateTime.UtcNow,
                LastUsedAt: DateTime.UtcNow,
                ChecksumSha256: null,
                Role: null,
                RawChatTemplate: metadata?.RawChatTemplate
            );

            _models[id] = model;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering discovered model {File}", filePath);
        }
    }

    private long? CalculateEstimatedVram(long fileSizeBytes)
    {
        const double overheadFactor = 1.2;
        return (long)((fileSizeBytes / (1024.0 * 1024.0)) * overheadFactor);
    }
}

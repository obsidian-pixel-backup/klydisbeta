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
    /// Initializes a new instance of the <see cref="ModelRegistry"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ModelRegistry(ILogger<ModelRegistry> logger)
    {
        _logger = logger;
        
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _modelsDirectory = Path.Combine(userHome, ".klydis", "models");
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
                _models = new ConcurrentDictionary<string, ModelInfo>(
                    models.ToDictionary(m => m.Id)
                );
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
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_models.Values, options);
            await File.WriteAllTextAsync(_registryFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save model registry.");
        }
        finally
        {
            _lock.Release();
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
        }
    }

    /// <summary>
    /// Scans the models directory, registers new .gguf files, and removes entries for missing files.
    /// </summary>
    public async Task SyncWithDiskAsync()
    {
        _logger.LogInformation("Syncing model registry with disk...");

        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        if (Directory.Exists(_modelsDirectory))
        {
            var ggufFiles = Directory.EnumerateFiles(_modelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly);
            
            foreach (var file in ggufFiles)
            {
                existingFiles.Add(file);
                
                // Check if file is already registered
                var existingModel = _models.Values.FirstOrDefault(m => m.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
                if (existingModel == null)
                {
                    _logger.LogInformation("Found new GGUF file: {File}", file);
                    await RegisterDiscoveredModelAsync(file);
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
            _models.TryRemove(missingModel.Id, out _);
        }

        if (modelsToRemove.Any() || existingFiles.Any())
        {
            await SaveAsync();
        }
        
        _logger.LogInformation("Sync complete.");
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
                ChecksumSha256: null
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

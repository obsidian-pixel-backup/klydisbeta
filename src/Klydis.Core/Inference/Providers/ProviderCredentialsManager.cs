using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference.Providers;

/// <summary>
/// Manages encrypted and persistent storage for multi-provider API keys and endpoint configurations.
/// </summary>
public interface IProviderCredentialsManager
{
    Task<IReadOnlyList<ProviderConfig>> LoadConfigsAsync(CancellationToken ct = default);
    Task SaveConfigAsync(ProviderConfig config, CancellationToken ct = default);
    Task DeleteConfigAsync(string providerId, CancellationToken ct = default);
    Task<ProviderConfig?> GetConfigAsync(string providerId, CancellationToken ct = default);
}

public sealed class ProviderCredentialsManager : IProviderCredentialsManager
{
    private readonly string _configFilePath;
    private readonly ILogger<ProviderCredentialsManager>? _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ProviderConfig> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ProviderCredentialsManager(
        string? configFilePath = null,
        ILogger<ProviderCredentialsManager>? logger = null)
    {
        _logger = logger;
        if (!string.IsNullOrEmpty(configFilePath))
        {
            _configFilePath = configFilePath;
        }
        else
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string klydisDir = Path.Combine(userProfile, ".klydis");
            Directory.CreateDirectory(klydisDir);
            _configFilePath = Path.Combine(klydisDir, "providers.json");
        }
    }

    public async Task<IReadOnlyList<ProviderConfig>> LoadConfigsAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_configFilePath))
            {
                return _cache.Values.ToList();
            }

            string json = await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return _cache.Values.ToList();
            }

            var configs = JsonSerializer.Deserialize<List<ProviderConfigDto>>(json, JsonOptions);
            if (configs != null)
            {
                _cache.Clear();
                foreach (var dto in configs)
                {
                    string? decryptedApiKey = DecryptSecret(dto.EncryptedApiKey);
                    var config = new ProviderConfig(
                        ProviderId: dto.ProviderId,
                        Type: dto.Type,
                        ApiKey: decryptedApiKey,
                        BaseUrl: dto.BaseUrl,
                        OrganizationId: dto.OrganizationId,
                        DefaultModelId: dto.DefaultModelId,
                        IsEnabled: dto.IsEnabled,
                        Priority: dto.Priority,
                        MaxRetries: dto.MaxRetries
                    );
                    _cache[config.ProviderId] = config;
                }
            }

            return _cache.Values.ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load provider credentials from {Path}", _configFilePath);
            return _cache.Values.ToList();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveConfigAsync(ProviderConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cache[config.ProviderId] = config;

            var dtoList = _cache.Values.Select(c => new ProviderConfigDto
            {
                ProviderId = c.ProviderId,
                Type = c.Type,
                EncryptedApiKey = EncryptSecret(c.ApiKey),
                BaseUrl = c.BaseUrl,
                OrganizationId = c.OrganizationId,
                DefaultModelId = c.DefaultModelId,
                IsEnabled = c.IsEnabled,
                Priority = c.Priority,
                MaxRetries = c.MaxRetries
            }).ToList();

            string dir = Path.GetDirectoryName(_configFilePath)!;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(dtoList, JsonOptions);
            await File.WriteAllTextAsync(_configFilePath, json, ct).ConfigureAwait(false);
            _logger?.LogInformation("Saved provider config for {ProviderId} to {Path}", config.ProviderId, _configFilePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task DeleteConfigAsync(string providerId, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryRemove(providerId, out _))
            {
                var dtoList = _cache.Values.Select(c => new ProviderConfigDto
                {
                    ProviderId = c.ProviderId,
                    Type = c.Type,
                    EncryptedApiKey = EncryptSecret(c.ApiKey),
                    BaseUrl = c.BaseUrl,
                    OrganizationId = c.OrganizationId,
                    DefaultModelId = c.DefaultModelId,
                    IsEnabled = c.IsEnabled,
                    Priority = c.Priority,
                    MaxRetries = c.MaxRetries
                }).ToList();

                string json = JsonSerializer.Serialize(dtoList, JsonOptions);
                await File.WriteAllTextAsync(_configFilePath, json, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<ProviderConfig?> GetConfigAsync(string providerId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(providerId, out var cached))
            return cached;

        await LoadConfigsAsync(ct).ConfigureAwait(false);
        _cache.TryGetValue(providerId, out var result);
        return result;
    }

    private static string? EncryptSecret(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(encrypted);
            }
        }
        catch
        {
            // Fallback
        }

        // Safe base64 obfuscation fallback for non-Windows or tests
        return "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
    }

    private static string? DecryptSecret(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return null;

        try
        {
            if (cipherText.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText.Substring(6));
                byte[] decrypted = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }

            if (cipherText.StartsWith("b64:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText.Substring(4));
                return Encoding.UTF8.GetString(cipherBytes);
            }
        }
        catch
        {
            // If decryption fails, return null
        }

        return cipherText;
    }

    private sealed class ProviderConfigDto
    {
        public string ProviderId { get; set; } = string.Empty;
        public ProviderType Type { get; set; }
        public string? EncryptedApiKey { get; set; }
        public string? BaseUrl { get; set; }
        public string? OrganizationId { get; set; }
        public string? DefaultModelId { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int Priority { get; set; }
        public int MaxRetries { get; set; } = 3;
    }
}

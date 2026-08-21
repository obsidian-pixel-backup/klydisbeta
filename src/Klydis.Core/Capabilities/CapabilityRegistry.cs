using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Capabilities;

/// <summary>
/// Thread-safe registry implementation managing all active machine capabilities.
/// </summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly ConcurrentDictionary<string, ICapability> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CapabilityRegistry>? _logger;

    public CapabilityRegistry(ILogger<CapabilityRegistry>? logger = null)
    {
        _logger = logger;
    }

    public void Register(ICapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        _capabilities.AddOrUpdate(
            capability.Id,
            capability,
            (key, old) =>
            {
                _logger?.LogDebug("Overwriting capability registration for {CapabilityId}", key);
                return capability;
            });

        _logger?.LogDebug("Registered capability: {CapabilityId} [{Domain}]", capability.Id, capability.Domain);
    }

    public void RegisterRange(IEnumerable<ICapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        foreach (var cap in capabilities)
        {
            Register(cap);
        }
    }

    public ICapability? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        _capabilities.TryGetValue(id.Trim(), out var cap);
        return cap;
    }

    public ICapability GetRequired(string id)
    {
        var cap = Get(id);
        if (cap is null)
        {
            throw new KeyNotFoundException($"Machine capability '{id}' is not registered.");
        }
        return cap;
    }

    public bool Contains(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return _capabilities.ContainsKey(id.Trim());
    }

    public IReadOnlyList<ICapability> GetByDomain(CapabilityDomain domain)
    {
        return _capabilities.Values
            .Where(c => c.Domain == domain)
            .OrderBy(c => c.Id)
            .ToList();
    }

    public IReadOnlyList<ICapability> GetAll()
    {
        return _capabilities.Values
            .OrderBy(c => c.Domain)
            .ThenBy(c => c.Id)
            .ToList();
    }

    public IReadOnlyList<ToolDefinition> ToToolDefinitions()
    {
        var list = new List<ToolDefinition>();
        foreach (var cap in GetAll())
        {
            var desc = cap.Describe();
            var parameters = desc.Parameters.Select(p => new ToolParameter(
                Name: p.Name,
                Type: p.Type,
                Description: p.Description,
                Required: p.Required,
                Enum: p.EnumValues?.ToArray()
            )).ToList();

            bool requiresApproval = desc.Policy != PolicyDefault.Auto;
            list.Add(new ToolDefinition(desc.Id, desc.Description, parameters, requiresApproval));
        }
        return list;
    }
}

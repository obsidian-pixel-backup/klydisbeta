using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Capabilities;

/// <summary>
/// A node in the Capability Graph representing a domain, category, or capability.
/// </summary>
public sealed record CapabilityGraphNode(
    string Id,
    string DisplayName,
    string Description,
    CapabilityDomain? Domain = null,
    string? CapabilityId = null,
    IReadOnlyList<string>? Prerequisites = null
)
{
    public bool IsLeaf => !string.IsNullOrEmpty(CapabilityId);
}

/// <summary>
/// Capability Graph modeling the machine surface taxonomy and execution routing.
/// </summary>
public sealed class CapabilityGraph
{
    private readonly ICapabilityRegistry _registry;
    private readonly Dictionary<string, List<string>> _prerequisites = new(StringComparer.OrdinalIgnoreCase);

    public CapabilityGraph(ICapabilityRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Registers a prerequisite dependency between capabilities.
    /// (e.g. "desktop.window.move" requires "desktop.windows.enumerate" or "desktop.displays.enumerate").
    /// </summary>
    public void AddPrerequisite(string capabilityId, string prerequisiteCapabilityId)
    {
        if (!_prerequisites.TryGetValue(capabilityId, out var list))
        {
            list = new List<string>();
            _prerequisites[capabilityId] = list;
        }

        if (!list.Contains(prerequisiteCapabilityId, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(prerequisiteCapabilityId);
        }
    }

    /// <summary>
    /// Gets direct prerequisite capabilities required before executing the specified capability.
    /// </summary>
    public IReadOnlyList<string> GetPrerequisites(string capabilityId)
    {
        return _prerequisites.TryGetValue(capabilityId, out var list)
            ? list.ToList()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Discovers relevant capabilities given a natural language search query or keywords.
    /// </summary>
    public IReadOnlyList<CapabilityDescription> Discover(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _registry.GetAll().Select(c => c.Describe()).ToList();
        }

        var terms = query.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(CapabilityDescription Desc, int Score)>();

        foreach (var cap in _registry.GetAll())
        {
            var desc = cap.Describe();
            int score = 0;

            foreach (var term in terms)
            {
                if (desc.Id.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 5;
                if (desc.Domain.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (desc.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (desc.Parameters.Any(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))) score += 1;
            }

            if (score > 0)
            {
                scored.Add((desc, score));
            }
        }

        return scored.OrderByDescending(s => s.Score).Select(s => s.Desc).ToList();
    }

    /// <summary>
    /// Formats an ASCII tree representation of all registered capabilities for reasoning.
    /// </summary>
    public string RenderGraphTree()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("KLYDIS MACHINE CAPABILITY GRAPH");

        var domains = Enum.GetValues<CapabilityDomain>();
        for (int i = 0; i < domains.Length; i++)
        {
            var dom = domains[i];
            var caps = _registry.GetByDomain(dom);
            if (caps.Count == 0) continue;

            sb.AppendLine($"├── [{dom}] ({caps.Count} capabilities)");
            for (int j = 0; j < caps.Count; j++)
            {
                var c = caps[j];
                var desc = c.Describe();
                string prefix = (j == caps.Count - 1) ? "    └── " : "    ├── ";
                sb.AppendLine($"{prefix}{desc.Id} - {desc.Description}");
            }
        }

        return sb.ToString();
    }
}

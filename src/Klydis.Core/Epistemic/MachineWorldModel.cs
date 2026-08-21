using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Capabilities;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Epistemic;

/// <summary>
/// World Model coordinating the verified epistemic state of the local machine and software environment.
/// </summary>
public sealed class MachineWorldModel : IWorldModel
{
    private readonly FactLedger _ledger;
    private readonly ILogger<MachineWorldModel>? _logger;

    public MachineWorldModel(FactLedger ledger, ILogger<MachineWorldModel>? logger = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger;
    }

    public Task<T?> GetFactAsync<T>(string domain, string entityKey, string propertyName, CancellationToken ct = default) =>
        _ledger.GetFactAsync<T>(domain, entityKey, propertyName, ct);

    public Task<EpistemicFact?> GetFactEntryAsync(string domain, string entityKey, string propertyName, CancellationToken ct = default) =>
        _ledger.GetFactEntryAsync(domain, entityKey, propertyName, ct);

    public Task AssertFactAsync(FactAssertion assertion, CancellationToken ct = default) =>
        _ledger.AssertFactAsync(assertion, ct);

    public Task InvalidateAsync(string domain, string? entityKey = null, string? reason = null, CancellationToken ct = default) =>
        _ledger.InvalidateAsync(domain, entityKey, reason, ct);

    public Task<IReadOnlyList<EpistemicFact>> QueryDomainFactsAsync(string domain, CancellationToken ct = default) =>
        _ledger.QueryDomainFactsAsync(domain, ct);

    public async Task<string> SummarizeStateAsync(CancellationToken ct = default)
    {
        var facts = await _ledger.GetAllActiveFactsAsync(ct);
        if (facts.Count == 0) return "World state: No active verified machine facts recorded.";

        var sb = new StringBuilder();
        sb.AppendLine("### Verified Machine State (Fact Ledger)");

        var grouped = new Dictionary<string, List<EpistemicFact>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in facts)
        {
            if (!grouped.TryGetValue(f.Domain, out var list))
            {
                list = new List<EpistemicFact>();
                grouped[f.Domain] = list;
            }
            list.Add(f);
        }

        foreach (var (domain, domainFacts) in grouped)
        {
            sb.AppendLine($"- **[{domain.ToUpperInvariant()}]**");
            foreach (var f in domainFacts)
            {
                var remaining = f.ExpiresAtUtc - DateTime.UtcNow;
                string ttlStr = remaining.TotalSeconds > 60
                    ? $"{remaining.TotalMinutes:F1}m"
                    : $"{remaining.TotalSeconds:F0}s";
                sb.AppendLine($"  * `{f.EntityKey}.{f.PropertyName}`: {f.ValueJson} (TTL: {ttlStr})");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

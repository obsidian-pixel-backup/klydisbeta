using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

namespace Klydis.Core.Capabilities.Providers;

/// <summary>
/// Capability: network.interfaces
/// Enumerates network adapters, IPs, gateways, and link speeds.
/// </summary>
public sealed class NetworkInterfacesCapability : ICapability
{
    public string Id => "network.interfaces";
    public CapabilityDomain Domain => CapabilityDomain.Network;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Enumerates network adapters, IP addresses, operational status, interface types, and link speeds.",
        Parameters: Array.Empty<CapabilityParameter>(),
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Select(nic =>
                {
                    var ipProps = nic.GetIPProperties();
                    var unicast = ipProps.UnicastAddresses
                        .Where(u => u.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        .Select(u => u.Address.ToString())
                        .ToList();

                    return new
                    {
                        nic.Name,
                        nic.Description,
                        Type = nic.NetworkInterfaceType.ToString(),
                        Status = nic.OperationalStatus.ToString(),
                        SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : 0,
                        IpAddresses = unicast
                    };
                }).ToList();

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(interfaces, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, interfaces, sw.Elapsed, evidence));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null)
            return Task.FromResult(VerificationResult.Failed("Failed to enumerate network interfaces."));

        var facts = new List<FactAssertion>
        {
            new("network", "interfaces", "adapters", result.Data, TimeSpan.FromMinutes(5), Id)
        };

        return Task.FromResult(VerificationResult.Verified("Network interfaces verified.", facts));
    }
}

/// <summary>
/// Capability: network.ping
/// Pings a remote host or IP to measure latency and reachability.
/// </summary>
public sealed class NetworkPingCapability : ICapability
{
    public string Id => "network.ping";
    public CapabilityDomain Domain => CapabilityDomain.Network;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Sends ICMP echo requests (ping) to a hostname or IP address to measure latency and reachability.",
        Parameters: new List<CapabilityParameter>
        {
            new("host", "string", "The hostname or IP address to ping (e.g. '8.8.8.8', 'google.com').", true),
            new("timeout_ms", "integer", "Timeout in milliseconds (default: 3000).", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? host = request.GetParam<string>("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'host' is required for network.ping."));
        }
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string host = request.GetParam<string>("host")!;
            int timeout = Math.Clamp(request.GetParam<int>("timeout_ms", 3000), 500, 10000);

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout);

            var data = new
            {
                Host = host,
                Status = reply.Status.ToString(),
                RoundtripTimeMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1,
                Address = reply.Address?.ToString() ?? host
            };

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow,
                StructuredMetrics: new Dictionary<string, object?> { ["RoundtripTimeMs"] = data.RoundtripTimeMs }
            );

            return reply.Status == IPStatus.Success
                ? CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence)
                : CapabilityResult.Failed(Id, $"Ping to {host} returned {reply.Status}", sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success || result.Data is null)
            return Task.FromResult(VerificationResult.Failed("Ping target was unreachable."));

        return Task.FromResult(VerificationResult.Verified("Network ping verified reachability."));
    }
}

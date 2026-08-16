namespace Klydis.Core.Protocol;

/// <summary>
/// Registry that maps a <see cref="ModelProfile"/> to the protocol implementation that knows
/// how to talk to that model. P1: populated progressively with adapters (Qwen first). Until a
/// profile has a registered adapter, the runtime falls back to the legacy paths — the
/// registry is the single place that decides which path a model takes, so the fallback is
/// explicit and observable instead of implicit.
/// </summary>
public static class ProtocolRegistry
{
    private static readonly Dictionary<string, Func<ModelProfile, IModelProtocol>> _factories = new(StringComparer.Ordinal);

    /// <summary>Registers a protocol factory keyed by protocol kind.</summary>
    public static void Register(string protocolKey, Func<ModelProfile, IModelProtocol> factory)
    {
        _factories[protocolKey] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Clears all registrations (used by tests).</summary>
    public static void Reset()
    {
        _factories.Clear();
    }

    /// <summary>
    /// The protocol key for a profile, or null when no protocol is registered (legacy fallback).
    /// Derived from the profile's tool protocol, so a Qwen-native profile resolves to
    /// "qwen-native" and everything else to "generic-json".
    /// </summary>
    public static string? ResolveProtocolKey(ModelProfile profile)
        => profile.ToolProtocol switch
        {
            ToolProtocol.QwenNative => "qwen-native",
            ToolProtocol.Antml => "antml",
            _ => "generic-json"
        };

    /// <summary>
    /// The protocol adapter for a profile, or null when none is registered (the runtime falls
    /// back to legacy parsing for that model).
    /// </summary>
    public static IModelProtocol? Resolve(ModelProfile profile)
    {
        string? key = ResolveProtocolKey(profile);
        if (key == null) return null;
        return _factories.TryGetValue(key, out var factory) ? factory(profile) : null;
    }
}

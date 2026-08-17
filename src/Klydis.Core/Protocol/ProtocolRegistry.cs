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
    // Thread safety (review: multiple sessions / model switches / tests resolve protocols
    // concurrently — the dictionary and the defaults flag were unsynchronized mutable
    // statics). A single lock covers both; resolution copies the factory reference under the
    // lock so the dictionary is never read while another thread mutates it.
    private static readonly object _sync = new();
    private static readonly Dictionary<string, Func<ModelProfile, IModelProtocol>> _factories = new(StringComparer.Ordinal);
    private static bool _defaultsRegistered;

    /// <summary>Registers a protocol factory keyed by protocol kind.</summary>
    public static void Register(string protocolKey, Func<ModelProfile, IModelProtocol> factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (_sync)
        {
            _factories[protocolKey] = factory;
        }
    }

    /// <summary>
    /// Registers the built-in adapters — currently qwen-native; generic-json and the other
    /// families land with their own adapters. Idempotent; called lazily by the runtime and
    /// available to tests. Reset() clears everything (including the default flag) so tests
    /// can simulate a fresh registry.
    /// </summary>
    public static void RegisterDefaultAdapters()
    {
        lock (_sync)
        {
            if (_defaultsRegistered) return;
            _defaultsRegistered = true;
            _factories["qwen-native"] = static profile => new QwenProtocolAdapter(profile);
        }
    }

    /// <summary>Clears all registrations (used by tests).</summary>
    public static void Reset()
    {
        lock (_sync)
        {
            _factories.Clear();
            _defaultsRegistered = false;
        }
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
            ToolProtocol.OpenAiStyle => "openai-style",
            // Unknown = NO protocol claimed until a capability probe proves one (the
            // optimistic mapping to "generic-json" would make every unknown GGUF enter a
            // JSON execution protocol it was never trained to produce).
            ToolProtocol.Unknown => null,
            _ => "generic-json"
        };

    /// <summary>
    /// The protocol adapter for a profile, or null when none is registered (the runtime falls
    /// back to legacy parsing for that model).
    /// </summary>
    public static IModelProtocol? Resolve(ModelProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        string? key = ResolveProtocolKey(profile);
        if (key == null) return null;
        Func<ModelProfile, IModelProtocol>? factory;
        lock (_sync)
        {
            factory = _factories.TryGetValue(key, out var f) ? f : null;
        }
        return factory?.Invoke(profile);
    }
}

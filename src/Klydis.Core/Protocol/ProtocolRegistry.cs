using System;
using System.Collections.Generic;
using Klydis.Core.Chat;

namespace Klydis.Core.Protocol;

/// <summary>
/// Registry that maps a <see cref="ModelProfile"/> to the protocol implementation that knows
/// how to talk to that model.
/// </summary>
public static class ProtocolRegistry
{
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
    /// Registers the built-in adapters across all 10 model protocols.
    /// </summary>
    public static void RegisterDefaultAdapters()
    {
        lock (_sync)
        {
            if (_defaultsRegistered) return;
            _defaultsRegistered = true;
            _factories["qwen-native"] = static profile => new QwenProtocolAdapter(profile);
            _factories["llama3"] = static profile => new Llama3ProtocolAdapter(profile);
            _factories["deepseek"] = static profile => new DeepSeekProtocolAdapter(profile);
            _factories["mistral"] = static profile => new MistralProtocolAdapter(profile);
            _factories["gemma"] = static profile => new GemmaProtocolAdapter(profile);
            _factories["phi"] = static profile => new PhiProtocolAdapter(profile);
            _factories["command-r"] = static profile => new CommandRProtocolAdapter(profile);
            _factories["openai-style"] = static profile => new OpenAiProtocolAdapter(profile);
            _factories["antml"] = static profile => new AntmlProtocolAdapter(profile);
            _factories["generic-json"] = static profile => new GenericJsonProtocolAdapter(profile);
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
    /// </summary>
    public static string? ResolveProtocolKey(ModelProfile profile)
        => profile.ToolProtocol switch
        {
            ToolProtocol.QwenNative => "qwen-native",
            ToolProtocol.Llama3Native => "llama3",
            ToolProtocol.DeepSeekNative => "deepseek",
            ToolProtocol.MistralNative => "mistral",
            ToolProtocol.GemmaNative => "gemma",
            ToolProtocol.PhiNative => "phi",
            ToolProtocol.CommandRNative => "command-r",
            ToolProtocol.Antml => "antml",
            ToolProtocol.OpenAiStyle => "openai-style",
            ToolProtocol.GenericJson => "generic-json",
            ToolProtocol.Unknown => null,
            _ => profile.Template switch
            {
                ChatTemplate.Llama3 => "llama3",
                ChatTemplate.DeepSeek => "deepseek",
                ChatTemplate.Mistral => "mistral",
                ChatTemplate.Gemma => "gemma",
                ChatTemplate.Phi => "phi",
                ChatTemplate.CommandR => "command-r",
                ChatTemplate.Qwen => "qwen-native",
                _ => "generic-json"
            }
        };

    /// <summary>
    /// Resolves the safe protocol key for a profile, applying protocol confidence gating:
    /// if protocol confidence is low (&lt; 0.50) and the protocol is unverified native,
    /// it safely falls back to "generic-json" to prevent hallucinated tool dialect loops.
    /// </summary>
    public static string ResolveSafeProtocolKey(ModelProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        string? rawKey = ResolveProtocolKey(profile);
        if (rawKey == null) return "generic-json";

        // Protocol confidence gate: below 0.50 confidence on an experimental native protocol,
        // safely fallback to structured generic-json schema.
        if (profile.ProtocolConfidence < 0.50 && profile.ToolCalling <= CapabilityLevel.Experimental && rawKey != "generic-json")
        {
            return "generic-json";
        }

        return rawKey;
    }

    /// <summary>
    /// The protocol adapter for a profile, or null when none is registered.
    /// </summary>
    public static IModelProtocol? Resolve(ModelProfile profile, bool enforceConfidenceGating = false)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        string? key = enforceConfidenceGating ? ResolveSafeProtocolKey(profile) : ResolveProtocolKey(profile);
        if (key == null) return null;
        Func<ModelProfile, IModelProtocol>? factory;
        lock (_sync)
        {
            factory = _factories.TryGetValue(key, out var f) ? f : null;
        }
        return factory?.Invoke(profile);
    }
}

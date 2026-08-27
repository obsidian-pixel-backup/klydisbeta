using System;
using Klydis.Core.Models;

namespace Klydis.Core.Inference;

/// <summary>
/// Supported Key-Value Cache Quantization types.
/// </summary>
public enum KvCacheQuantizationType
{
    F16 = 0,
    Q8_0 = 1,
    Q4_0 = 2,
    Q4_1 = 3,
    TurboQuant3Bit = 4
}

/// <summary>
/// Represents calculated KV cache memory requirements and hardware metrics.
/// </summary>
public record KvCacheMemoryEstimate(
    string AttentionArchitecture, // MHA, GQA, or MQA
    long NumLayers,
    long NumQueryHeads,
    long NumKvHeads,
    long HeadDim,
    long ContextSize,
    KvCacheQuantizationType QuantizationType,
    double BytesPerToken,
    double TotalVramBytes,
    double TotalVramMegabytes,
    double TotalVramGigabytes,
    double GqaRatio // Efficiency factor compared to MHA
);

/// <summary>
/// Hardware-aware KV cache memory calculator for MHA, GQA, and MQA model architectures.
/// </summary>
public static class KvCacheCalculator
{
    /// <summary>
    /// Gets bytes per element for the given KV quantization precision type.
    /// Values match the actual GGML block-quantized layouts (block size 32 with block
    /// scale/offset metadata): Q4_0 = 18 bytes/32 elts, Q4_1 = 20/32, Q8_0 = 34/32.
    /// These feed VRAM planning, so using the real on-disk/in-memory sizes (not the
    /// naive bit width) keeps the offload plan and KV estimates accurate.
    /// </summary>
    public static double GetBytesPerElement(KvCacheQuantizationType quantType) => quantType switch
    {
        KvCacheQuantizationType.F16 => 2.0,
        KvCacheQuantizationType.Q8_0 => 34.0 / 32.0, // 1.0625
        KvCacheQuantizationType.Q4_0 => 18.0 / 32.0, // 0.5625
        KvCacheQuantizationType.Q4_1 => 20.0 / 32.0, // 0.625
        KvCacheQuantizationType.TurboQuant3Bit => 0.375, // 3 bits per value (PolarQuant + QJL residual)
        _ => 18.0 / 32.0
    };

    /// <summary>
    /// Calculates KV Cache VRAM footprint from GGUF metadata and context parameters.
    /// Accurately scales KV cache layers for hybrid SSM/DeltaNet architectures (such as Qwen 3.5 / Qwen 3-Next)
    /// where only standard attention layers (1/4 of layers, e.g. 8 of 32) maintain a dynamic KV cache.
    /// </summary>
    public static KvCacheMemoryEstimate Calculate(GgufMetadata metadata, long contextSize, KvCacheQuantizationType quantType = KvCacheQuantizationType.Q4_0)
    {
        long numLayers = metadata.BlockCount ?? 32;
        long numQueryHeads = metadata.HeadCount ?? 32;
        long numKvHeads = metadata.HeadCountKv ?? numQueryHeads;
        long embeddingLength = metadata.EmbeddingLength ?? 4096;
        long headDim = numQueryHeads > 0 ? (embeddingLength / numQueryHeads) : 128;

        string archLower = (metadata.Architecture ?? string.Empty).ToLowerInvariant();
        long effectiveAttentionLayers = numLayers;

        // Qwen 3.5 / Qwen 3-Next hybrid architectures: 8 of 32 layers (1/4) are standard attention,
        // while 24 of 32 layers are Gated DeltaNet linear recurrence with constant O(1) state.
        if (archLower is "qwen35" or "qwen3next" or "qwen35moe" ||
            archLower.StartsWith("qwen35", StringComparison.Ordinal) ||
            archLower.StartsWith("qwen3next", StringComparison.Ordinal) ||
            archLower.StartsWith("qwen3-next", StringComparison.Ordinal))
        {
            effectiveAttentionLayers = Math.Max(1, numLayers / 4);
        }
        else if (archLower is "mamba" or "rwkv" or "jamba")
        {
            effectiveAttentionLayers = 0;
        }

        return Calculate(effectiveAttentionLayers, numQueryHeads, numKvHeads, headDim, contextSize, quantType);
    }

    /// <summary>
    /// Calculates KV Cache VRAM footprint from raw architecture parameters.
    /// </summary>
    public static KvCacheMemoryEstimate Calculate(
        long numLayers,
        long numQueryHeads,
        long numKvHeads,
        long headDim,
        long contextSize,
        KvCacheQuantizationType quantType = KvCacheQuantizationType.Q4_0)
    {
        string arch = numKvHeads switch
        {
            1 => "MQA (Multi-Query Attention)",
            var k when k < numQueryHeads => $"GQA (Grouped-Query Attention 1:{numQueryHeads / Math.Max(1, numKvHeads)})",
            _ => "MHA (Multi-Head Attention)"
        };

        double bytesPerElem = GetBytesPerElement(quantType);

        // Memory per token = 2 (K & V) * numLayers * numKvHeads * headDim * bytesPerElem
        double bytesPerToken = 2.0 * numLayers * numKvHeads * headDim * bytesPerElem;
        double totalBytes = bytesPerToken * contextSize;
        double totalMb = totalBytes / (1024.0 * 1024.0);
        double totalGb = totalMb / 1024.0;

        double gqaRatio = numQueryHeads > 0 ? (double)numQueryHeads / Math.Max(1, numKvHeads) : 1.0;

        return new KvCacheMemoryEstimate(
            AttentionArchitecture: arch,
            NumLayers: numLayers,
            NumQueryHeads: numQueryHeads,
            NumKvHeads: numKvHeads,
            HeadDim: headDim,
            ContextSize: contextSize,
            QuantizationType: quantType,
            BytesPerToken: bytesPerToken,
            TotalVramBytes: totalBytes,
            TotalVramMegabytes: Math.Round(totalMb, 2),
            TotalVramGigabytes: Math.Round(totalGb, 3),
            GqaRatio: Math.Round(gqaRatio, 1)
        );
    }

    /// <summary>
    /// Calculates the maximum achievable context size given a target VRAM budget in megabytes.
    /// </summary>
    public static long MaxContextForVramBudget(GgufMetadata metadata, double maxVramMb, KvCacheQuantizationType quantType = KvCacheQuantizationType.Q4_0)
    {
        var singleTokenEst = Calculate(metadata, 1, quantType);
        if (singleTokenEst.BytesPerToken <= 0) return 4096;

        double maxBytes = maxVramMb * 1024.0 * 1024.0;
        long maxContext = (long)(maxBytes / singleTokenEst.BytesPerToken);

        // Round down to nearest 512 boundary
        return Math.Max(512, (maxContext / 512) * 512);
    }
}

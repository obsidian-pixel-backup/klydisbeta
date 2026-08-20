using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Klydis.Core.Models;

/// <summary>
/// Extracted metadata from a GGUF file.
/// </summary>
public record GgufMetadata(
    string? Architecture,
    long? BlockCount,
    long? ContextLength,
    long? EmbeddingLength,
    long? HeadCount,
    long? HeadCountKv,
    long? VocabSize,
    string? QuantizationType,
    string? RawChatTemplate = null,
    string? FineTuneName = null,
    string? ModelName = null,
    string? PreTokenizer = null,
    string? EosToken = null,
    string? BosToken = null
);

/// <summary>
/// Result of a structural (non-architecture) integrity check of a GGUF file.
/// A file can be architecturally "supported" yet structurally broken (truncated download,
/// interrupted conversion) — this check catches that independently of model type.
/// </summary>
public record GgufIntegrityResult(bool IsValid, string? Issue);

/// <summary>
/// A pure C# binary parser for GGUF file headers.
/// </summary>
public static class GgufMetadataReader
{
    private const uint GgufMagic = 0x46554747; // 'GGUF' in little endian

    /// <summary>
    /// Cached parse result keyed by file path + last-write-time + length. Model loading
    /// parses the same GGUF header several times (InferenceEngine, GgufCompatibilityAdapter
    /// + its integrity walk, ModelPool), so a multi-GB model used to pay 3-4 full header
    /// reads per cold load. Bounded by a simple cap: metadata entries are tiny (a few dozen
    /// KV pairs), so clearing beyond the cap costs almost nothing.
    /// </summary>
    private sealed record CachedGguf(DateTime LastWriteTimeUtc, long Length, GgufMetadata? Metadata);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedGguf> ParseCache = new();

    private const int ParseCacheCap = 64;

    /// <summary>
    /// Parses GGUF metadata with a file-keyed cache (path + last-write-time + length), so
    /// repeated parses of the same unchanged file are free. Falls back to an uncached parse
    /// on any cache failure.
    /// </summary>
    public static GgufMetadata? ParseCached(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;

            if (ParseCache.TryGetValue(info.FullName, out var cached) &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc &&
                cached.Length == info.Length)
            {
                return cached.Metadata;
            }

            var parsed = Parse(filePath);
            if (ParseCache.Count > ParseCacheCap) ParseCache.Clear();
            ParseCache[info.FullName] = new CachedGguf(info.LastWriteTimeUtc, info.Length, parsed);
            return parsed;
        }
        catch
        {
            return Parse(filePath);
        }
    }

    private enum GgufValueType : uint
    {
        Uint8 = 0, Int8 = 1, Uint16 = 2, Int16 = 3,
        Uint32 = 4, Int32 = 5, Float32 = 6, Bool = 7,
        String = 8, Array = 9, Uint64 = 10, Int64 = 11, Float64 = 12
    }

    /// <summary>
    /// Parses the metadata from a GGUF file at the given path.
    /// Returns null if the file cannot be read or is not a valid GGUF.
    /// </summary>
    /// <param name="filePath">The path to the GGUF file.</param>
    /// <returns>A GgufMetadata object containing extracted fields, or null if invalid.</returns>
    public static GgufMetadata? Parse(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            uint magic = reader.ReadUInt32();
            if (magic != GgufMagic)
            {
                return null;
            }

            uint version = reader.ReadUInt32();
            if (version == 1)
            {
                // Version 1 uses uint32 for tensor and KV counts
                ulong tensorCountV1 = reader.ReadUInt32();
                ulong kvCountV1 = reader.ReadUInt32();
                return ParseKvs(reader, kvCountV1, filePath);
            }
            else
            {
                // Version 2 and 3 use uint64
                ulong tensorCount = reader.ReadUInt64();
                ulong kvCount = reader.ReadUInt64();
                return ParseKvs(reader, kvCount, filePath);
            }
        }
        catch
        {
            return null;
        }
    }

    private static GgufMetadata ParseKvs(BinaryReader reader, ulong kvCount, string? filePath = null)
    {
        var rawKvs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        for (ulong i = 0; i < kvCount; i++)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                break;

            string key = ReadGgufString(reader);
            var valueType = (GgufValueType)reader.ReadUInt32();
            object? value = ReadGgufValue(reader, valueType);

            if (value != null)
            {
                rawKvs[key] = value;
            }
        }

        string? architecture = rawKvs.TryGetValue("general.architecture", out var archVal) && archVal is string archStr ? archStr : null;

        string? quantizationType = null;
        if (rawKvs.TryGetValue("general.file_type", out var ftVal) && ftVal is IConvertible ft)
        {
            quantizationType = GetFileTypeString(Convert.ToUInt32(ft));
        }

        if (string.IsNullOrEmpty(quantizationType) || quantizationType.StartsWith("Type_", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : string.Empty;
            string detected = ExtractQuantFromFileName(fileName);
            if (!string.IsNullOrEmpty(detected))
            {
                quantizationType = detected;
            }
        }

        if (string.IsNullOrEmpty(quantizationType) && rawKvs.TryGetValue("general.quantization_version", out var qvVal) && qvVal is IConvertible qv)
        {
            quantizationType = $"v{Convert.ToUInt32(qv)}";
        }

        string? rawChatTemplate = rawKvs.TryGetValue("tokenizer.chat_template", out var ctVal) && ctVal is string ctStr ? ctStr : null;
        string? fineTuneName = rawKvs.TryGetValue("general.finetune", out var ftValName) && ftValName is string ftStr ? ftStr : null;
        string? modelName = rawKvs.TryGetValue("general.name", out var nameVal) && nameVal is string nStr ? nStr : null;
        string? preTokenizer = rawKvs.TryGetValue("tokenizer.ggml.pre", out var preVal) && preVal is string preStr ? preStr : null;

        // Stop-token extraction (blueprint TODO 012): the tokenizer vocab is an array of
        // strings; eos/bos ids are uint32. Resolve the ids to their token TEXT here and
        // discard the (potentially 150K-entry) vocab array so the cached GgufMetadata stays
        // small — callers only need the stop-token strings, never the full vocab.
        string? eosToken = null;
        string? bosToken = null;
        if (rawKvs.TryGetValue("tokenizer.ggml.tokens", out var tokensVal) && tokensVal is string[] tokens)
        {
            if (rawKvs.TryGetValue("tokenizer.ggml.eos_token_id", out var eosVal) && eosVal is IConvertible eosConv)
            {
                uint eosId = Convert.ToUInt32(eosConv);
                if (eosId < (uint)tokens.Length) eosToken = tokens[eosId];
            }
            if (rawKvs.TryGetValue("tokenizer.ggml.bos_token_id", out var bosVal) && bosVal is IConvertible bosConv)
            {
                uint bosId = Convert.ToUInt32(bosConv);
                if (bosId < (uint)tokens.Length) bosToken = tokens[bosId];
            }
        }

        long? blockCount = GetLongValue(rawKvs, architecture, "block_count");
        long? contextLength = GetLongValue(rawKvs, architecture, "context_length");
        long? embeddingLength = GetLongValue(rawKvs, architecture, "embedding_length");
        long? headCount = GetLongValue(rawKvs, architecture, "attention.head_count");
        long? headCountKv = GetLongValue(rawKvs, architecture, "attention.head_count_kv");
        long? vocabSize = GetLongValue(rawKvs, architecture, "vocab_size");

        return new GgufMetadata(
            Architecture: architecture,
            BlockCount: blockCount,
            ContextLength: contextLength,
            EmbeddingLength: embeddingLength,
            HeadCount: headCount,
            HeadCountKv: headCountKv,
            VocabSize: vocabSize,
            QuantizationType: quantizationType,
            RawChatTemplate: rawChatTemplate,
            FineTuneName: fineTuneName,
            ModelName: modelName,
            PreTokenizer: preTokenizer,
            EosToken: eosToken,
            BosToken: bosToken
        );
    }

    private static long? GetLongValue(Dictionary<string, object> rawKvs, string? architecture, string suffix)
    {
        if (!string.IsNullOrEmpty(architecture) && rawKvs.TryGetValue($"{architecture}.{suffix}", out var val) && val is IConvertible c)
        {
            return Convert.ToInt64(c);
        }

        foreach (var kvp in rawKvs)
        {
            if (kvp.Key.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase) && kvp.Value is IConvertible cFallback)
            {
                return Convert.ToInt64(cFallback);
            }
        }

        return null;
    }

    private static string ReadGgufString(BinaryReader reader)
    {
        ulong length = reader.ReadUInt64();
        if (length == 0) return string.Empty;
        
        if (length > int.MaxValue || reader.BaseStream.Position + (long)length > reader.BaseStream.Length)
            throw new InvalidDataException("String length exceeds file boundaries.");
            
        byte[] bytes = reader.ReadBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static object? ReadGgufValue(BinaryReader reader, GgufValueType valueType)
    {
        return valueType switch
        {
            GgufValueType.Uint8 => reader.ReadByte(),
            GgufValueType.Int8 => reader.ReadSByte(),
            GgufValueType.Uint16 => reader.ReadUInt16(),
            GgufValueType.Int16 => reader.ReadInt16(),
            GgufValueType.Uint32 => reader.ReadUInt32(),
            GgufValueType.Int32 => reader.ReadInt32(),
            GgufValueType.Float32 => reader.ReadSingle(),
            GgufValueType.Bool => reader.ReadBoolean(),
            GgufValueType.String => ReadGgufString(reader),
            GgufValueType.Array => ReadGgufArray(reader),
            GgufValueType.Uint64 => reader.ReadUInt64(),
            GgufValueType.Int64 => reader.ReadInt64(),
            GgufValueType.Float64 => reader.ReadDouble(),
            _ => throw new InvalidDataException($"Unknown GGUF value type: {valueType}")
        };
    }

    private static object? ReadGgufArray(BinaryReader reader)
    {
        var itemType = (GgufValueType)reader.ReadUInt32();
        ulong count = reader.ReadUInt64();

        // String arrays (e.g. tokenizer.ggml.tokens) are materialized so callers can map token
        // ids to their text (eos/bos stop tokens). All other array types are seeked past exactly
        // as before — materializing the large typed tensor-metadata arrays GGUF files carry would
        // balloon memory for no benefit.
        if (itemType == GgufValueType.String)
        {
            var items = new string[count];
            for (ulong i = 0; i < count; i++)
            {
                items[i] = ReadGgufString(reader);
            }
            return items;
        }

        long itemSize = GetFixedItemSize(itemType);
        if (itemSize > 0)
        {
            reader.BaseStream.Seek((long)(count * (ulong)itemSize), SeekOrigin.Current);
        }
        else
        {
            for (ulong i = 0; i < count; i++)
            {
                if (itemType == GgufValueType.Array)
                {
                    ReadGgufArray(reader);
                }
                else
                {
                    ReadGgufValue(reader, itemType);
                }
            }
        }
        return null;
    }

    private static long GetFixedItemSize(GgufValueType type)
    {
        return type switch
        {
            GgufValueType.Uint8 or GgufValueType.Int8 or GgufValueType.Bool => 1,
            GgufValueType.Uint16 or GgufValueType.Int16 => 2,
            GgufValueType.Uint32 or GgufValueType.Int32 or GgufValueType.Float32 => 4,
            GgufValueType.Uint64 or GgufValueType.Int64 or GgufValueType.Float64 => 8,
            _ => -1
        };
    }

    /// <summary>
    /// Cached variant of <see cref="ValidateStructuralIntegrity"/>, keyed like
    /// <see cref="ParseCached"/>. The integrity walk reads the whole header plus the tensor
    /// table, so caching it avoids repeating that work when the same model is evaluated
    /// repeatedly (e.g. per load, per model-library refresh).
    /// </summary>
    private sealed record CachedIntegrity(DateTime LastWriteTimeUtc, long Length, GgufIntegrityResult Result);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedIntegrity> IntegrityCache = new();

    private const int IntegrityCacheCap = 64;

    public static GgufIntegrityResult ValidateStructuralIntegrityCached(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return new GgufIntegrityResult(false, "Model file not found.");
            }

            if (IntegrityCache.TryGetValue(info.FullName, out var cached) &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc &&
                cached.Length == info.Length)
            {
                return cached.Result;
            }

            var result = ValidateStructuralIntegrity(filePath);
            if (IntegrityCache.Count > IntegrityCacheCap) IntegrityCache.Clear();
            IntegrityCache[info.FullName] = new CachedIntegrity(info.LastWriteTimeUtc, info.Length, result);
            return result;
        }
        catch
        {
            return ValidateStructuralIntegrity(filePath);
        }
    }

    /// <summary>
    /// Validates the structural integrity of a GGUF file WITHOUT any architecture assumptions:
    /// (1) the tensor-info table must contain exactly tensor_count entries (i.e. the header is
    ///     not truncated mid-table), (2) every transformer block the metadata declares
    ///     (blk.0..blk.{block_count-1}) must actually have tensors present, and (3) the tensor
    ///     data region declared by the last tensor must lie within the file. This is what turns
    ///     "missing tensor 'blk.16...'" from a confusing "architecture not supported" error into
    ///     a clear "file corrupt/truncated — re-download" message, and it works for any model
    ///     architecture (llama.cpp normalizes all layer tensors to the 'blk.N.' prefix).
    /// </summary>
    /// <param name="filePath">Path to the GGUF file.</param>
    /// <returns>Valid=true when the file is structurally sound; otherwise false with a description.</returns>
    public static GgufIntegrityResult ValidateStructuralIntegrity(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            uint magic = reader.ReadUInt32();
            if (magic != GgufMagic)
            {
                return new GgufIntegrityResult(false, "Not a valid GGUF file (bad magic bytes).");
            }

            uint version = reader.ReadUInt32();
            ulong tensorCount;
            ulong kvCount;
            if (version == 1)
            {
                tensorCount = reader.ReadUInt32();
                kvCount = reader.ReadUInt32();
            }
            else
            {
                tensorCount = reader.ReadUInt64();
                kvCount = reader.ReadUInt64();
            }

            // Skip the KV metadata section (values are read generically; arrays are skipped via seek).
            for (ulong i = 0; i < kvCount; i++)
            {
                ReadGgufString(reader);
                var vtype = (GgufValueType)reader.ReadUInt32();
                ReadGgufValue(reader, vtype);
            }

            // Walk the tensor-info table: name, n_dims, dims, ggml type, data offset.
            long fileLength = stream.Length;
            long maxBlockSeen = -1;
            long maxDataEnd = -1;
            bool anyBlockTensors = false;
            for (ulong i = 0; i < tensorCount; i++)
            {
                if (reader.BaseStream.Position >= fileLength)
                {
                    return new GgufIntegrityResult(false,
                        $"Tensor table truncated: header declares {tensorCount} tensors but the file ends after {i}. " +
                        $"The file is corrupt or was cut short during download/conversion.");
                }

                string name = ReadGgufString(reader);
                uint nDims = reader.ReadUInt32();
                ulong numel = 1;
                for (uint d = 0; d < nDims; d++)
                {
                    numel *= reader.ReadUInt64();
                }
                uint ggmlType = reader.ReadUInt32();
                ulong offset = reader.ReadUInt64();

                if (name.StartsWith("blk.", StringComparison.Ordinal))
                {
                    anyBlockTensors = true;
                    int dot = name.IndexOf('.', 4);
                    if (dot > 4 && long.TryParse(name.AsSpan(4, dot - 4), out long blkIdx))
                    {
                        maxBlockSeen = Math.Max(maxBlockSeen, blkIdx);
                    }
                }

                double bytesPerElement = GetGgmlTypeBytesPerElement(ggmlType);
                if (bytesPerElement > 0)
                {
                    long estSize = (long)Math.Ceiling(numel * bytesPerElement);
                    maxDataEnd = Math.Max(maxDataEnd, (long)offset + estSize);
                }
            }

            var metadata = ParseCached(filePath);
            long declaredBlocks = metadata?.BlockCount ?? 0;

            if (anyBlockTensors && declaredBlocks > 0 && maxBlockSeen >= 0 && maxBlockSeen < declaredBlocks - 1)
            {
                return new GgufIntegrityResult(false,
                    $"Metadata declares {declaredBlocks} transformer blocks (blk.0..blk.{declaredBlocks - 1}) but the tensor table " +
                    $"only contains tensors through blk.{maxBlockSeen}. The model file is truncated or was converted from an " +
                    $"incomplete model — re-download it.");
            }

            if (maxDataEnd > fileLength)
            {
                return new GgufIntegrityResult(false,
                    $"Tensor data region ends at byte {maxDataEnd} but the file is only {fileLength} bytes long. " +
                    $"The file is truncated — re-download it.");
            }

            return new GgufIntegrityResult(true, null);
        }
        catch (EndOfStreamException)
        {
            return new GgufIntegrityResult(false,
                "Unexpected end of file while reading GGUF structure. The file is truncated or corrupt — re-download it.");
        }
        catch (Exception ex)
        {
            return new GgufIntegrityResult(false, $"Failed to read GGUF structure: {ex.Message}");
        }
    }

    /// <summary>
    /// Bytes per element for common GGML tensor types (block-quantized types return their
    /// average bytes per element). Unknown types return 0, which skips the data-bounds check
    /// for that tensor rather than risking a false "truncated" report.
    /// </summary>
    private static double GetGgmlTypeBytesPerElement(uint type)
    {
        return type switch
        {
            0 => 4.0,          // F32
            1 => 2.0,          // F16
            2 => 18.0 / 32.0,  // Q4_0
            3 => 20.0 / 32.0,  // Q4_1
            6 => 22.0 / 32.0,  // Q5_0
            7 => 24.0 / 32.0,  // Q5_1
            8 => 34.0 / 32.0,  // Q8_0
            9 => 36.0 / 32.0,  // Q8_1
            10 => 84.0 / 256.0,  // Q2_K
            11 => 110.0 / 256.0, // Q3_K
            12 => 144.0 / 256.0, // Q4_K
            13 => 176.0 / 256.0, // Q5_K
            14 => 210.0 / 256.0, // Q6_K
            15 => 292.0 / 256.0, // Q8_K
            16 => 40.0 / 256.0,  // IQ2_XXS
            17 => 48.0 / 256.0,  // IQ2_XS
            18 => 56.0 / 256.0,  // IQ3_XXS
            19 => 24.0 / 256.0,  // IQ1_S
            20 => 18.0 / 32.0,   // IQ4_NL
            21 => 88.0 / 256.0,  // IQ3_S
            22 => 52.0 / 256.0,  // IQ2_S
            23 => 64.0 / 256.0,  // IQ4_XS
            24 => 30.0 / 256.0,  // IQ1_M
            25 => 108.0 / 256.0, // IQ3_M
            26 => 124.0 / 256.0, // IQ3_L
            27 => 60.0 / 256.0,  // IQ2_M
            29 => 2.0,           // BF16
            32 => 34.0 / 256.0,  // TQ1_0
            33 => 68.0 / 256.0,  // TQ2_0
            _ => 0.0
        };
    }

    private static string GetFileTypeString(uint fileType)
    {
        return fileType switch
        {
            0 => "F32",
            1 => "F16",
            2 => "Q4_0",
            3 => "Q4_1",
            7 => "Q8_0",
            8 => "Q5_0",
            9 => "Q5_1",
            10 => "Q2_K",
            11 => "Q3_K_S",
            12 => "Q3_K_M",
            13 => "Q3_K_L",
            14 => "Q4_K_S",
            15 => "Q4_K_M",
            16 => "Q5_K_S",
            17 => "Q5_K_M",
            18 => "Q6_K",
            19 => "IQ2_XXS",
            20 => "IQ2_XS",
            21 => "Q2_K_S",
            22 => "IQ3_XS",
            23 => "IQ3_S",
            24 => "IQ2_S",
            25 => "IQ2_M",
            26 => "IQ1_S",
            27 => "IQ1_M",
            28 => "IQ4_NL",
            29 => "IQ4_XS",
            30 => "IQ3_M",
            _ => $"Type_{fileType}"
        };
    }

    private static string ExtractQuantFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;

        var knownQuants = new[]
        {
            "Q4_K_M", "Q4_K_S", "Q4_0", "Q4_1",
            "Q8_0", "Q5_K_M", "Q5_K_S", "Q5_0", "Q5_1",
            "Q6_K", "Q2_K", "Q3_K_M", "Q3_K_S", "Q3_K_L",
            "IQ4_NL", "IQ4_XS", "IQ3_S", "IQ3_M", "IQ2_XXS", "IQ2_XS",
            "F16", "F32"
        };

        foreach (var quant in knownQuants)
        {
            if (fileName.Contains(quant, StringComparison.OrdinalIgnoreCase))
            {
                return quant;
            }
        }

        return string.Empty;
    }
}

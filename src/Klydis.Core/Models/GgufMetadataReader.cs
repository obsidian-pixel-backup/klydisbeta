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
    string? QuantizationType
);

/// <summary>
/// A pure C# binary parser for GGUF file headers.
/// </summary>
public static class GgufMetadataReader
{
    private const uint GgufMagic = 0x46554747; // 'GGUF' in little endian

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
                return ParseKvs(reader, kvCountV1);
            }
            else
            {
                // Version 2 and 3 use uint64
                ulong tensorCount = reader.ReadUInt64();
                ulong kvCount = reader.ReadUInt64();
                return ParseKvs(reader, kvCount);
            }
        }
        catch
        {
            return null;
        }
    }

    private static GgufMetadata ParseKvs(BinaryReader reader, ulong kvCount)
    {
        string? architecture = null;
        long? blockCount = null;
        long? contextLength = null;
        long? embeddingLength = null;
        long? headCount = null;
        long? headCountKv = null;
        long? vocabSize = null;
        string? quantizationType = null;

        for (ulong i = 0; i < kvCount; i++)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                break;

            string key = ReadGgufString(reader);
            var valueType = (GgufValueType)reader.ReadUInt32();
            object? value = ReadGgufValue(reader, valueType);

            if (value == null) continue;

            if (key == "general.architecture" && value is string archStr)
                architecture = archStr;
            else if (key == "general.quantization_version" && value is IConvertible)
                quantizationType = $"v{Convert.ToUInt32(value)}"; 
            else if (key == "general.file_type" && value is IConvertible ftVal)
                quantizationType = GetFileTypeString(Convert.ToUInt32(ftVal));
            
            if (architecture != null)
            {
                if (key == $"{architecture}.block_count" && value is IConvertible bc)
                    blockCount = Convert.ToInt64(bc);
                else if (key == $"{architecture}.context_length" && value is IConvertible cl)
                    contextLength = Convert.ToInt64(cl);
                else if (key == $"{architecture}.embedding_length" && value is IConvertible el)
                    embeddingLength = Convert.ToInt64(el);
                else if (key == $"{architecture}.attention.head_count" && value is IConvertible hc)
                    headCount = Convert.ToInt64(hc);
                else if (key == $"{architecture}.attention.head_count_kv" && value is IConvertible hckv)
                    headCountKv = Convert.ToInt64(hckv);
                else if (key == $"{architecture}.vocab_size" && value is IConvertible vs)
                    vocabSize = Convert.ToInt64(vs);
            }
        }

        return new GgufMetadata(
            Architecture: architecture,
            BlockCount: blockCount,
            ContextLength: contextLength,
            EmbeddingLength: embeddingLength,
            HeadCount: headCount,
            HeadCountKv: headCountKv,
            VocabSize: vocabSize,
            QuantizationType: quantizationType
        );
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
        
        long itemSize = GetFixedItemSize(itemType);
        if (itemSize > 0)
        {
            reader.BaseStream.Seek((long)(count * (ulong)itemSize), SeekOrigin.Current);
        }
        else
        {
            for (ulong i = 0; i < count; i++)
            {
                if (itemType == GgufValueType.String)
                {
                    ulong len = reader.ReadUInt64();
                    reader.BaseStream.Seek((long)len, SeekOrigin.Current);
                }
                else if (itemType == GgufValueType.Array)
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

    private static string GetFileTypeString(uint fileType)
    {
        return fileType switch
        {
            0 => "F32",
            1 => "F16",
            2 => "Q4_0",
            3 => "Q4_1",
            8 => "Q5_0",
            9 => "Q5_1",
            10 => "Q2_K",
            12 => "Q3_K_M",
            14 => "Q4_K_M",
            15 => "Q4_K_S",
            16 => "Q5_K_M",
            17 => "Q6_K",
            18 => "Q8_0",
            _ => $"Type_{fileType}"
        };
    }
}

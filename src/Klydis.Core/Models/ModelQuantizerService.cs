using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LLama;
using LLama.Native;

namespace Klydis.Core.Models;

/// <summary>
/// Service for performing engine-side GGUF model quantization to 4-bit precision (e.g. Q4_K_M or Q4_0).
/// </summary>
public class ModelQuantizerService
{
    private readonly ILogger<ModelQuantizerService>? _logger;

    public ModelQuantizerService(ILogger<ModelQuantizerService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Asynchronously quantizes an input GGUF model file to a target 4-bit precision (default: Q4_K_M).
    /// If outputGgufPath is omitted or null, an output path will be automatically generated.
    /// </summary>
    /// <param name="inputGgufPath">Path to the source GGUF file.</param>
    /// <param name="outputGgufPath">Path where the quantized GGUF file will be written (optional).</param>
    /// <param name="targetQuantType">Target quantization format (default: Q4_K_M).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if quantization succeeded, false otherwise.</returns>
    public Task<bool> QuantizeTo4BitAsync(
        string inputGgufPath,
        string? outputGgufPath = null,
        string targetQuantType = "Q4_K_M",
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(inputGgufPath))
            {
                _logger?.LogError("Input model file not found: {Path}", inputGgufPath);
                return false;
            }

            try
            {
                string quantTag = string.IsNullOrWhiteSpace(targetQuantType) ? "Q4_K_M" : targetQuantType;
                if (string.IsNullOrWhiteSpace(outputGgufPath))
                {
                    string dir = Path.GetDirectoryName(inputGgufPath) ?? string.Empty;
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(inputGgufPath);
                    outputGgufPath = Path.Combine(dir, $"{nameWithoutExt}-{quantTag}.gguf");
                }

                _logger?.LogInformation("Starting 4-bit engine quantization of {InputPath} -> {OutputPath} (Format: {QuantType})",
                    inputGgufPath, outputGgufPath, quantTag);

                string? outputDir = Path.GetDirectoryName(outputGgufPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // LLamaQuantizer.Quantize quantizes an existing GGUF model file natively
                bool success = LLamaQuantizer.Quantize(inputGgufPath, outputGgufPath, quantTag);
                if (success)
                {
                    _logger?.LogInformation("Engine-side 4-bit quantization completed successfully: {OutputPath}", outputGgufPath);
                }
                else
                {
                    _logger?.LogError("Native LLamaQuantizer failed to quantize model: {InputPath}", inputGgufPath);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to quantize model {InputPath}", inputGgufPath);
                return false;
            }
        }, ct);
    }
}

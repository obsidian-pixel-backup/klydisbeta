using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Klydis.Core.Inference;
using Klydis.Core.Hardware;
using LLama.Native;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Initializing LLamaSharp configurations...");
        NativeLibraryConfig.All.WithCuda().WithVulkan();
        
        var logger = NullLogger<InferenceEngine>.Instance;
        var engine = new InferenceEngine(logger);
        
        string modelPath = @"C:\Users\corne\.klydis\models\Qwythos-9B-Claude-Mythos-5-1M-Q4_K_M.gguf";
        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"Model file not found at {modelPath}");
            return;
        }

        Console.WriteLine("Loading model...");
        var plan = new OffloadPlan(
            GpuLayers: 32,
            CpuLayers: 0,
            EstimatedVramUsageMb: 4000,
            RecommendedContextSize: 2048,
            RecommendedBatchSize: 512,
            StrategyUsed: OffloadStrategyType.FullGpu
        );
        
        await engine.LoadModelAsync(modelPath, plan);
        Console.WriteLine("Model loaded successfully.");

        // 1. Simulating context consolidation: GenerateTextAsync
        Console.WriteLine("\n=== Simulating Context Consolidation (GenerateTextAsync) ===");
        try
        {
            var summaryPrompt = "Task: Condense the following interaction to a single line: User: Hello, what can you do? Assistant: I am Klydis, a local AI assistant.";
            Console.WriteLine($"Prompt: {summaryPrompt}");
            
            var summary = await engine.GenerateTextAsync(summaryPrompt, CancellationToken.None);
            Console.WriteLine($"Generated Summary: {summary.Trim()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GenerateTextAsync failed: {ex}");
        }

        // 2. Simulating subsequent generation: StreamTokensAsync
        Console.WriteLine("\n=== Simulating Subsequent Generation (StreamTokensAsync) ===");
        try
        {
            var nextPrompt = "<|im_start|>system\nYou are Klydis, a helpful AI assistant.<|im_end|>\n<|im_start|>user\nWho are you?<|im_end|>\n<|im_start|>assistant\n";
            var stopTokens = new[] { "<|im_end|>", "<|im_start|>" };
            
            Console.WriteLine("Streaming response tokens:");
            await foreach (var token in engine.StreamTokensAsync(nextPrompt, stopTokens, 100, CancellationToken.None))
            {
                Console.Write(token);
            }
            Console.WriteLine();
            Console.WriteLine("Generation completed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"StreamTokensAsync failed: {ex}");
        }
    }
}

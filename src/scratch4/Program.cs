using System;
using System.Collections.Generic;
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

        var templateEngine = new Klydis.Core.Chat.PromptTemplateEngine();
        var templateType = Klydis.Core.Chat.ChatTemplate.Llama3;
        
        // Turn 1
        var sysPrompt = "You are a helpful AI assistant.";
        var userMsg1 = "My name is Cornelius. Remember it.";
        
        var messages = new List<Klydis.Core.Chat.ChatMessage>
        {
            new Klydis.Core.Chat.ChatMessage(Klydis.Core.Chat.ChatRole.System, sysPrompt),
            new Klydis.Core.Chat.ChatMessage(Klydis.Core.Chat.ChatRole.User, userMsg1)
        };
        
        var prompt1 = templateEngine.ApplyTemplate(messages, templateType);
        Console.WriteLine($"\n--- Turn 1 Prompt ---\n{prompt1}\n---------------------");
        
        Console.WriteLine("Generating turn 1 response...");
        var stopTokens = templateEngine.GetStopTokens(templateType);
        var sb1 = new StringBuilder();
        
        await foreach (var token in engine.StreamTokensAsync(prompt1, stopTokens, 100, CancellationToken.None))
        {
            Console.Write(token);
            sb1.Append(token);
        }
        Console.WriteLine();
        Console.WriteLine($"Turn 1 generation complete. Response length: {sb1.Length}");

        // Turn 2
        var userMsg2 = "What is my name?";
        messages.Add(new Klydis.Core.Chat.ChatMessage(Klydis.Core.Chat.ChatRole.Assistant, sb1.ToString()));
        messages.Add(new Klydis.Core.Chat.ChatMessage(Klydis.Core.Chat.ChatRole.User, userMsg2));
        
        var prompt2 = templateEngine.ApplyTemplate(messages, templateType);
        Console.WriteLine($"\n--- Turn 2 Prompt ---\n{prompt2}\n---------------------");
        
        Console.WriteLine("Generating turn 2 response...");
        var sb2 = new StringBuilder();
        
        try
        {
            await foreach (var token in engine.StreamTokensAsync(prompt2, stopTokens, 100, CancellationToken.None))
            {
                Console.Write(token);
                sb2.Append(token);
            }
            Console.WriteLine();
            Console.WriteLine("Turn 2 generation complete!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nException caught in main: {ex}");
        }
    }
}

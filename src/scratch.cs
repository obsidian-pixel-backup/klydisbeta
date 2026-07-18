using System;
using LLama.Common;

class Program
{
    static void Main()
    {
        var p = new InferenceParams();
        Console.WriteLine($"MaxTokens: {p.MaxTokens}");
        Console.WriteLine($"TokensKeep: {p.TokensKeep}");
    }
}

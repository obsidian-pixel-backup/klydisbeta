using System;
using System.Linq;
using System.Reflection;
using LLama.Native;
using LLama;

class Program
{
    static void Main()
    {
        Console.WriteLine("--- NativeApi methods ---");
        var methods = typeof(NativeApi).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var m in methods.Where(m => m.Name.Contains("abort", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("cancel", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("set", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(m.Name);
        }

        Console.WriteLine("--- SafeLLamaContextHandle methods ---");
        var handleMethods = typeof(SafeLLamaContextHandle).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var m in handleMethods.Where(m => m.Name.Contains("abort", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("cancel", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("set", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(m.Name);
        }
    }
}

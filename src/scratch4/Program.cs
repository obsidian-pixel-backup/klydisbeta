using System;
using System.Reflection;
using LLama;

class Program {
    static void Main() {
        var methods = typeof(InteractiveExecutor).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach(var m in methods) {
            Console.WriteLine(m.Name);
        }
    }
}

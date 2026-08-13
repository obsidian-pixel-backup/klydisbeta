using System;
using System.Linq;
using System.Reflection;

var asm = Assembly.LoadFrom(@"C:\Users\corne\.nuget\packages\modelcontextprotocol\2.1.0\lib\net10.0\ModelContextProtocol.dll");

Console.WriteLine("--- types named *ServiceCollection* ---");
foreach (var t in asm.GetTypes().Where(t => t.Name.Contains("ServiceCollection")))
{
    Console.WriteLine("TYPE: " + t.FullName);
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).OrderBy(m => m.Name))
        Console.WriteLine($"   {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
}

Console.WriteLine("--- IHostedService implementers (any visibility) ---");
foreach (var t in asm.GetTypes().Where(t => t.GetInterfaces().Any(i => i.Name == "IHostedService")))
    Console.WriteLine("HOSTED: " + t.FullName);

Console.WriteLine("--- McpServer constructors & registration info ---");
var server = asm.GetType("ModelContextProtocol.Server.McpServer");
foreach (var c in server.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
    Console.WriteLine($"  CTOR: ({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) [{(c.IsPublic ? "public" : "nonpublic")}]");

Console.WriteLine("--- IMcpServerBuilder methods ---");
var b = asm.GetType("Microsoft.Extensions.DependencyInjection.IMcpServerBuilder");
foreach (var m in b.GetMethods().OrderBy(m => m.Name))
    Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");

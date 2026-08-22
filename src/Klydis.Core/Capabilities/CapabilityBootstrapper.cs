using System;
using Klydis.Core.Capabilities.Providers;
using Klydis.Core.Hardware;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Capabilities;

/// <summary>
/// Bootstrapper for initializing and populating standard machine capabilities into the registry and graph.
/// </summary>
public static class CapabilityBootstrapper
{
    /// <summary>
    /// Creates and configures a default CapabilityRegistry populated with all standard machine capabilities.
    /// </summary>
    public static CapabilityRegistry CreateDefaultRegistry(
        SystemProfiler? systemProfiler = null,
        GpuProfiler? gpuProfiler = null,
        ILogger<CapabilityRegistry>? logger = null)
    {
        var registry = new CapabilityRegistry(logger);
        RegisterAllStandardCapabilities(registry, systemProfiler, gpuProfiler);
        return registry;
    }

    /// <summary>
    /// Registers all standard machine capabilities into an existing registry.
    /// </summary>
    public static void RegisterAllStandardCapabilities(
        ICapabilityRegistry registry,
        SystemProfiler? systemProfiler = null,
        GpuProfiler? gpuProfiler = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Hardware
        registry.Register(new CpuInspectCapability(systemProfiler));
        registry.Register(new GpuInspectCapability(gpuProfiler));
        registry.Register(new RamInspectCapability(systemProfiler));
        registry.Register(new DiskInspectCapability());
        registry.Register(new DisplayEnumerateCapability());
        registry.Register(new BatteryInspectCapability());

        // Operating System
        registry.Register(new SystemInfoCapability());
        registry.Register(new EnvironmentGetCapability());
        registry.Register(new ProcessEnumerateCapability());
        registry.Register(new ServicesEnumerateCapability());

        // Network
        registry.Register(new NetworkInterfacesCapability());
        registry.Register(new NetworkPingCapability());

        // Filesystem
        registry.Register(new FilesystemReadCapability());
        registry.Register(new FilesystemWriteCapability());
        registry.Register(new FilesystemEditCapability());
        registry.Register(new FilesystemDeleteCapability());
        registry.Register(new FilesystemCopyCapability());
        registry.Register(new FilesystemMoveCapability());
        registry.Register(new FilesystemMkdirCapability());
        registry.Register(new FilesystemListCapability());
        registry.Register(new FilesystemSearchCapability());
        registry.Register(new FilesystemMetadataCapability());

        // Process Lifecycle & Filtering
        registry.Register(new ProcessStartCapability());
        registry.Register(new ProcessKillCapability());
        registry.Register(new ProcessInspectCapability());
        registry.Register(new ProcessWaitCapability());
        registry.Register(new SystemTopProcessesCapability());
        registry.Register(new ProcessFindCapability());

        // Shell Escape Hatches
        registry.Register(new ShellPowershellCapability());
        registry.Register(new ShellCmdCapability());

        // Desktop & Window Automation
        registry.Register(new DesktopWindowsEnumerateCapability());
        registry.Register(new DesktopWindowFocusCapability());
        registry.Register(new DesktopWindowMoveCapability());
        registry.Register(new DesktopWindowMinimizeCapability());
        registry.Register(new DesktopWindowMaximizeCapability());
        registry.Register(new DesktopWindowCloseCapability());
        registry.Register(new DesktopClipboardGetCapability());
        registry.Register(new DesktopClipboardSetCapability());

        // Git & Development
        registry.Register(new GitStatusCapability());
        registry.Register(new GitDiffCapability());
        registry.Register(new GitLogCapability());

        // Local AI
        registry.Register(new AiGpuInspectCapability(gpuProfiler));
        registry.Register(new AiModelsListCapability());
    }

    /// <summary>
    /// Legacy compatibility alias for Phase 1 registration.
    /// </summary>
    public static void RegisterPerceptionCapabilities(
        ICapabilityRegistry registry,
        SystemProfiler? systemProfiler = null,
        GpuProfiler? gpuProfiler = null)
    {
        RegisterAllStandardCapabilities(registry, systemProfiler, gpuProfiler);
    }

    /// <summary>
    /// Builds a capability graph initialized with dependencies and taxonomy.
    /// </summary>
    public static CapabilityGraph CreateDefaultGraph(ICapabilityRegistry registry)
    {
        var graph = new CapabilityGraph(registry);

        // Establish core graph dependencies
        graph.AddPrerequisite("desktop.window.move", "desktop.windows.enumerate");
        graph.AddPrerequisite("desktop.window.focus", "desktop.windows.enumerate");
        graph.AddPrerequisite("process.kill", "os.processes.enumerate");
        graph.AddPrerequisite("filesystem.edit", "filesystem.read");

        return graph;
    }
}

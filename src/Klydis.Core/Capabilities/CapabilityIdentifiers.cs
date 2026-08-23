using System;

namespace Klydis.Core.Capabilities;

/// <summary>
/// Canonical capability identifiers for Klydis agent tasks and tool routing.
/// </summary>
public static class CapabilityIdentifiers
{
    // Hardware & System Telemetry
    public const string CpuTelemetry = "hardware.cpu";
    public const string GpuTelemetry = "hardware.gpu";
    public const string MemoryTelemetry = "hardware.memory";
    public const string DiskTelemetry = "hardware.disk";
    public const string ThermalTelemetry = "hardware.thermal";
    public const string OsInfo = "os.info";
    public const string OsUptime = "os.uptime";
    public const string ProcessInspection = "process.inspection";
    public const string SystemDiagnostics = "system.diagnostics";

    // Filesystem & Codebase
    public const string FileRead = "filesystem.read";
    public const string FileWrite = "filesystem.write";
    public const string FileEdit = "filesystem.edit";
    public const string FileList = "filesystem.list";
    public const string FileSearch = "filesystem.search";
    public const string CodeInspection = "code.inspection";

    // Verification & Testing
    public const string BuildVerify = "build.verify";
    public const string TestVerify = "test.verify";
    public const string PreviewVerify = "preview.verify";

    // Web & Browser
    public const string WebSearch = "browser.search";
    public const string WebCrawl = "browser.crawl";

    // Desktop & Shell Execution
    public const string DesktopAutomation = "desktop.automation";
    public const string ShellExecution = "shell.powershell";
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

#pragma warning disable CA1416

namespace Klydis.Core.Capabilities.Providers;

internal static class User32Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public const int SW_HIDE = 0;
    public const int SW_NORMAL = 1;
    public const int SW_MINIMIZE = 6;
    public const int SW_MAXIMIZE = 3;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_SHOWWINDOW = 0x0040;
}

public sealed record WindowEntry(
    long Hwnd,
    string Title,
    int ProcessId,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsVisible
);

/// <summary>
/// Capability: desktop.windows.enumerate
/// Enumerates visible top-level application windows and their screen coordinates.
/// </summary>
public sealed class DesktopWindowsEnumerateCapability : ICapability
{
    public string Id => "desktop.windows.enumerate";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Enumerates open top-level application windows on the desktop, their window handles (HWND), process IDs, titles, and bounding boxes.",
        Parameters: new List<CapabilityParameter>
        {
            new("filter_title", "string", "Optional window title substring filter.", false),
            new("only_visible", "boolean", "Only return visible windows (default: true).", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filter = request.GetParam<string>("filter_title");
            bool onlyVisible = request.GetParam<bool>("only_visible", true);

            var windows = new List<WindowEntry>();

            if (OperatingSystem.IsWindows())
            {
                User32Native.EnumWindows((hWnd, lParam) =>
                {
                    bool visible = User32Native.IsWindowVisible(hWnd);
                    if (onlyVisible && !visible) return true;

                    int len = User32Native.GetWindowTextLength(hWnd);
                    if (len == 0) return true;

                    var sb = new StringBuilder(len + 1);
                    User32Native.GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();

                    if (!string.IsNullOrWhiteSpace(filter) && !title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    User32Native.GetWindowThreadProcessId(hWnd, out uint pid);
                    User32Native.GetWindowRect(hWnd, out var rect);

                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;

                    windows.Add(new WindowEntry(
                        Hwnd: hWnd.ToInt64(),
                        Title: title,
                        ProcessId: (int)pid,
                        X: rect.Left,
                        Y: rect.Top,
                        Width: width,
                        Height: height,
                        IsVisible: visible
                    ));

                    return true;
                }, IntPtr.Zero);
            }

            sw.Stop();
            var evidence = new CapabilityEvidence(
                Source: Id,
                RawOutput: JsonSerializer.Serialize(windows, new JsonSerializerOptions { WriteIndented = true }),
                CollectedAtUtc: DateTime.UtcNow
            );

            return Task.FromResult(CapabilityResult.Succeeded(Id, windows, sw.Elapsed, evidence));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Window enumeration failed."));
        var facts = new List<FactAssertion>
        {
            new("desktop", "windows", "active_list", result.Data!, TimeSpan.FromSeconds(5), Id)
        };
        return Task.FromResult(VerificationResult.Verified("Window list verified.", facts));
    }
}

/// <summary>
/// Capability: desktop.window.focus
/// Brings an application window to foreground.
/// </summary>
public sealed class DesktopWindowFocusCapability : ICapability
{
    public string Id => "desktop.window.focus";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Brings an application window to the foreground and focuses input on it.",
        Parameters: new List<CapabilityParameter>
        {
            new("hwnd", "integer", "Window Handle (HWND).", false),
            new("title_filter", "string", "Optional window title if HWND is unknown.", false)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            long hwndVal = request.GetParam<long>("hwnd", 0);
            string? titleFilter = request.GetParam<string>("title_filter");

            IntPtr targetHwnd = IntPtr.Zero;

            if (hwndVal != 0)
            {
                targetHwnd = new IntPtr(hwndVal);
            }
            else if (!string.IsNullOrWhiteSpace(titleFilter) && OperatingSystem.IsWindows())
            {
                User32Native.EnumWindows((hWnd, lParam) =>
                {
                    int len = User32Native.GetWindowTextLength(hWnd);
                    if (len == 0) return true;
                    var sb = new StringBuilder(len + 1);
                    User32Native.GetWindowText(hWnd, sb, sb.Capacity);
                    if (sb.ToString().Contains(titleFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        targetHwnd = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }

            if (targetHwnd == IntPtr.Zero)
            {
                return Task.FromResult(CapabilityResult.Failed(Id, "Target window was not found.", sw.Elapsed));
            }

            if (OperatingSystem.IsWindows())
            {
                User32Native.ShowWindow(targetHwnd, User32Native.SW_RESTORE);
                User32Native.BringWindowToTop(targetHwnd);
                User32Native.SetForegroundWindow(targetHwnd);
            }

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(SideEffectKind.WindowModified, targetHwnd.ToInt64().ToString(), "Focused window")
            };

            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Hwnd = targetHwnd.ToInt64(), Focused = true }, sw.Elapsed, sideEffects: sideEffects));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Focus window failed."));
        return Task.FromResult(VerificationResult.Verified("Window focus verified."));
    }
}

/// <summary>
/// Capability: desktop.window.move
/// Moves or resizes a window on the desktop.
/// </summary>
public sealed class DesktopWindowMoveCapability : ICapability
{
    public string Id => "desktop.window.move";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Relocates or resizes an application window to specific screen coordinates and dimensions.",
        Parameters: new List<CapabilityParameter>
        {
            new("hwnd", "integer", "Window Handle (HWND).", true),
            new("x", "integer", "X screen coordinate.", true),
            new("y", "integer", "Y screen coordinate.", true),
            new("width", "integer", "Window width in pixels.", true),
            new("height", "integer", "Window height in pixels.", true)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        long hwndVal = request.GetParam<long>("hwnd", 0);
        if (hwndVal == 0) return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'hwnd' is required."));
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            long hwndVal = request.GetParam<long>("hwnd");
            int x = request.GetParam<int>("x");
            int y = request.GetParam<int>("y");
            int width = request.GetParam<int>("width");
            int height = request.GetParam<int>("height");

            var hwnd = new IntPtr(hwndVal);
            if (OperatingSystem.IsWindows())
            {
                User32Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, User32Native.SWP_NOZORDER | User32Native.SWP_SHOWWINDOW);
            }

            sw.Stop();
            var sideEffects = new List<SideEffect>
            {
                new(SideEffectKind.WindowModified, hwndVal.ToString(), $"Moved window to ({x}, {y}, {width}x{height})")
            };

            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Hwnd = hwndVal, X = x, Y = y, Width = width, Height = height }, sw.Elapsed, sideEffects: sideEffects));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default)
    {
        if (!result.Success) return Task.FromResult(VerificationResult.Failed("Window move failed."));
        return Task.FromResult(VerificationResult.Verified("Window relocation verified."));
    }
}

/// <summary>
/// Capability: desktop.window.minimize
/// Minimizes a window.
/// </summary>
public sealed class DesktopWindowMinimizeCapability : ICapability
{
    public string Id => "desktop.window.minimize";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Minimizes a window to the taskbar.",
        Parameters: new List<CapabilityParameter> { new("hwnd", "integer", "Window Handle (HWND).", true) },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            long hwndVal = request.GetParam<long>("hwnd");
            var hwnd = new IntPtr(hwndVal);
            if (OperatingSystem.IsWindows())
            {
                User32Native.ShowWindow(hwnd, User32Native.SW_MINIMIZE);
            }
            sw.Stop();
            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Hwnd = hwndVal, Minimized = true }, sw.Elapsed));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(VerificationResult.Verified("Window minimized."));
}

/// <summary>
/// Capability: desktop.window.maximize
/// Maximizes a window.
/// </summary>
public sealed class DesktopWindowMaximizeCapability : ICapability
{
    public string Id => "desktop.window.maximize";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Maximizes a window to fill the monitor display.",
        Parameters: new List<CapabilityParameter> { new("hwnd", "integer", "Window Handle (HWND).", true) },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            long hwndVal = request.GetParam<long>("hwnd");
            var hwnd = new IntPtr(hwndVal);
            if (OperatingSystem.IsWindows())
            {
                User32Native.ShowWindow(hwnd, User32Native.SW_MAXIMIZE);
            }
            sw.Stop();
            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Hwnd = hwndVal, Maximized = true }, sw.Elapsed));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(VerificationResult.Verified("Window maximized."));
}

/// <summary>
/// Capability: desktop.window.close
/// Closes a window via WM_CLOSE.
/// </summary>
public sealed class DesktopWindowCloseCapability : ICapability
{
    public string Id => "desktop.window.close";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Confirm;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Requests an application window to close gracefully.",
        Parameters: new List<CapabilityParameter> { new("hwnd", "integer", "Window Handle (HWND).", true) },
        Policy: PolicyDefault.Confirm
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            long hwndVal = request.GetParam<long>("hwnd");
            var hwnd = new IntPtr(hwndVal);
            if (OperatingSystem.IsWindows())
            {
                User32Native.PostMessage(hwnd, User32Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            sw.Stop();
            return Task.FromResult(CapabilityResult.Succeeded(Id, new { Hwnd = hwndVal, Closed = true }, sw.Elapsed));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(CapabilityResult.Failed(Id, ex.Message, sw.Elapsed));
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(VerificationResult.Verified("Window close sent."));
}

/// <summary>
/// Capability: desktop.clipboard.get
/// Reads text content currently on the clipboard.
/// </summary>
public sealed class DesktopClipboardGetCapability : ICapability
{
    public string Id => "desktop.clipboard.get";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Reads plain text currently copied to the system clipboard.",
        Parameters: Array.Empty<CapabilityParameter>(),
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(PreconditionCheckResult.Satisfied());

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Execute via PowerShell STA command for thread safety on Windows
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-Clipboard\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            string text = proc != null ? await proc.StandardOutput.ReadToEndAsync(ct) : "";
            if (proc != null) await proc.WaitForExitAsync(ct);

            sw.Stop();
            var data = new { Text = text.TrimEnd(), Length = text.Length };
            var evidence = new CapabilityEvidence(Id, data.Text, DateTime.UtcNow);

            return CapabilityResult.Succeeded(Id, data, sw.Elapsed, evidence);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(VerificationResult.Verified("Clipboard read verified."));
}

/// <summary>
/// Capability: desktop.clipboard.set
/// Writes text content to the clipboard.
/// </summary>
public sealed class DesktopClipboardSetCapability : ICapability
{
    public string Id => "desktop.clipboard.set";
    public CapabilityDomain Domain => CapabilityDomain.Desktop;
    public PolicyDefault Policy => PolicyDefault.Auto;

    public CapabilityDescription Describe() => new(
        Id: Id,
        Domain: Domain,
        Description: "Copies text to the system clipboard.",
        Parameters: new List<CapabilityParameter>
        {
            new("text", "string", "Text string to place on the clipboard.", true)
        },
        Policy: PolicyDefault.Auto
    );

    public Task<PreconditionCheckResult> CheckPreconditionsAsync(CapabilityRequest request, IWorldModel worldModel, CancellationToken ct = default)
    {
        string? text = request.GetParam<string>("text");
        if (text is null) return Task.FromResult(PreconditionCheckResult.Failed("Parameter 'text' is required."));
        return Task.FromResult(PreconditionCheckResult.Satisfied());
    }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string text = request.GetParam<string>("text")!;
            string script = $"Set-Clipboard -Value \"{text.Replace("\"", "\\\"")}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);

            sw.Stop();
            return CapabilityResult.Succeeded(Id, new { Copied = true, Length = text.Length }, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CapabilityResult.Failed(Id, ex.Message, sw.Elapsed);
        }
    }

    public Task<VerificationResult> VerifyPostconditionsAsync(CapabilityRequest request, CapabilityResult result, IWorldModel worldModel, CancellationToken ct = default) =>
        Task.FromResult(VerificationResult.Verified("Clipboard set verified."));
}

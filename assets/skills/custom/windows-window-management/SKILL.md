---
name: windows-window-management
description: Desktop window control, bringing apps to foreground, minimizing/maximizing, snapping windows, multi-monitor alignment.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Window Management

Interacting with active desktop application windows using Win32 API calls in PowerShell.

## Foreground Window Activation

```powershell
$code = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);'
$type = Add-Type -MemberDefinition $code -Name "Win32Utils" -Namespace "Win32" -PassThru
$proc = Get-Process -Name "chrome" | Select-Object -First 1
if ($proc) { $type::SetForegroundWindow($proc.MainWindowHandle) }
```

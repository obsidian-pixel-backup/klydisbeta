---
name: windows-taskbar-start-menu
description: Finding and launching pinned taskbar apps, Start Menu shortcuts, and UWP/MS Store packages (Shell:AppsFolder).
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Taskbar & Start Menu Manager

Launching packaged UWP and Start Menu applications.

## Launching Packaged UWP Apps

```powershell
Start-Process "shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
```

## Listing Installed Start Apps

```powershell
Get-StartApps | Select-Object -First 20 Name, AppID
```

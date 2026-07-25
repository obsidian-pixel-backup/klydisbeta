---
name: windows-app-launcher
description: Reliable launching of Windows desktop applications (Chrome, Edge, Steam, VS Code, Notepad, Spotify, Calculator) using verified Start-Process syntax and path resolution.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Application Launcher

When launching Windows applications, always use `Start-Process` via `run_command` with explicit `-FilePath` and `-ArgumentList` parameters.

## Core Rules for App Launching

1. **NEVER pass switches directly after app executable without `-ArgumentList`**:
   - ❌ WRONG: `start chrome --new-window https://youtube.com` (Causes `PositionalParameterNotFound` in PowerShell!)
   - ✅ CORRECT: `Start-Process -FilePath "chrome.exe" -ArgumentList "--new-window", "https://youtube.com"`

2. **Common Application Launch Templates**:
   - **Chrome**:
     `Start-Process -FilePath "chrome.exe" -ArgumentList "https://www.youtube.com"`
   - **Edge**:
     `Start-Process -FilePath "msedge.exe" -ArgumentList "https://www.google.com"`
   - **Notepad**:
     `Start-Process -FilePath "notepad.exe"`
   - **Calculator**:
     `Start-Process -FilePath "calc.exe"`
   - **VS Code**:
     `Start-Process -FilePath "code"`

3. **Locating Path if App Not in Environment PATH**:
   If an application path is unknown, search first before guessing:
   ```powershell
   Get-ChildItem -Path "C:\Program Files","C:\Program Files (x86)" -Recurse -Filter "appname.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 FullName
   ```

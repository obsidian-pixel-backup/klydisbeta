---
name: windows-gaming-steam-manager
description: Steam client management, launching Steam games via steam://rungameid/<id>, locating game install paths.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Gaming & Steam Manager

Launching Steam and Steam games.

## Guidelines

1. **Launch Steam Client**:
   ```powershell
   $steamPath = "C:\Program Files (x86)\Steam\steam.exe"
   if (Test-Path $steamPath) { Start-Process $steamPath } else { Start-Process "steam.exe" }
   ```
2. **Launch Steam Game via App ID**:
   ```powershell
   Start-Process "steam://rungameid/730"
   ```

---
name: windows-registry-query
description: Reading registry keys safely (Get-ItemProperty, HKLM, HKCU), checking installed software versions.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Registry Query

Safely inspecting Windows Registry property values.

## Guidelines

1. **Read Installed Software Registry**:
   ```powershell
   Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" | Select-Object -First 10 DisplayName, DisplayVersion
   ```

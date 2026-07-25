---
name: windows-terminal-powershell-expert
description: Best practices for writing robust, error-free PowerShell commands and scripts without parameter binding failures.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Terminal & PowerShell Standards

Rules for executing PowerShell commands cleanly without parameter binding errors or pipeline deadlocks.

## Critical Execution Rules

1. Never pass switches directly after executable name in `start` alias. Use `Start-Process -FilePath "app.exe" -ArgumentList "arg1", "arg2"`.
2. Do not fabricate non-existent cmdlets like `Get-AppProcessList`. Use standard cmdlets (`Get-Process`, `Get-Service`, `Get-ChildItem`).
3. For large directory scans, limit results with `Select-Object -First 20` to prevent 60-second timeouts.

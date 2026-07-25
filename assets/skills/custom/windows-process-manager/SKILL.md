---
name: windows-process-manager
description: Inspecting, monitoring, filtering, and killing system processes (Get-Process, Stop-Process) safely.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Process Manager

Monitoring and controlling running Windows tasks and processes.

## Guidelines

1. **Check if Process is Running**:
   ```powershell
   Get-Process -Name "chrome" -ErrorAction SilentlyContinue
   ```
2. **Find High CPU / Memory Processes**:
   ```powershell
   Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 10 Id, ProcessName, @{N='CPU(s)';E={$_.CPU}}, @{N='RAM(MB)';E={[math]::Round($_.WorkingSet64/1MB,2)}}
   ```
3. **Graceful or Force Stop Process**:
   ```powershell
   Stop-Process -Name "notepad" -ErrorAction SilentlyContinue
   ```

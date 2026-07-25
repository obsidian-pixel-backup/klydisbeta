---
name: windows-scheduled-tasks
description: Inspecting, running, and creating Task Scheduler jobs (Get-ScheduledTask, Start-ScheduledTask).
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Scheduled Tasks Manager

Querying Task Scheduler tasks.

## Guidelines

```powershell
Get-ScheduledTask | Where-Object State -eq "Ready" | Select-Object -First 10 TaskName, TaskPath
```

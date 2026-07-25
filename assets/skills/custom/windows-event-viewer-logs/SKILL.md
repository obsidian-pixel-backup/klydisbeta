---
name: windows-event-viewer-logs
description: Querying Windows Event Logs (Get-WinEvent) for application crashes, system errors, and diagnostic traces.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Event Viewer & Logs Diagnostic

Querying system and application event logs to diagnose crashes.

## Querying Recent Error Events

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2} -MaxEvents 5 | Select-Object TimeCreated, ProviderName, Message
```

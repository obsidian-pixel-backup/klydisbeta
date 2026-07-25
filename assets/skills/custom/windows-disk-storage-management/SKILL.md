---
name: windows-disk-storage-management
description: Drive usage analysis, free space monitoring, drive mounting, storage cleanup.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Disk & Storage Management

Inspecting disk space and volumes.

## Guidelines

```powershell
Get-Volume | Select-Object DriveLetter, FileSystemLabel, @{N='Free(GB)';E={[math]::Round($_.SizeRemaining/1GB,2)}}, @{N='Total(GB)';E={[math]::Round($_.Size/1GB,2)}}
```

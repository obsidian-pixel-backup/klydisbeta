---
name: windows-service-manager
description: Querying, starting, stopping, and restarting Windows services (Get-Service, Start-Service, Stop-Service).
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Service Manager

Managing background system services.

## Guidelines

1. **Query Running Services**:
   ```powershell
   Get-Service | Where-Object Status -eq "Running" | Select-Object -First 15 Name, DisplayName
   ```
2. **Start or Restart Service**:
   ```powershell
   Restart-Service -Name "wuauserv" -ErrorAction SilentlyContinue
   ```

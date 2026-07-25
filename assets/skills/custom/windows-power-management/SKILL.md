---
name: windows-power-management
description: Managing power schemes (powercfg), battery status, sleep, hibernate, display timeout, and lock screen settings.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Power Management

Querying power state and power plans.

## Guidelines

1. **Check Battery Status**:
   ```powershell
   Get-CimInstance -ClassName Win32_Battery | Select-Object EstimatedChargeRemaining, BatteryStatus
   ```
2. **List Active Power Schemes**:
   ```powershell
   powercfg /GetActiveScheme
   ```

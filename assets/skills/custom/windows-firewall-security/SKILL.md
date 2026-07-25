---
name: windows-firewall-security
description: Inspecting Windows Defender status, firewall rules (Get-NetFirewallRule), security center health.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Firewall & Security

Checking Windows Defender and Firewall rules.

## Guidelines

```powershell
Get-MpComputerStatus | Select-Object AntivirusEnabled, RealTimeProtectionEnabled
```

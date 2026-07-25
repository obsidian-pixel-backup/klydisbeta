---
name: windows-network-diagnostics
description: Inspecting IP config, network adapters, DNS flushing (Clear-DnsClientCache), ping tests, wifi connection status.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Network Diagnostics

Checking network interfaces, flushing DNS, testing connectivity.

## Guidelines

1. **IP Configuration**:
   ```powershell
   Get-NetIPAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, IPAddress
   ```
2. **Flush DNS Cache**:
   ```powershell
   Clear-DnsClientCache
   ```
3. **Test Host Reachability**:
   ```powershell
   Test-Connection -ComputerName "google.com" -Count 2
   ```

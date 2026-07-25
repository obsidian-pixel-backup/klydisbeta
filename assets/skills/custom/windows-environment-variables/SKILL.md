---
name: windows-environment-variables
description: Querying and modifying User and System environment variables (Get-ChildItem Env:, [Environment]::SetEnvironmentVariable).
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Environment Variables Manager

Querying environment variables.

## Guidelines

1. **List All Env Variables**:
   ```powershell
   Get-ChildItem Env: | Select-Object -First 15 Name, Value
   ```
2. **Read Specific Variable**:
   ```powershell
   $env:PATH
   ```

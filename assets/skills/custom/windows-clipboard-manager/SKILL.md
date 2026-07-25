---
name: windows-clipboard-manager
description: Reading, setting, and clearing Windows clipboard contents via PowerShell (Get-Clipboard, Set-Clipboard).
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Clipboard Manager

Reading and updating the system clipboard.

## Guidelines

1. **Read Clipboard**:
   ```powershell
   Get-Clipboard
   ```
2. **Set Clipboard Content**:
   ```powershell
   Set-Clipboard -Value "Sample text copied to clipboard"
   ```

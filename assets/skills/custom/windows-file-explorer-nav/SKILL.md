---
name: windows-file-explorer-nav
description: Opening File Explorer to paths, managing AppData, Documents, Downloads, shortcuts, and path variables.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows File Explorer Navigation

Opening File Explorer to specific directories and managing special system folders.

## Guidelines

1. **Open File Explorer to Directory**:
   ```powershell
   Start-Process explorer.exe -ArgumentList "C:\Users\corne\Downloads"
   ```
2. **Open Special Folders**:
   - Downloads: `Start-Process explorer.exe -ArgumentList "$env:USERPROFILE\Downloads"`
   - Documents: `Start-Process explorer.exe -ArgumentList "$env:USERPROFILE\Documents"`
   - AppData: `Start-Process explorer.exe -ArgumentList "$env:APPDATA"`
3. **Select Specific File in Explorer**:
   ```powershell
   Start-Process explorer.exe -ArgumentList "/select,C:\temp\test.txt"
   ```

---
name: windows-hotkey-shortcut-automation
description: Hotkey shortcuts reference, SendKeys automation patterns, Win+R commands.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Hotkey & Shortcut Automation

Automating keyboard shortcuts and Win+R run dialogs.

## SendKeys Pattern in PowerShell

```powershell
$wshell = New-Object -ComObject WScript.Shell
$wshell.SendKeys("^{ESC}") # Send Ctrl+Esc to open Start Menu
```

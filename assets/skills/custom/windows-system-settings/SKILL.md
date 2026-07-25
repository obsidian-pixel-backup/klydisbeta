---
name: windows-system-settings
description: Accessing Windows settings pages (ms-settings: URIs), display settings, network settings, sound settings, and system specs.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows System Settings

Directly launching Windows 10/11 system configuration panels using `ms-settings:` URI schemes.

## Common URI Schemes

- **Main Settings**: `Start-Process "ms-settings:"`
- **Display Settings**: `Start-Process "ms-settings:display"`
- **Sound Settings**: `Start-Process "ms-settings:sound"`
- **Network & Internet**: `Start-Process "ms-settings:network"`
- **Bluetooth & Devices**: `Start-Process "ms-settings:bluetooth"`
- **Windows Update**: `Start-Process "ms-settings:windowsupdate"`
- **Apps & Features**: `Start-Process "ms-settings:appsfeatures"`

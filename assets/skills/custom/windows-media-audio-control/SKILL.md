---
name: windows-media-audio-control
description: Managing volume, audio output devices, mute states, and media playback control via PowerShell and system tools.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Media & Audio Control

Inspecting audio endpoints and system sound controls.

## Guidelines

1. **Open Volume Mixer**:
   ```powershell
   Start-Process "sndvol.exe"
   ```
2. **Open Audio Endpoints Control Panel**:
   ```powershell
   Start-Process "mmsys.cpl"
   ```
3. **Mute or Change Volume via PowerShell Component**:
   Use WScript or `nvc` / `sndvol` tools if installed.

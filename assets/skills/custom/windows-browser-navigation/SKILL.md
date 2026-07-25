---
name: windows-browser-navigation
description: Directing Chrome/Edge/Firefox to URLs, YouTube searches, web applications, and new window parameters without shell argument errors.
category: System Navigation & App Management
author: Klydis System
version: 1.0.0
---

# Windows Browser Navigation

Directing web browsers to specific web pages or YouTube search results cleanly on Windows.

## Guidelines

1. **Opening YouTube Search directly**:
   ```powershell
   Start-Process -FilePath "chrome.exe" -ArgumentList "https://www.youtube.com/results?search_query=cat+videos"
   ```
2. **Opening New Browser Window**:
   ```powershell
   Start-Process -FilePath "msedge.exe" -ArgumentList "--new-window", "https://news.google.com"
   ```
3. **Url Encoding**:
   Always encode spaces in search queries with `+` or `%20` (e.g. `cat+videos`).

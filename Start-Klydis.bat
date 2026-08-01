@echo off
title Klydis Beta Launcher
echo Starting Klydis Orchestrator with Verbose Debug Logging...
echo.
set DOTNET_ENVIRONMENT=Development
set Logging__LogLevel__Default=Debug
set Logging__LogLevel__Microsoft=Information
dotnet run --verbosity diagnostic --project "%~dp0src\Klydis.App\Klydis.App.csproj"
pause

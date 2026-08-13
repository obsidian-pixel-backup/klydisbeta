@echo off
title Klydis Beta Launcher
echo Starting Klydis...
echo.
set DOTNET_ENVIRONMENT=Development
set Logging__LogLevel__Default=Debug
set Logging__LogLevel__Microsoft=Information
dotnet run --project "%~dp0src\Klydis.App\Klydis.App.csproj"
pause

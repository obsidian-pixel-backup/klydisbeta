@echo off
title Klydis Beta Launcher
cd /d "%~dp0"

set DOTNET_ENVIRONMENT=Development
set Logging__LogLevel__Default=Debug
set Logging__LogLevel__Microsoft=Information

set EXE_PATH=%~dp0src\Klydis.App\bin\Debug\net10.0-windows\Klydis.exe

if exist "%EXE_PATH%" (
    echo Starting Klydis...
    "%EXE_PATH%"
    if %ERRORLEVEL% NEQ 0 (
        echo.
        echo Application exited with code %ERRORLEVEL%.
        pause
    )
    exit /b 0
)

echo Klydis executable not found. Performing initial build...
dotnet build "%~dp0src\Klydis.App\Klydis.App.csproj" -c Debug --no-restore
if %ERRORLEVEL% NEQ 0 (
    echo Restoring packages and building...
    dotnet build "%~dp0src\Klydis.App\Klydis.App.csproj" -c Debug
)

if exist "%EXE_PATH%" (
    echo Starting Klydis...
    "%EXE_PATH%"
) else (
    dotnet run --no-restore --project "%~dp0src\Klydis.App\Klydis.App.csproj"
)

if %ERRORLEVEL% NEQ 0 pause

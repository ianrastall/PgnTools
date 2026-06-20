@echo off
rem ============================================================
rem  Pick a single tool and generate its curated context dump.
rem  Double-click this, type the number for the tool, press Enter.
rem  Output lands in Context\<Tool>_Context.txt
rem ============================================================
cd /d "%~dp0"

where pwsh >nul 2>nul
if errorlevel 1 (
    echo PowerShell 7 ^(pwsh^) is required but was not found on PATH.
    echo Install it from https://aka.ms/powershell  then run this again.
    echo.
    pause
    exit /b 1
)

pwsh -NoProfile -ExecutionPolicy Bypass -File "Scripts\dump-menu.ps1"

echo.
pause

@echo off
rem ============================================================
rem  Generate the full-codebase context dump for an LLM audit.
rem  Double-click this. Output: Context\Full_Context.txt
rem  Load that into the round-up tool's "Codebase" slot, and
rem  ARCHITECTURE.md into its "Context" slot.
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

echo Generating full-codebase context dump...
echo.
pwsh -NoProfile -ExecutionPolicy Bypass -File "Scripts\code-dump-all.ps1"

echo.
echo Output: %~dp0Context\Full_Context.txt
echo.
pause

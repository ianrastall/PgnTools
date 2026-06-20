@echo off
rem ============================================================
rem  Build PGN Tools as a single-file, self-contained win-x64 EXE.
rem  Just double-click this file. The window stays open at the end.
rem ============================================================
cd /d "%~dp0"

echo ===========================================================
echo   Building PGN Tools  (single-file, self-contained win-x64)
echo ===========================================================
echo.

set "OUTDIR=Build\PgnTools.Wpf.Release"

if exist "%OUTDIR%" (
    echo Cleaning previous build...
    rmdir /s /q "%OUTDIR%"
)

echo Publishing - this can take a minute or two...
echo.

dotnet publish "PgnTools.Wpf\PgnTools.Wpf.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%OUTDIR%"

echo.
if %ERRORLEVEL% EQU 0 (
    echo ===========================================================
    echo   BUILD SUCCEEDED
    echo   App:  %~dp0%OUTDIR%\PgnTools.exe
    echo   Your Desktop "PGN Tools" shortcut already points here.
    echo ===========================================================
) else (
    echo ===========================================================
    echo   BUILD FAILED  ^(error code %ERRORLEVEL%^)
    echo   Scroll up to read the first error message.
    echo ===========================================================
)

echo.
pause

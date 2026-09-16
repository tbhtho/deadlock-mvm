@echo off
rem One-click build + launch for local development.
rem
rem Builds the native DLL and the launcher from the current working tree, then
rem starts the launcher from its build output. Building the launcher project runs
rem native/build-native.ps1 (the BuildNativeReplayCamera target) and copies the
rem fresh DeadlockMVM.Native.dll next to the launcher EXE, so the two always
rem match each other and the source.
rem
rem For a distributable ZIP instead, use package-release.ps1.
setlocal
cd /d "%~dp0"

set "PROJECT=src\DeadlockMVM.Launcher\DeadlockMVM.Launcher.csproj"
set "EXE=src\DeadlockMVM.Launcher\bin\Release\net8.0-windows\DeadlockMVM.Launcher.exe"
set "NATIVE=src\DeadlockMVM.Launcher\bin\Release\net8.0-windows\DeadlockMVM.Native.dll"

rem The build overwrites the launcher EXE and the native DLL in place. A running
rem launcher locks both, which fails the copy with a confusing MSBuild error, so
rem refuse up front and say what to do about it.
tasklist /FI "IMAGENAME eq DeadlockMVM.Launcher.exe" 2>nul | find /I "DeadlockMVM.Launcher.exe" >nul
if not errorlevel 1 (
    echo DeadlockMVM is already running.
    echo Close the launcher window, then run this again.
    echo.
    pause
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET SDK was not found on PATH.
    echo Install the SDK version named in global.json, then run this again.
    echo.
    pause
    exit /b 1
)

echo Building DeadlockMVM ^(native DLL + launcher^)...
echo.
dotnet build "%PROJECT%" -c Release
if errorlevel 1 (
    echo.
    echo Build failed. Nothing was launched.
    echo.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo.
    echo Build reported success but "%EXE%" is missing.
    echo.
    pause
    exit /b 1
)

echo.
for %%F in ("%EXE%") do echo Launcher  : %%~tF
for %%F in ("%NATIVE%") do echo Native DLL: %%~tF
echo.
echo Starting the launcher...
start "" "%EXE%"

@echo off
rem One-click DeadlockMVM launcher. Picks the newest portable build so you
rem don't have to dig through release\portable-* folders.
setlocal
cd /d "%~dp0"
set "TARGET="
for /d %%D in ("release\portable-*") do (
    if exist "%%D\DeadlockMVM\DeadlockMVM.Launcher.exe" set "TARGET=%%D\DeadlockMVM\DeadlockMVM.Launcher.exe"
)
if not defined TARGET (
    echo No portable build found under release\portable-*.
    echo Build one with: powershell -NoProfile -ExecutionPolicy Bypass -File package-release.ps1
    pause
    exit /b 1
)
echo Starting %TARGET%
start "" "%TARGET%"

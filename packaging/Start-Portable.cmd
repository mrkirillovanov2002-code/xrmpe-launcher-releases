@echo off
setlocal DisableDelayedExpansion
if not exist "%~dp0app\Xrmpe.Launcher.exe" (
    echo Xrmpe.Launcher.exe was not found in the app folder.
    echo Please copy the entire Portable folder, including app and its dependencies.
    pause
    exit /b 1
)
start "" /D "%~dp0app" "%~dp0app\Xrmpe.Launcher.exe"
exit /b 0

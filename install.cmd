@echo off
rem Double-click to install the latest release. Add -FromSource to build from this clone instead.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause

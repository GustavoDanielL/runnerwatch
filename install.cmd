@echo off
rem Duplo clique para instalar. Não precisa de administrador.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
pause

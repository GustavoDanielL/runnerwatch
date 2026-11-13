<#
  Remove o GitHub Actions Monitor. Use -Purge para apagar também a config e o token salvo.
#>
param([switch]$Purge)
$ErrorActionPreference = 'Continue'

Get-Process GhActionsMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'GhActionsMonitor' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'Programs\GhActionsMonitor') -ErrorAction SilentlyContinue

if ($Purge) {
    Remove-Item -Recurse -Force (Join-Path $env:APPDATA 'GhActionsMonitor') -ErrorAction SilentlyContinue
    cmdkey /delete:GhActionsMonitor:github.com 2>$null | Out-Null
}
Write-Host 'Desinstalado.' -ForegroundColor Green

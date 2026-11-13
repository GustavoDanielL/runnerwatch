<#
  Removes RunnerWatch. -Purge also deletes the settings, the log and the saved token.
#>
param([switch]$Purge)
$ErrorActionPreference = 'Continue'

Get-Process RunnerWatch, GhActionsMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($name in 'RunnerWatch', 'GhActionsMonitor') {
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $name -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA "Programs\$name") -ErrorAction SilentlyContinue
}
Remove-Item -Force (Join-Path ([Environment]::GetFolderPath('Programs')) 'RunnerWatch.lnk') -ErrorAction SilentlyContinue

if ($Purge) {
    foreach ($name in 'RunnerWatch', 'GhActionsMonitor') {
        Remove-Item -Recurse -Force (Join-Path $env:APPDATA $name) -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA $name) -ErrorAction SilentlyContinue
        cmdkey /delete:"${name}:github.com" 2>$null | Out-Null
    }
}
Write-Host 'RunnerWatch removed.' -ForegroundColor Green

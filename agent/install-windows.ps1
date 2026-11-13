<#
  Installs the RunnerWatch metrics agent on a WINDOWS runner machine (a different machine than the one running RunnerWatch).
  If the runner is on the SAME PC as RunnerWatch you don't need this: CPU/RAM is read locally.
  Run as administrator:  powershell -ExecutionPolicy Bypass -File agent\install-windows.ps1 [-Port 9465] [-Token xyz]
#>
param([int]$Port = 9465, [string]$Token = '')
$ErrorActionPreference = 'Stop'

# Resolve the REAL python.exe ("python" on PATH may just be the Microsoft Store alias).
$py = $null
foreach ($cmd in 'python', 'py') {
    if (Get-Command $cmd -ErrorAction SilentlyContinue) {
        $p = & $cmd -c "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $p -and (Test-Path $p)) { $py = $p.Trim(); break }
    }
}
if (-not $py) { throw 'Python 3 not found. Install it with: winget install Python.Python.3.12 --scope machine' }

$dir = Join-Path $env:ProgramData 'runner-agent'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'runner_agent.py') $dir -Force

$argLine = "`"$dir\runner_agent.py`" --port $Port"
if ($Token) { $argLine += " --token $Token" }

$action   = New-ScheduledTaskAction -Execute $py -Argument $argLine
$trigger  = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
# SYSTEM because a per-user Python install is not readable by LOCAL SERVICE.
# The agent only reads counters and answers HTTP; it never executes anything received from the network.
Register-ScheduledTask -TaskName 'RunnerAgent' -Action $action -Trigger $trigger -Settings $settings `
    -User 'NT AUTHORITY\SYSTEM' -RunLevel Limited -Force | Out-Null
Start-ScheduledTask -TaskName 'RunnerAgent'

New-NetFirewallRule -DisplayName "Runner Agent $Port" -Direction Inbound -Protocol TCP -LocalPort $Port `
    -Profile Private,Domain -Action Allow -ErrorAction SilentlyContinue | Out-Null

Write-Host "OK: http://$($env:COMPUTERNAME):$Port/metrics (firewall opened for Private/Domain networks only)" -ForegroundColor Green

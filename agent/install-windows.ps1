<#
  Instala o agente de métricas numa máquina runner WINDOWS (outra máquina que não a do monitor).
  Se o runner roda no MESMO PC do monitor, não precisa disto: o monitor lê CPU/RAM local sozinho.
  Executar como administrador:  powershell -ExecutionPolicy Bypass -File agent\install-windows.ps1 [-Port 9465] [-Token xyz]
#>
param([int]$Port = 9465, [string]$Token = '')
$ErrorActionPreference = 'Stop'

# Resolve o python.exe REAL (o "python" do PATH pode ser só o atalho da Microsoft Store).
$py = $null
foreach ($cmd in 'python', 'py') {
    if (Get-Command $cmd -ErrorAction SilentlyContinue) {
        $p = & $cmd -c "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $p -and (Test-Path $p)) { $py = $p.Trim(); break }
    }
}
if (-not $py) { throw 'Python 3 não encontrado. Instale com: winget install Python.Python.3.12 --scope machine' }

$dir = Join-Path $env:ProgramData 'runner-agent'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'runner_agent.py') $dir -Force

$argLine = "`"$dir\runner_agent.py`" --port $Port"
if ($Token) { $argLine += " --token $Token" }

$action   = New-ScheduledTaskAction -Execute $py -Argument $argLine
$trigger  = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
# SYSTEM porque um Python instalado por usuário não é legível por LOCAL SERVICE.
# O agente só lê contadores e responde HTTP; não executa nada recebido pela rede.
Register-ScheduledTask -TaskName 'RunnerAgent' -Action $action -Trigger $trigger -Settings $settings `
    -User 'NT AUTHORITY\SYSTEM' -RunLevel Limited -Force | Out-Null
Start-ScheduledTask -TaskName 'RunnerAgent'

New-NetFirewallRule -DisplayName "Runner Agent $Port" -Direction Inbound -Protocol TCP -LocalPort $Port `
    -Profile Private,Domain -Action Allow -ErrorAction SilentlyContinue | Out-Null

Write-Host "OK: http://$($env:COMPUTERNAME):$Port/metrics (firewall liberado só em rede Privada/Domínio)" -ForegroundColor Green

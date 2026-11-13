<#
  Instala o GitHub Actions Monitor para o usuário atual (sem admin).
  - Garante .NET SDK 8+ (via winget, se faltar)
  - Compila e copia para %LOCALAPPDATA%\Programs\GhActionsMonitor
  - Registra para iniciar com o Windows e abre o app
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 [-NoStartup]
#>
param([switch]$NoStartup)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\GhActionsMonitor\GhActionsMonitor.csproj'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GhActionsMonitor'
$exe  = Join-Path $dest 'GhActionsMonitor.exe'

function Test-DotnetSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool]($sdks | Where-Object { [int]($_.Split('.')[0]) -ge 8 })
}

if (-not (Test-DotnetSdk)) {
    Write-Host '.NET SDK 8 não encontrado. Instalando via winget...' -ForegroundColor Yellow
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'winget não disponível. Instale o .NET 8 SDK manualmente: https://dotnet.microsoft.com/download/dotnet/8.0'
    }
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Test-DotnetSdk)) { throw 'O .NET SDK foi instalado, mas não está no PATH. Abra um novo terminal e rode de novo.' }
}

# Fecha a versão em execução para poder sobrescrever o exe.
Get-Process GhActionsMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Compilando para $dest ..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $dest --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação.' }

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoStartup) {
    Remove-ItemProperty -Path $run -Name 'GhActionsMonitor' -ErrorAction SilentlyContinue
} else {
    Set-ItemProperty -Path $run -Name 'GhActionsMonitor' -Value "`"$exe`""
}

Start-Process $exe
Write-Host ''
Write-Host 'Pronto! O monitor está no canto inferior direito (Ctrl+Alt+G mostra/esconde).' -ForegroundColor Green
Write-Host 'Configuração: %APPDATA%\GhActionsMonitor\config.json (ou pelo ícone na bandeja).'

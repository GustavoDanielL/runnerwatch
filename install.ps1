<#
  RunnerWatch installer for Windows (per user, no admin).

    irm https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/install.ps1 | iex

  From a clone:  .\install.ps1 [-Version v0.2.0] [-FromSource]
  -FromSource builds locally with the .NET 10 SDK instead of downloading a release.
#>
param(
    [string]$Version = $(if ($env:RUNNERWATCH_VERSION) { $env:RUNNERWATCH_VERSION } else { 'latest' }),
    [switch]$FromSource
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar

$repo = 'GustavoDanielL/runnerwatch'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\RunnerWatch'
$exe  = Join-Path $dest 'RunnerWatch.exe'
$rid  = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Stop running copies (current and the previous name) so files can be replaced.
Get-Process RunnerWatch, GhActionsMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Previous name of the project: remove its binary and autostart entry (config/token are migrated by the app).
$legacy = Join-Path $env:LOCALAPPDATA 'Programs\GhActionsMonitor'
if (Test-Path $legacy) { Step 'Removing the previous version (GhActionsMonitor)'; Remove-Item -Recurse -Force $legacy }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'GhActionsMonitor' -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $dest | Out-Null

if ($FromSource) {
    $proj = Join-Path $PSScriptRoot 'src\RunnerWatch\RunnerWatch.csproj'
    if (-not $PSScriptRoot -or -not (Test-Path $proj)) { throw '-FromSource must be run from a clone of the repository.' }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK not found: winget install Microsoft.DotNet.SDK.10' }
    Step "Building from source for $rid"
    # Self-contained single file: no C++ toolchain needed (unlike Native AOT, which the release workflow uses).
    dotnet publish $proj -c Release -r $rid --self-contained -p:PublishAot=false -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $dest --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
else {
    $base = if ($Version -eq 'latest') { "https://github.com/$repo/releases/latest/download" } else { "https://github.com/$repo/releases/download/$Version" }
    $zipName = "RunnerWatch-$rid.zip"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('runnerwatch-' + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        Step "Downloading $zipName ($Version)"
        Invoke-WebRequest "$base/$zipName" -OutFile (Join-Path $tmp $zipName) -UseBasicParsing
        Invoke-WebRequest "$base/SHA256SUMS.txt" -OutFile (Join-Path $tmp 'SHA256SUMS.txt') -UseBasicParsing

        $line = Get-Content (Join-Path $tmp 'SHA256SUMS.txt') | Where-Object { $_ -match ([regex]::Escape($zipName) + '$') } | Select-Object -First 1
        $expected = if ($line) { ($line -split '\s+')[0].ToUpperInvariant() } else { $null }
        $actual = (Get-FileHash (Join-Path $tmp $zipName) -Algorithm SHA256).Hash
        if (-not $expected -or $actual -ne $expected) { throw "Checksum mismatch for $zipName" }

        Step "Installing to $dest"
        Get-ChildItem $dest -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
        Expand-Archive (Join-Path $tmp $zipName) -DestinationPath $dest -Force
    }
    finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}

# Start Menu shortcut, so it can be found with the Windows search.
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'RunnerWatch.lnk'
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dest
$sc.Description = 'GitHub Actions monitor'
$sc.Save()

Start-Process $exe
Write-Host ''
Write-Host 'RunnerWatch is running in the bottom-right corner (Ctrl+Alt+G shows/hides it).' -ForegroundColor Green
Write-Host 'Settings and log: right-click the tray icon. Uninstall: scripts\uninstall.ps1 (or the README one-liner).'

# Bouwt PipelineBar.exe op Windows.
# Gebruik: .\build.ps1             (bouwen en starten)
#          .\build.ps1 -Install    (ook installeren in %LOCALAPPDATA%\Programs met snelkoppeling in Start)
#          .\build.ps1 -Arch arm64
param([switch]$Install, [ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'

$App = 'PipelineBar'
$Out = Join-Path $PSScriptRoot "..\build\windows-$Arch"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '.NET SDK niet gevonden. Installeer .NET 10: winget install Microsoft.DotNet.SDK.10'
    exit 1
}

Write-Host "› Publiceren voor win-$Arch…"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot "$App.csproj") -c Release -r "win-$Arch" --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $Out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$Exe = Join-Path $Out "$App.exe"
if ($Install) {
    Get-Process $App -ErrorAction SilentlyContinue | Stop-Process -Force
    $Target = Join-Path $env:LOCALAPPDATA "Programs\$App"
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    Copy-Item $Exe $Target -Force
    $Exe = Join-Path $Target "$App.exe"

    $Lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$App.lnk"
    $Shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($Lnk)
    $Shortcut.TargetPath = $Exe
    $Shortcut.Save()
    Write-Host "› Geïnstalleerd in $Target"
}

Start-Process $Exe
Write-Host "✓ $App draait. Zoek het icoon in het systeemvak (rechtsonder, eventueel onder ^)."

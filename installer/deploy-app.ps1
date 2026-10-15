# Copies the freshly published app/service files over the installed copy in Program Files,
# without going through the Inno Setup installer. Run installer\build.ps1 (or a plain
# `dotnet publish` of NeoSync.App) first so installer\publish has the files to copy.
# Needs to run elevated - Program Files isn't writable otherwise.
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot "publish"),
    [string]$InstallDir = "C:\Program Files\NeoSync"
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PublishDir)) {
    Write-Error "Publish folder not found: $PublishDir -- run 'dotnet publish NeoSync.App\NeoSync.App.csproj -c Release -r win-x64 --self-contained true -o `"$PublishDir`"' first."
    exit 1
}

$proc = Get-Process -Name "NeoSync.App" -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Stopping running NeoSync.App..."
    $proc | Stop-Process -Force
}

Write-Host "--- Copying $PublishDir -> $InstallDir ---"
# Only the app itself changed -- NeoSyncHelper is a running service (would need a service
# stop/start to safely replace) and didn't change, so it's left alone here.
robocopy $PublishDir $InstallDir "NeoSync.App.exe","NeoSync.App.dll","NeoSync.App.pdb","NeoSync.App.deps.json","NeoSync.App.runtimeconfig.json","NeoSync.Core.dll","NeoSync.Core.pdb" /NFL /NDL /NJH
if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed with exit code $LASTEXITCODE"; exit 1 }

Write-Host "Done. Launch NeoSync.App from $InstallDir or the Start Menu shortcut."

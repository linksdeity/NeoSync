# Builds NeoSyncSetup.exe: publishes the .NET app then compiles the Inno Setup script.
# Run from any directory; output is installer\Output\NeoSyncSetup.exe.
param(
    [string]$Version = "0.9.0"
)
$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot     = Split-Path $installerDir -Parent
$publishDir   = Join-Path $installerDir "publish"
$iscc         = "C:\Program Files\Inno Setup 7\ISCC.exe"

if (-not (Test-Path $iscc)) {
    Write-Error "ISCC.exe not found at '$iscc'. Install Inno Setup 7 from https://jrsoftware.org/isdl.php"
    exit 1
}

# Verify pre-signed driver package exists (run installer\driver\create-driver-package.ps1 once to generate)
$driverDir      = Join-Path $installerDir "driver"
$requiredDriverFiles = @("neosync.cer","neosync_bd01.inf","neosync_bd01.cat","neosync_bd04.inf","neosync_bd04.cat")
foreach ($f in $requiredDriverFiles) {
    if (-not (Test-Path (Join-Path $driverDir $f))) {
        Write-Error "Missing driver file: driver\$f -- run installer\driver\create-driver-package.ps1 first"
        exit 1
    }
}
Write-Host "--- Driver package files verified ---"

Write-Host "--- Publishing app (self-contained, win-x64) ---"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish "$repoRoot\NeoSync.App\NeoSync.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -o $publishDir `
    /p:Version=$Version
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed"; exit 1 }

Write-Host "--- Publishing service (self-contained, win-x64) ---"
dotnet publish "$repoRoot\NeoSync.Service\NeoSync.Service.csproj" `
    -c Release -r win-x64 --self-contained true `
    -o $publishDir `
    /p:Version=$Version
if ($LASTEXITCODE -ne 0) { Write-Error "Service publish failed"; exit 1 }

Write-Host "--- Compiling installer ---"
& $iscc /DAppVersion=$Version (Join-Path $installerDir "NeoSync.iss")
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC failed"; exit 1 }

$output = Join-Path $installerDir "Output\NeoSyncSetup.exe"
Write-Host "Done: $output"

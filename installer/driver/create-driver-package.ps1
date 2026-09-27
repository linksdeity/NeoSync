# Creates the pre-signed WinUSB driver package: INF files, signed CAT files, and the signing cert.
# Run ONCE to bootstrap; commit the outputs (*.inf, *.cat, neosync.cer) but NOT neosync.pfx.
param([string]$PfxPassword = "NeoSyncDriverSign")
$ErrorActionPreference = 'Stop'

$sdk      = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64"
$signtool = Join-Path $sdk "signtool.exe"
$makecat  = Join-Path $sdk "makecat.exe"
foreach ($t in @($signtool, $makecat)) {
    if (-not (Test-Path $t)) { Write-Error "Not found: $t -- install the Windows 10 SDK"; exit 1 }
}

$outDir  = $PSScriptRoot
$pfxPath = Join-Path $outDir "neosync.pfx"
$cerPath = Join-Path $outDir "neosync.cer"

# Fixed DeviceInterfaceGUIDs. Never change after first install.
$guidBd01 = "{3D10B9D7-F13A-4DC1-A0B5-8A11C2A58BD1}"
$guidBd04 = "{A83F8E5B-2C4A-4BE9-9B06-7E3F1C4ABD04}"

$driverDate    = (Get-Date -Format "MM/dd/yyyy")
$driverVersion = "1.0.0.0"

# 1. Generate self-signed code-signing certificate
if (-not (Test-Path $pfxPath)) {
    Write-Host "Generating self-signed code-signing certificate..."
    $cert = New-SelfSignedCertificate `
        -Subject "CN=NeoSync Driver Signing, O=NeoSync" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyUsage DigitalSignature `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") `
        -NotAfter (Get-Date).AddYears(15)
    $pwd = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd | Out-Null
    Export-Certificate   -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
    Write-Host "Created: $cerPath (commit this)"
    Write-Host "Created: $pfxPath (do NOT commit)"
} else {
    Write-Host "Reusing existing certificate."
    if (-not (Test-Path $cerPath)) {
        $pfxCert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $PfxPassword)
        [IO.File]::WriteAllBytes($cerPath, $pfxCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
}

# 2. Generate WinUSB INF files
function Write-WinUsbInf($infName, $hwId, $deviceName, $catName, $ifaceGuid, $provider = "AlphaSmart") {
    $path = Join-Path $outDir $infName
    $enc  = [System.Text.UTF8Encoding]::new($false)
    $nl   = "`r`n"
    $content  = "; WinUSB driver package for AlphaSmart Neo ($deviceName)$nl"
    $content += "; DeviceInterfaceGUID: $ifaceGuid$nl"
    $content += "$nl"
    $content += "[Version]$nl"
    $content += 'Signature   = "$Windows NT$"' + $nl
    $content += "Class       = USBDevice$nl"
    $content += "ClassGuid   = {88BAE032-5A81-49f0-BC3D-A4FF138216D6}$nl"
    $content += "Provider    = %ManufacturerName%$nl"
    $content += "CatalogFile = $catName$nl"
    $content += "DriverVer   = $driverDate,$driverVersion$nl"
    $content += "PnpLockdown = 1$nl"
    $content += "$nl"
    $content += "[Manufacturer]$nl"
    $content += "%ManufacturerName% = Standard,NTamd64$nl"
    $content += "$nl"
    $content += "[Standard.NTamd64]$nl"
    $content += "%DeviceName% = USB_Install,$hwId$nl"
    $content += "$nl"
    $content += "[USB_Install.NT]$nl"
    $content += "Include = winusb.inf$nl"
    $content += "Needs   = WINUSB.NT$nl"
    $content += "$nl"
    $content += "[USB_Install.NT.Services]$nl"
    $content += "Include    = winusb.inf$nl"
    $content += "AddService = WinUSB,0x00000002,WinUSB_ServiceInstall$nl"
    $content += "$nl"
    $content += "[WinUSB_ServiceInstall]$nl"
    $content += "DisplayName   = %WinUSB_SvcDesc%$nl"
    $content += "ServiceType   = 1$nl"
    $content += "StartType     = 3$nl"
    $content += "ErrorControl  = 1$nl"
    $content += "ServiceBinary = %12%\WinUSB.sys$nl"
    $content += "$nl"
    $content += "[USB_Install.NT.Wdf]$nl"
    $content += "KmdfService = WinUSB,WinUSB_wdfsect$nl"
    $content += "$nl"
    $content += "[WinUSB_wdfsect]$nl"
    $content += "KmdfLibraryVersion = 1.9$nl"
    $content += "$nl"
    $content += "[USB_Install.NT.HW]$nl"
    $content += "AddReg = Dev_AddReg$nl"
    $content += "$nl"
    $content += "[Dev_AddReg]$nl"
    $content += 'HKR,,DeviceInterfaceGUIDs,0x00010000,"' + $ifaceGuid + '"' + $nl
    $content += "$nl"
    $content += "[Strings]$nl"
    $content += 'ManufacturerName = "' + $provider + '"' + $nl
    $content += 'DeviceName       = "' + $deviceName + '"' + $nl
    $content += 'WinUSB_SvcDesc   = "WinUSB"' + $nl
    [IO.File]::WriteAllText($path, $content, $enc)
    Write-Host "Wrote: $infName"
}

Write-WinUsbInf "neosync_bd01.inf"      "USB\VID_081E&PID_BD01"      "AlphaSmart Neo (Comms)"         "neosync_bd01.cat"      $guidBd01 "AlphaSmart"
Write-WinUsbInf "neosync_bd04.inf"      "USB\VID_081E&PID_BD04"      "AlphaSmart Neo (Mode Switch)"   "neosync_bd04.cat"      $guidBd04 "NeoSync"

# 3. Create and sign catalog files
function Sign-DriverPackage($infName, $catName) {
    $infFull = Join-Path $outDir $infName
    $catFull = Join-Path $outDir $catName
    $cdfFull = Join-Path $outDir "$catName.cdf"
    $enc     = [System.Text.UTF8Encoding]::new($false)
    $nl      = "`r`n"

    $cdf  = "[CatalogHeader]$nl"
    $cdf += "Name=$catFull$nl"
    $cdf += "PublicVersion=0x0000001$nl"
    $cdf += "EncodingType=0x00010001$nl"
    $cdf += "CATATTR1=0x10010001:attr1:Driver$nl"
    $cdf += "$nl"
    $cdf += "[CatalogFiles]$nl"
    $cdf += "<HASH>$infName=$infFull$nl"
    [IO.File]::WriteAllText($cdfFull, $cdf, $enc)

    Write-Host "Creating catalog: $catName"
    & $makecat -v $cdfFull
    if ($LASTEXITCODE -ne 0) { throw "makecat failed for $catName" }
    Remove-Item $cdfFull -Force

    Write-Host "Signing: $catName"
    & $signtool sign /fd SHA256 /f $pfxPath /p $PfxPassword /t "http://timestamp.digicert.com" $catFull 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Timestamp server unavailable; signing without timestamp."
        & $signtool sign /fd SHA256 /f $pfxPath /p $PfxPassword $catFull
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $catName" }
    }
    Write-Host "Signed: $catName"
}

Sign-DriverPackage "neosync_bd01.inf" "neosync_bd01.cat"
Sign-DriverPackage "neosync_bd04.inf" "neosync_bd04.cat"

Write-Host ""
Write-Host "Driver package ready. Commit to the repo:"
Write-Host "  installer/driver/neosync.cer"
Write-Host "  installer/driver/neosync_bd01.inf + .cat"
Write-Host "  installer/driver/neosync_bd04.inf + .cat"
Write-Host "Do NOT commit neosync.pfx (private signing key)."
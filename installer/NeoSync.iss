[Setup]
AppName=NeoSync
; AppVersion is passed from build.ps1 via /DAppVersion=x.y.z - defaults to 0.9.4 for IDE use.
#ifndef AppVersion
  #define AppVersion "0.9.4"
#endif
AppVersion={#AppVersion}
AppPublisher=NeoSync
AppPublisherURL=https://github.com/linksdeity/NeoSync
DefaultDirName={autopf}\NeoSync
DefaultGroupName=NeoSync
OutputDir=Output
OutputBaseFilename=NeoSyncSetup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
; 64-bit Windows only - the self-contained .NET publish is x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; User must accept the disclaimer (unofficial software, use at your own risk,
; driver installation risk) before Setup proceeds.
LicenseFile=LICENSE.txt
SetupIconFile=..\NeoSync.App\neosync.ico

[Files]
; All app files from dotnet publish output
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\THIRD_PARTY_LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion
; Pre-signed WinUSB driver package (INF + signed CAT + public cert)
Source: "driver\neosync.cer";            DestDir: "{app}\driver"; Flags: ignoreversion
Source: "driver\neosync_bd01.inf";       DestDir: "{app}\driver"; Flags: ignoreversion
Source: "driver\neosync_bd01.cat";       DestDir: "{app}\driver"; Flags: ignoreversion
Source: "driver\neosync_bd04.inf";      DestDir: "{app}\driver"; Flags: ignoreversion
Source: "driver\neosync_bd04.cat";      DestDir: "{app}\driver"; Flags: ignoreversion

[Icons]
Name: "{group}\NeoSync"; Filename: "{app}\NeoSync.App.exe"
Name: "{commondesktop}\NeoSync"; Filename: "{app}\NeoSync.App.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"
Name: diagnostics; Description: "Allow NeoSync to keep local diagnostic logs (recommended -- helps troubleshoot problems; never includes the contents of your files, and is never sent anywhere automatically)"; GroupDescription: "Privacy:"; Flags: checkedonce

[Run]
Filename: "{app}\NeoSync.App.exe"; Description: "Launch NeoSync"; Flags: nowait postinstall skipifsilent

[Code]
// Service must be created and running BEFORE any driver setup, since setup now routes
// through NeoSyncHelper (the same service used at runtime) instead of duplicating driver
// logic directly in this script - that duplicate path lacked the reliability fixes
// (auto-restart on transient driver failures, no harmful install/uninstall churn) that
// the service itself has, so a real client's first-run setup could get stuck with no
// self-healing (recovering from that during development required manual pnputil commands).
procedure StartHelperService;
var
  ResultCode: Integer;
begin
  // Stop and delete any prior installation before re-creating (handles upgrades).
  Exec(ExpandConstant('{sys}') + '\sc.exe', 'stop NeoSyncHelper', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}') + '\sc.exe', 'delete NeoSyncHelper', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}') + '\sc.exe',
    'create NeoSyncHelper binPath= "' + ExpandConstant('{app}') + '\NeoSyncHelper.exe" start= auto type= own displayname= "NeoSync Helper"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}') + '\sc.exe', 'start NeoSyncHelper', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure InstallDrivers;
var
  AppDir: String;
  ResultCode: Integer;
begin
  AppDir := ExpandConstant('{app}');

  // Delegates entirely to the app's own driver setup dialog (Advanced > Reinstall WinUSB
  // drivers) -- staging, the live replug status, and the mode switch are all handled there,
  // so the installer doesn't need its own separate copy of that flow.
  Exec(AppDir + '\NeoSync.App.exe', '--driver-setup', AppDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  DiagFlag: String;
begin
  if CurStep = ssPostInstall then
  begin
    StartHelperService;
    if WizardIsTaskSelected('diagnostics') then DiagFlag := '1' else DiagFlag := '0';
    Exec(ExpandConstant('{app}') + '\NeoSync.App.exe',
      '--set-diagnostics ' + DiagFlag,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
    InstallDrivers;
  end;
end;

procedure RemoveNeoSyncDriverInfs;
var
  InfDir, InfPath: String;
  InfContent: AnsiString;
  FindRec: TFindRec;
  ResultCode: Integer;
begin
  InfDir := ExpandConstant('{win}') + '\INF\';
  if FindFirst(InfDir + 'oem*.inf', FindRec) then
  begin
    try
      repeat
        InfPath := InfDir + FindRec.Name;
        if LoadStringFromFile(InfPath, InfContent) then
          if Pos('VID_081E', String(InfContent)) > 0 then
            // /force is required here -- without it, pnputil silently fails to remove
            // a driver package that's still actively bound to a connected device (the
            // common case: uninstalling with the Neo plugged in), leaving BD04 stuck on
            // WinUSB with no real keyboard driver ever getting a chance to bind.
            Exec(ExpandConstant('{sys}') + '\pnputil.exe',
              '/delete-driver ' + FindRec.Name + ' /uninstall /force',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure RemoveNeoSyncCert;
var
  ResultCode: Integer;
begin
  // Remove the driver signing cert from both stores
  Exec(ExpandConstant('{sys}') + '\certutil.exe',
    '-delstore TrustedPublisher NeoSync', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}') + '\certutil.exe',
    '-delstore Root NeoSync', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Deleting the driver package from the store doesn't necessarily clear a live,
// still-connected device's own binding to it -- a plain device restart has been
// observed to not always be enough to get Windows to redo driver selection and
// actually rebind the inbox HID keyboard driver (BD04 can come back still not
// working as a keyboard). Fully remove the device node instead, forcing a real
// re-enumeration from scratch. The service is already stopped/deleted by this
// point in uninstall, so this calls pnputil directly (the uninstaller itself
// already runs elevated).
procedure ResetNeoDevices;
var
  DevIds, InstIds: TArrayOfString;
  i, j: Integer;
  ResultCode: Integer;
begin
  SetArrayLength(DevIds, 2);
  DevIds[0] := 'VID_081E&PID_BD04';
  DevIds[1] := 'VID_081E&PID_BD01';
  for i := 0 to GetArrayLength(DevIds) - 1 do
  begin
    if RegGetSubkeyNames(HKLM, 'SYSTEM\CurrentControlSet\Enum\USB\' + DevIds[i], InstIds) then
      for j := 0 to GetArrayLength(InstIds) - 1 do
        Exec(ExpandConstant('{sys}') + '\pnputil.exe',
          '/remove-device "USB\' + DevIds[i] + '\' + InstIds[j] + '" /subtree',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  // A removed device isn't always re-discovered automatically - explicitly ask Windows
  // to rescan rather than leave that to chance (same as Device Manager's "Scan for
  // hardware changes").
  Exec(ExpandConstant('{sys}') + '\pnputil.exe', '/scan-devices',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}') + '\sc.exe', 'stop NeoSyncHelper',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}') + '\sc.exe', 'delete NeoSyncHelper',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RemoveNeoSyncDriverInfs;
    RemoveNeoSyncCert;
    ResetNeoDevices;
  end;
end;

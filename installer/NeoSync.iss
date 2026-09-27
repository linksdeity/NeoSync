[Setup]
AppName=NeoSync
; AppVersion is passed from build.ps1 via /DAppVersion=x.y — defaults to 1.0 for IDE use.
#ifndef AppVersion
  #define AppVersion "1.0"
#endif
AppVersion={#AppVersion}
AppPublisher=NeoSync
AppPublisherURL=https://github.com/neosync
DefaultDirName={autopf}\NeoSync
DefaultGroupName=NeoSync
OutputDir=Output
OutputBaseFilename=NeoSyncSetup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
; 64-bit Windows only — libwdi.dll and the .NET publish are x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; User must accept the disclaimer (unofficial software, use at your own risk,
; driver installation risk) before Setup proceeds.
LicenseFile=LICENSE.txt

[Files]
; All app files from dotnet publish output (includes libwdi.dll at root)
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

[Run]
Filename: "{app}\NeoSync.App.exe"; Description: "Launch NeoSync"; Flags: nowait postinstall skipifsilent

[Code]
// Service must be created and running BEFORE any driver setup, since setup now routes
// through NeoSyncHelper (the same service used at runtime) instead of duplicating driver
// logic directly in this script — that duplicate path lacked the reliability fixes
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

  // Step 1: cert trust + BD01/BD04 driver staging, via the same SETUP_DRIVERS command
  // used by "Reinstall WinUSB drivers..." in the app itself.
  Exec(AppDir + '\NeoSync.App.exe', '--setup-drivers "' + AppDir + '\driver"',
    AppDir, SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Step 2: Ask user to connect the Neo (in its default keyboard mode, BD04)
  if MsgBox('USB Driver Setup' + #13#10#13#10 +
      'If the Neo is already plugged in, unplug it now.' + #13#10 +
      'Then plug it in and wait for the USB connect sound (or the keyboard light).' + #13#10 +
      'Click OK once Windows has recognised it as a keyboard -- NeoSync will switch it to transfer mode.' + #13#10#13#10 +
      'Click Cancel to skip (the driver is staged and will apply the next time you use NeoSync).',
      mbConfirmation, MB_OKCANCEL) = IDCANCEL then Exit;

  // Step 3: NeoSync sends the HID mode-switch so BD01 enumerates; Windows auto-applies staged driver
  Exec(AppDir + '\NeoSync.App.exe', '--switch-to-comms',
    AppDir, SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if ResultCode = 0 then
    MsgBox('Driver setup complete. Your Neo is ready to use with NeoSync.', mbInformation, MB_OK)
  else
    MsgBox('The mode switch timed out -- the device may still be enumerating.' + #13#10 +
      'The driver is staged and will be applied automatically when NeoSync first connects.',
      mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    StartHelperService;
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
            Exec(ExpandConstant('{sys}') + '\pnputil.exe',
              '/delete-driver ' + FindRec.Name + ' /uninstall',
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
  end;
end;

# NeoSync – Session Pickup Notes
_Last updated: 2026-08-03 23:00_

## Current situation
Installer works. Drivers show green (BD04=WindowsHid, BD01=WinUsb).
Clicking Connect gets stuck at **"Waiting for device to re-enumerate…"** and times out.

## Root cause of the timeout

`pnputil /add-driver neosync_bd04.inf /install` stages the driver and reports
"Driver package installed on device: USB\VID_081E&PID_BD04\..." but Windows
**does not actually switch BD04 from HidUsb to WinUSB**. It keeps HidUsb active.
`WaitForWinUsb` polls for 30 s seeing `WindowsHid` the whole time, then gives up.

Why pnputil fails here: `pnputil /install` goes through the normal PnP rank
mechanism. For a connected HID keyboard, Windows won't swap the inbox driver
(HidUsb) for a third-party one via the normal rank path - even when the new INF
has a more specific hardware-ID match.

Zadig (libwdi) avoids this by calling `UpdateDriverForPlugAndPlayDevicesW`
(newdev.dll) with `INSTALLFLAG_FORCE`, which explicitly bypasses ranking and
forces the driver swap on the running device.

## Fix needed (next session)

Replace `RunPnputil("/add-driver ... /install")` in `Bd04WinUsbCycle.Install()`
with a P/Invoke call to `UpdateDriverForPlugAndPlayDevicesW`:

```csharp
// newdev.dll
[DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool UpdateDriverForPlugAndPlayDevicesW(
    nint hwndParent,
    string hardwareId,    // "USB\\VID_081E&PID_BD04"
    string fullInfPath,   // absolute path to neosync_bd04.inf
    uint   installFlags,
    out bool rebootRequired);

const uint INSTALLFLAG_FORCE          = 0x00000001;
const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;
```

Call:
```csharp
bool ok = UpdateDriverForPlugAndPlayDevicesW(
    nint.Zero,
    "USB\\VID_081E&PID_BD04",
    bd04InfPath,
    INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE,
    out bool reboot);
```

This forces the driver swap even on a running HID keyboard, which is what
Zadig does.  The oem name for later removal can still be obtained by scanning
pnputil /enum-drivers for "neosync_bd04.inf" as the Original Name (RemoveAny).

For the Remove side: `pnputil /delete-driver oemXXX.inf /uninstall` should
still work after the force-install.  Alternatively DiUninstallDevice can be
used to do a cleaner revert, but pnputil is simpler and likely sufficient.

## Bugs already fixed (do NOT revert)

| # | File | What was fixed |
|---|------|----------------|
| 1 | `create-driver-package.ps1` | `neosync_bd04.inf` now uses `Provider=NeoSync` so it gets a different oem number from `neosync_bd01.inf`. Before this, staging bd04 replaced bd01 (same oem212.inf), then Remove deleted bd01's driver too. |
| 2 | `NeoHidSwitcher.cs` | wIndex order swapped: 0 first, then 1. BD04 is single-interface - wIndex=1 SET_REPORT is ACK'd but ignored by firmware. |
| 3 | `Bd04WinUsbCycle.RemoveAny()` | Now matches `neosync_bd04.inf` exactly (not `neosync_bd04_mi01.inf`). |
| 4 | `MainForm.cs ConnectAsync` | Changed `BothReady` → `HidReady` gate. BD01 being NotSeen is expected before first switch. |
| 5 | `MainForm.cs ConnectAsync` | Handles `Hid == WinUsb` (leftover from failed run): captures HidInfPath for cleanup. |
| 6 | `MainForm.cs ConnectAsync` | 2 s stabilisation delay after WaitForWinUsb before sending transfers. |
| 7 | `Program.cs --switch-to-comms` | Same WinUsb-already / delay / RemoveAny fixes as ConnectAsync. |

## Key file locations

| Purpose | File |
|---------|------|
| BD04 WinUSB install/remove helper | `NeoSync.Core/Usb/Bd04WinUsbCycle.cs` |
| Mode switch logic (ControlTransfers) | `NeoSync.Core/Usb/NeoHidSwitcher.cs` |
| ConnectAsync (BD04 cycle + switch) | `NeoSync.App/MainForm.cs` ~L191 |
| --switch-to-comms headless mode | `NeoSync.App/Program.cs` ~L13 |
| Driver INF generator | `installer/driver/create-driver-package.ps1` |
| Installer script | `installer/NeoSync.iss` |
| Build script | `installer/build.ps1` |
| Debug log | `%TEMP%\neosync-debug.log` |

## Current driver state on test machine (2026-08-03 end of session)

- BD04: `HidUsb` (keyboard works fine - RemoveAny cleaned up bd04 WinUSB)
- BD01: `WinUSB` / `oem212.inf` staged ✓ (re-staged manually after earlier deletion)
- `neosync_bd04_mi01.inf` still in store (oem213.inf was deleted - need to check oem number)

Run `pnputil /enum-drivers | findstr /i neosync` to see current state.

## Build commands

```powershell
# Regenerate signed INFs (only needed if INF content changes)
& "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -ExecutionPolicy Bypass `
  -File "C:\repos\neosync\installer\driver\create-driver-package.ps1"

# Build installer
& "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -ExecutionPolicy Bypass `
  -File "C:\repos\neosync\installer\build.ps1" -Version "1.0"

# Output
C:\repos\neosync\installer\Output\NeoSyncSetup.exe
```

## Debugging tips

The debug log at `%TEMP%\neosync-debug.log` shows every registry poll and
pnputil call verbatim. Key things to look for:

- After `Bd04WinUsbCycle.Install:` - check that `staged as:` is **not** oem212.inf
  (that would mean the package-collision bug returned).
- During `WaitForWinUsb: polling` - check what `BD04=` shows each iteration.
  If it stays `WindowsHid` forever → pnputil installed but HidUsb is not being
  replaced (the UpdateDriverForPlugAndPlayDevicesW fix is needed).
  If it shows `DriverError` → WinUSB is loading but failing (signing/security issue).
- `TrySwitchToCommsMode: WinUSB devicePath=` - check that a real path appears,
  not `(null)`. If null, WinUSB interface was not registered.

## Mode switch details

The Neo BD04 mode switch is 5 `SET_REPORT` control transfers (bytes 0xe0–0xe4)
sent via WinUSB ControlTransfer to endpoint 0.

```
RequestType = 0x21  (Class | Interface | Host→Device)
bRequest    = 0x09  (SET_REPORT)
wValue      = 0x0200 (Output report type=2, report ID=0)
wIndex      = 0 first, then 1 as fallback (BD04 is single-interface)
wLength     = 1
Data        = [0xE0] … [0xE4]
```

Before each attempt, `SendHidInit` sends SET_PROTOCOL(1) and SET_IDLE(0).
The Mac uses IOUSBDeviceInterface DeviceRequest - same wire format.

BD01 appears after successful switch. `TryOpen` polls `\\?\USB#VID_081E&PID_BD01#...`
with CreateFile; Win32=2 = device not yet present (normal while switching).

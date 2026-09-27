# NeoSync — Development Notes

Windows port of [AlphaSync](https://github.com/tSoniq/alphasync) (macOS) for the AlphaSmart Neo.
Original Mac driver code: C:\repos\alphasync (reference only, not built here).

---

## What works right now

| Feature | Status |
|---|---|
| USB connection (BD01 comms mode) | ✅ Working |
| Applet enumeration | ✅ Working |
| AlphaWord file list | ✅ Working (uses GET_USED_SPACE, not header FileCount) |
| File download (text) | ✅ Working with correct AlphaWord encoding |
| File upload (text) | ✅ Working |
| Disconnect + Neo reboot to keyboard mode | ✅ Working |
| System applet filter (default = AlphaWord only) | ✅ Working |
| Mode switch BD04 → BD01 (auto) | ✅ Working via WinUSB (HID MI_01 not present on Neo) |
| WinUSB driver install (libwdi) | ✅ Working — in-app DriverSetupForm, no Zadig needed |
| WinUSB driver remove | ✅ Working — scans all accumulated OEM INFs and deletes them |
| Remove/reinstall cycle | ✅ Working — tested end-to-end multiple times |
| BD01 WinUSB transient load failure | ⚠️ Detected at 5s with actionable guidance; may need 2–3 Connect attempts |

---

## One-time setup required (Zadig)

> ⚠️ **HIGH PRIORITY for distribution** — see "Driver distribution problem" section below.

The user must run [Zadig](https://zadig.akeo.ie) twice per machine:

1. With Neo plugged in, open Zadig → Options → List All Devices
2. Select **AlphaSmart** (USB ID `081E / BD04`) → WinUSB → **Replace Driver**
3. Unplug Neo, replug it; it should come back in comms mode
4. Select **AlphaSmart Communication Driver** (USB ID `081E / BD01`) → WinUSB → **Replace Driver**

Both drivers persist across reboots. The app then handles mode switching on each session.

**Why Zadig twice:**
- BD04 (HID keyboard mode): WinUSB needed so we can send the mode-switch control transfers
  (Windows blocks write access to HID keyboard interfaces from user-space on all versions ≥ Vista)
- BD01 (comms mode): WinUSB needed for bulk USB transfers

**For redistribution:** embed [libwdi](https://github.com/pbatard/libwdi) (the library Zadig uses) 
to automate driver installation in a first-run setup step.

---

## USB / Protocol details

### Device VID/PIDs
- `0x081e / 0xbd04` — HID keyboard mode (default on power-up)
- `0x081e / 0xbd01` — Comms mode (bulk USB, used for file transfer)

### Mode switch sequence
Send 5 USB control transfers to BD04 via WinUSB:
```
bmRequestType = 0x20  (Class | Device | Host→Device)  ⚠️ NOT 0x21 (Interface) — see below
bRequest      = 0x09  (HID SET_REPORT)
wValue        = 0x0200 (Output report type=2, ID=0)
wIndex        = 0x0001 (arbitrary firmware-level tag — BD04 has only ONE real interface)
wLength       = 0x0001
data          = 0xe0, 0xe1, 0xe2, 0xe3, 0xe4  (one byte per transfer)
```
Preceded by a standard `SET_CONFIGURATION` (wValue=1) control transfer.
Confirmed against `neotools` (Linux, `C:\repos\neotools\neotools\device.py`
`flip_to_comms_mode()`), which is authoritative here — the tSoniq/Mac docs we
originally worked from implied `bmRequestType=0x21` (Class|**Interface**), which
is wrong and was the root cause of a multi-day debugging detour (including a
dead-end composite/MI_01 USB split attempt — BD04 does not have a second
interface, so there's nothing to split).
Source: `NeoHidSwitcher.TrySwitchToCommsMode()` in `NeoSync.Core/Usb/`.
**Working as of 2026-09-27:** send once, wait up to 10s for BD01 to enumerate.
Do not resend if the transfer succeeded — the Neo may already be mid-reset, and
resending to a disappearing device returns Win32 error 22 (`ERROR_BAD_COMMAND`)
and can disrupt the switch in progress.

### Device discovery (BD01)
`SetupDiGetDeviceInterfaceDetail` returns an empty SymbolicLink for BD01 even when the
device is present (WinUSB doesn't write it until first open). Instead we build the path
directly from the `DeviceClasses` registry key name:

```
HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{7060C8A1-...}\
  ##?#USB#VID_081E&PID_BD01#5&938d0f6&0&13#{7060c8a1-...}
```
Strip `##?#` → `\\?\` to get the CreateFile path.

The WinUSB interface GUID (`7060C8A1-...`) is written by Zadig to:
`HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_081E&PID_BD01\{instance}\Device Parameters\DeviceInterfaceGUIDs`

### Bulk pipe enumeration
`WINUSB_PIPE_INFORMATION.PipeType` is a 4-byte `uint` (`USBD_PIPE_TYPE` enum), NOT a byte.
`UsbdPipeTypeBulk = 2` (not 3 — Interrupt is 3). Both bugs were in the original code and 
caused no bulk pipes to be found. Fixed in `NativeMethods.cs`.

### ASM Protocol
8-byte messages: `[cmd(1)] [payload(6)] [checksum(1)]`
Checksum = sum of bytes 0–6 mod 256. All multi-byte payload values are big-endian.

Key sequences (all from `alphasync/AlphaSync/Driver/ASDevice.cc`):
- **Hello**: write `0x01` (1 byte), read 2 bytes back (version, must be ≥ 0x0230)
- **Reset**: write `{0x3f, 0xff, 0x00, 0x72, 0x65, 0x73, 0x65, 0x74}` (8 bytes)
- **SwitchApplet**: write `{0x3f, 0x53, 0x77, 0x74, 0x63, 0x68, appletHi, appletLo}`,
  expect back `{0x53, 0x77, 0x69, 0x74, 0x63, 0x68, 0x65, 0x64}` ("Switched")
- **dialogueStart** = Hello + Reset + SwitchApplet(0x0000)
- **dialogueEnd** = Reset()

File reads use a BLOCK_READ loop:
  Send REQUEST_BLOCK_READ → receive RESPONSE_BLOCK_READ (with blocksize+checksum) → read blocksize bytes → repeat

File writes use a BLOCK_WRITE loop:
  Send REQUEST_BLOCK_WRITE (with blocksize+checksum) → receive RESPONSE_BLOCK_WRITE → write blocksize bytes → receive RESPONSE_BLOCK_WRITE_DONE → repeat

---

## AlphaWord file format

Source: `alphasync/AlphaSync/Driver/ASAlphaWordFile.cc` — `exportText()` / `importText()`

### Decoding (download)
| Byte | Meaning |
|---|---|
| `0x09` | Tab |
| `0x0d` | End of paragraph → CRLF |
| `0x81`, `0xa1` | **Line-breaking space** → regular space (this is the main "missing spaces" fix) |
| `0x8d`, `0xa3` | Line-breaking tab → tab |
| `0x8f`, `0xa4`, `0xa7` | Discard (break hints / pad bytes) |
| `0xa8`, `0xa9` | Discard (unknown/display control) |
| `0xad` | Line-breaking hyphen → `-` |
| `0xb0` | Escape sequence: `{0xb0, code, 0xb0}` → treat `code` as a raw character |
| `0xa1–0xbf` (other) | Discard |
| `0x80–0xff` (other) | Decode as CP1252 (Windows-1252) |

### Encoding (upload)
- Regular ASCII (0x20–0x7E): stored as-is
- Tab → `0x09`
- Newline/CR → `0x0d`
- CP1252 chars in `0xa1–0xbf`: wrap in escape `{0xb0, byte, 0xb0}`
- Other CP1252 (0x80–0xa0, 0xc0–0xff): stored as-is
- File padded to minimum 512 bytes with `0xa7` fill bytes
- **The Neo regenerates line-breaking hints on first open** — we don't need to insert them

### AlphaWord file count
The applet header's `FileCount` field (offset 0x17) is **0** for AlphaWord even when files exist.
Use `REQUEST_GET_USED_SPACE` (0x1b) with `argument(1,4)=1` to get the real file count:
response `argument(5,2)` = file count.

---

## Applet header format
Size: `0x84` bytes. Source: `alphasync/AlphaSync/Driver/ASApplet.h`

| Offset | Field | Notes |
|---|---|---|
| 0x00 | Signature (uint32) | Must be `0xc0ffeead` |
| 0x10 | Flags (uint32) | Bit 0 = hidden (system applet) |
| 0x14 | AppletId (uint16) | `0xa000` = AlphaWord |
| 0x17 | FileCount (uint8) | Unreliable for AlphaWord, use GET_USED_SPACE |
| 0x18 | Name (36 bytes) | Null-terminated ASCII |

Known AppletIds: `0xa000` = AlphaWord, `0xa005` = Dictionary, `0x0000` = System.
AlphaWord flag value: `0xff0000ce` (hidden bit NOT set → shown in default view).
System/font/spell applets have hidden bit set (`flags & 0x01 == 1`).

---

## Code structure

```
NeoSync.sln
├── NeoSync.Core/              (.NET 8 class library, zero NuGet deps)
│   ├── Protocol/
│   │   ├── NeoMessageCodes.cs   All ASMESSAGE_* constants (from ASMessage.h)
│   │   ├── NeoMessage.cs        8-byte protocol frame with checksum
│   │   └── NeoFileAttributes.cs 40-byte binary attribute struct
│   ├── Device/
│   │   ├── NeoApplet.cs         Applet model (Name, FileCount, IsHidden, IsAlphaWord)
│   │   └── NeoDevice.cs         Full protocol engine — all USB dialogue logic
│   └── Usb/
│       ├── INeoTransport.cs     Read/write interface
│       ├── NativeMethods.cs     P/Invoke: setupapi, hid, winusb, kernel32
│       ├── NeoHidSwitcher.cs    BD04 WinUSB mode-switch (5 control transfers)
│       └── NeoUsbTransport.cs   BD01 WinUSB bulk transport + registry path fallback
└── NeoSync.App/               (.NET 8 WinForms)
    ├── MainForm.cs             Connect/Disconnect/Download/Upload/filter logic
    └── MainForm.Designer.cs    TableLayoutPanel layout, AutoSize buttons
```

---

## ⚡ HIGH PRIORITY: Driver distribution problem

**RESOLVED** — libwdi is now bundled and driver installation is handled in-app via `DriverSetupForm`.
See the "Driver lifecycle" section below for full details.

The original Zadig-based manual setup is no longer required.

---

## Known issues / future work

### Mode switch (BD04 → BD01) unreliable
The Neo has **no HID MI_01 interface** — confirmed by device enumeration. The HID switch path
(`NeoHidSwitcher.TrySwitchViaHid`) always falls through to the WinUSB fallback on this device.

WinUSB mode switch (5 control transfers to BD04 WinUSB) works reliably when BD04 WinUSB is
loaded. After mode switch, TryOpen succeeds at ~2.4s in the normal case.

**Transient failure after driver update:** Windows sometimes fails to auto-load WinUSB on BD01
after a fresh install (CM_PROB_FAILED_START or device doesn't enumerate). This is a Windows PnP
timing issue — `UpdateDriverForPlugAndPlayDevicesU` (used during install) works reliably;
subsequent PnP auto-loads do not always. Observed to self-resolve after 2–3 Connect attempts.
The app now detects this at 5s and shows an actionable message.

### Features not yet implemented
- **Rename files on device** — set file attributes (name field in ASFileAttributes)
- **Create new file slots** — `createFile()` in ASDevice.cc (needs GET_USED_SPACE + SET_FILE_ATTRIBUTES + COMMIT + WRITE_RAW_FILE)
- **Backup all files** — iterate all applets and download all files to a folder
- **Auto-launch on device connect** — register for USB device arrival events
- **Automatic mode switch on startup** — libwdi for driver install, then auto-switch
- **File inspector** — display/edit file attributes (name, size, flags)
- **Drag and drop** — drag .txt onto tree to upload, drag from tree to Explorer

### Security / privacy notes (from original design)
- Zero 3rd-party NuGet runtime packages — all USB via Windows P/Invoke
- `CryptographicOperations.ZeroMemory()` called on all byte[] containing file content after transfer
- No temp files — write directly to user-chosen path
- No content logging — diagnostic log at `%TEMP%\neosync_diag.txt` logs only command codes/counts
  (this log should be removed or made opt-in before release)
- INF file (`neosync.inf`) included in repo for future signed-driver distribution
- Code signing required before any public release (Secure Boot blocks unsigned INF installs)

---

## Driver lifecycle

### Overview
WinUSB must be installed on two VID/PIDs: BD04 (keyboard mode, for mode switch) and BD01
(comms mode, for file transfer). The Neo has no HID MI_01 interface so WinUSB on BD04 is
mandatory — the keyboard cannot be preserved.

libwdi 1.5.1 is bundled at `NeoSync.App/native/libwdi.dll`. Each `wdi_prepare_driver` call
stages a new OEM INF in `C:\Windows\INF`, generating a unique random device interface GUID.

### Install flow (`NeoDriverInstaller.InstallBoth`)
1. If BD01 is already present (Neo in comms mode or `DriverError`): skip mode switch, force-install BD01 directly.
2. Otherwise: try HID switch (always fails on Neo) → install BD04 WinUSB → retry mode switch loop (30×500ms) → 7s sleep for BD01 enumeration.
3. Install BD01 WinUSB with `hardware_id` set — this calls `UpdateDriverForPlugAndPlayDevicesU` (force-install while connected).
4. After install: poll `NeoDriverState.Detect().Comms` for up to 15s. If still not `WinUsb`, retry install once.
5. Success dialog says **"Unplug and replug your Neo, then click Connect"** — force-install on a connected device requires a clean re-enumeration before WinUSB is reliably accessible.

### Remove flow (`NeoDriverInstaller.RemoveAllMatchingInfs`)
Old approach (5-pass loop) was defeated by 29 accumulated OEM INFs — Windows kept reassigning fallbacks.

New approach:
1. Scan all `C:\Windows\INF\oem*.inf` files for VID_081E&PID_BD04 / VID_081E&PID_BD01 content.
2. Delete every match via `pnputil /delete-driver <inf> /uninstall /force`.
3. No INFs left = no fallback = device goes to "no driver" state.

Recommend unplugging Neo before removing — avoids Windows PnP reassignment race.

### Connect flow (after install)
After unplug/replug + clicking Connect:
- `TrySwitchToCommsMode` sends mode switch via BD04 WinUSB.
- Step 1 poll (25s, 300ms interval): waits for `NeoUsbTransport.TryOpen` to succeed.
- **At 5s**: if TryOpen is still failing, exit early with guidance:
  - `"Open failed on [...]"` in diag → stale DeviceClasses registry path → prompt replug.
  - `"FindDevicePath returned null"` → BD01 didn't enumerate this attempt → prompt retry (self-resolves in 2–3 attempts).
- Step 2 poll (10s): if mode switch failed but BD01 already present (previous session), polls TryOpen directly.

### Known WinUSB transient failure
After a clean install, Windows sometimes fails to auto-load WinUSB on BD01 on the first 1–2
Connect attempts (CM_PROB_FAILED_START or device disappears after mode switch). Root cause:
`UpdateDriverForPlugAndPlayDevicesU` triggers a device reset that makes WinUSB load cleanly;
normal PnP auto-load does not. Self-resolves after 2–3 attempts — no reinstall needed.

### Device instance IDs (this machine)
- BD04: `USB\VID_081E&PID_BD04\5&938d0f6&0&13`
- BD01: `USB\VID_081E&PID_BD01\5&938d0f6&0&13`

These instance IDs are hardcoded in `InstallBoth` and `ConnectAsync` — will need updating for
distribution (use SetupDi enumeration instead).

---

## Picking this up again

### Build and run
```powershell
dotnet build C:\repos\neosync\NeoSync.App\NeoSync.App.csproj -c Release
# Run as administrator — required for driver install/remove
C:\repos\neosync\NeoSync.App\bin\Release\net8.0-windows\NeoSync.App.exe
```
Build is currently clean (0 errors, 0 warnings). The app requires .NET 8 Desktop Runtime.

`libwdi.dll` lives at `NeoSync.App\native\libwdi.dll` and is copied to the output directory
by a `<None>` item in the csproj. The DLL is from libwdi 1.5.1 (x64 release build from
`C:\repos\libwdi`).

### Full driver test cycle
1. Run as admin. Neo plugged in as BD04 (keyboard mode).
2. **Advanced → Remove WinUSB drivers** → both status indicators go red.
3. Unplug Neo.
4. Replug Neo.
5. **Connect** → DriverSetupForm opens → **Install** → wait ~40s → dialog: "Unplug and replug".
6. Unplug Neo, replug Neo.
7. **Connect** → should connect. If it shows "BD01 did not appear — click Connect again":
   click Connect again (Windows sometimes needs 2–3 attempts after a fresh install).
8. File list appears. Download a file to verify end-to-end.
9. Repeat from step 2 for a second cycle to confirm clean behaviour.

### Diagnostic log
`%TEMP%\neosync_diag.txt` — appended each run. Key entries:
- `Switch=True` — mode switch command sent successfully to BD04 WinUSB
- `TryOpen=True (after switch, 2400ms)` — BD01 WinUSB accessible, connection made
- `BD01 did not appear...` — transient WinUSB load failure; retry Connect
- `BD01 driver not responding...` — stale DeviceClasses registry path; replug needed
- `TryOpen=False after 25000ms wait` — timeout (should not appear with new early-exit code)

Remove or gate this file behind a debug flag before release.

### Hardcoded instance IDs — must fix before distribution
`5&938d0f6&0&13` is the USB port-path of this specific machine's USB controller. Other
machines will have a different suffix, so `IsDevicePresent`, `InstallBoth`, and `ConnectAsync`
will fail to find BD01.

**Where they appear:**
- `NeoDriverInstaller.cs` — `const string Bd01InstanceId` in `InstallBoth`
- `MainForm.cs` — `NativeMethods.IsDevicePresentOrPhantom(...)` in `ConnectAsync`

**Fix:** replace both with a SetupDi enumeration that finds the first device instance matching
`VID_081E&PID_BD01` regardless of port path. `NativeMethods.cs` already has all the required
SetupDi P/Invoke; it's just a matter of writing `FindInstanceId(ushort vid, ushort pid)`.

### `TryFixBd01DriverError` — dead code
`NeoDriverInstaller.TryFixBd01DriverError(workDir)` is a public helper added during development
but is no longer called anywhere — the decision was that force-reinstall during Connect is wrong
and a replug is the right fix. It can be removed if desired.

### Post-install BD01 verification loop
`InstallBoth` polls `NeoDriverState.Detect().Comms` for up to 15s after BD01 install and
retries once if WinUSB isn't running. This is correct in principle but `NeoDriverState.Detect()`
reads from registry and `CM_Locate_DevNodeW` with flags=0 can return "no problem" (0) for a
device in a transitional state, making the DriverError check unreliable. The retry may not fire
even when it should. This has not caused visible user-facing failures because the "unplug and
replug" instruction in the success dialog provides a clean re-enumeration anyway.

### Next work items (priority order)
1. **Fix hardcoded instance IDs** — required before the app works on any other machine.
2. **Remove or flag the diagnostic log** — `neosync_diag.txt` leaks file operation timing; should be opt-in.
3. **Code-sign the app and INF** — required for release; Secure Boot blocks unsigned INF installs.
4. **File rename on device** — `SET_FILE_ATTRIBUTES` + `COMMIT` (protocol already understood).
5. **Backup all files** — iterate applets, download all files to a folder.
6. **Auto-launch on device connect** — `WM_DEVICECHANGE` / `RegisterDeviceNotification`.

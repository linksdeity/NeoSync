# NeoSync

A Windows tool for the AlphaSmart Neo and Neo2. It connects over USB, switches
the device into transfer mode, and lets you download or upload AlphaWord text
files without needing the original AlphaSmart Manager software.

This is an independent, unofficial project. It is not affiliated with or
endorsed by Renaissance Learning or AlphaSmart.

## What it does

- Connects to a Neo over USB and switches it from keyboard mode into file
  transfer mode automatically.
- Lists the AlphaWord files on the device, with size and a short content
  preview.
- Downloads and uploads files as plain text.
- Quick Sync: one click to pull every file on the device into a folder on
  your PC, backing up whatever was already there first.
- Installs its own WinUSB driver for the device, since Windows blocks direct
  USB access to a device that is enumerated as a keyboard.

## Requirements

- Windows 10 or later, 64-bit.
- An AlphaSmart Neo or Neo2.
- Administrator rights for the one-time driver install (the installer asks
  for this itself; day-to-day use afterward does not require running as
  admin).

So far this has only been tested against a Neo2. It's expected to work on
the original Neo too, since both devices share the same USB IDs and file
transfer protocol, but that hasn't been confirmed on real Neo1 hardware yet.
If you try it on a Neo1, let us know how it goes either way.

## Installing

Download the latest installer from the [Releases page](https://github.com/linksdeity/NeoSync/releases) and run it.

The installer isn't code-signed yet, so Windows SmartScreen will likely show
a "Windows protected your PC" warning when you run it. This is expected for
a small unsigned tool, not a sign that anything is wrong. Click "More info",
then "Run anyway" to proceed.

Once running, the installer will:

1. Ask you to accept a short disclaimer (this is unofficial, third-party
   software, use at your own risk).
2. Install a signed WinUSB driver for the Neo's two USB modes.
3. Ask you to connect or reconnect the Neo so it can do the first mode
   switch.

After that, the Neo will no longer work as a plain USB keyboard on that PC,
since NeoSync's driver replaces the keyboard driver in order to talk to the
device directly. This can be undone from Advanced > Remove WinUSB Drivers in
the app, followed by unplugging and replugging the Neo.

## Using it

Open NeoSync, plug in the Neo, and click Connect. Once connected, select a
file in the tree and click Download or Upload, or use Quick Sync to pull
everything at once. Advanced options (driver reinstall/removal, diagnostic
logging, checking for updates) are under the Advanced menu.

## Building from source

Requires the .NET 8 SDK and Inno Setup 7.

```powershell
dotnet build NeoSync.slnx -c Release
```

To build the full installer (publishes the app and the background service,
then compiles the Inno Setup script):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version "0.9.0"
```

This requires a pre-signed WinUSB driver package in `installer\driver\`. See
`installer\driver\create-driver-package.ps1` to generate one (needs a code
signing certificate).

See `NOTES.md` for protocol details, architecture notes, and known issues.

## How it works, briefly

The Neo has two USB personalities: it enumerates as a keyboard by default,
and switches into a bulk transfer mode on request. NeoSync sends that
switch command, then talks to the device using AlphaSmart's file transfer
protocol to list, read, and write files. A small background service
(NeoSyncHelper) handles the driver installation and mode switching, since
those operations need administrator rights that the main app does not
otherwise require.

## Credits

NeoSync's understanding of the USB protocol and file formats was developed
by reading two existing open source projects as reference:

- [AlphaSync](https://github.com/tSoniq/alphasync), a macOS tool for the same
  hardware.
- [neotools](https://github.com/rbanffy/neotools), a Linux command-line tool
  for the same hardware.

No code from either project was copied. See `THIRD_PARTY_LICENSES.txt` for
full attribution.

## License

MIT. See `LICENSE`.

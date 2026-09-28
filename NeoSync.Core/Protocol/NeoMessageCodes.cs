// Message command codes for the AlphaSmart Neo protocol.
// Ported from ASMessage.h - Copyright (c) 2008-2013 tSoniq (original reverse-engineering).
namespace NeoSync.Core.Protocol;

internal static class NeoMessageCodes
{
    // ── Requests (host → device) ────────────────────────────────────────────
    public const byte RequestVersion           = 0x00;
    public const byte RequestBlockWrite        = 0x02;
    public const byte RequestListApplets       = 0x04;
    public const byte RequestWriteApplet       = 0x06;
    public const byte RequestRestart           = 0x08;
    public const byte RequestSetBaudRate       = 0x09;
    public const byte RequestGetSettings       = 0x0c;
    public const byte RequestSetSettings       = 0x0d;
    public const byte RequestSetApplet         = 0x0e;
    public const byte RequestReadApplet        = 0x0f;
    public const byte RequestBlockRead         = 0x10;
    public const byte RequestEraseApplets      = 0x11;
    public const byte RequestReadFile          = 0x12;
    public const byte RequestGetFileAttributes = 0x13;
    public const byte RequestWriteFile         = 0x14;
    public const byte RequestConfirmWriteFile  = 0x15;
    public const byte RequestSmallRomUpdater   = 0x18;
    public const byte RequestGetAvailSpace     = 0x1a;
    public const byte RequestGetUsedSpace      = 0x1b;
    public const byte RequestReadRawFile       = 0x1c;
    public const byte RequestSetFileAttributes = 0x1d;
    public const byte RequestCommit            = 0x1e;
    public const byte RequestWriteRawFile      = 0x1f;

    // ── Responses (device → host) ───────────────────────────────────────────
    public const byte ResponseVersion           = 0x40;
    public const byte ResponseBlockWrite        = 0x42;
    public const byte ResponseBlockWriteDone    = 0x43;
    public const byte ResponseListApplets       = 0x44;
    public const byte ResponseWriteApplet       = 0x46;
    public const byte ResponseSetBaudRate       = 0x4a;
    public const byte ResponseGetSettings       = 0x4b;
    public const byte ResponseSetApplet         = 0x4c;
    public const byte ResponseBlockRead         = 0x4d;
    public const byte ResponseBlockReadEmpty    = 0x4e;
    public const byte ResponseWriteFile         = 0x50;
    public const byte ResponseConfirmWriteFile  = 0x51;
    public const byte ResponseRestart           = 0x52;
    public const byte ResponseReadFile          = 0x53;
    public const byte ResponseGetAvailSpace     = 0x58;
    public const byte ResponseGetUsedSpace      = 0x59;
    public const byte ResponseGetFileAttributes = 0x5a;
    public const byte ResponseSetFileAttributes = 0x5b;
    public const byte ResponseCommit            = 0x5c;

    // ── Errors ──────────────────────────────────────────────────────────────
    public const byte ErrorInvalidBaudRate = 0x86;
    public const byte ErrorInvalidApplet   = 0x8a;
    public const byte ErrorProtocol        = 0x8f;
    public const byte ErrorParameter       = 0x90;
    public const byte ErrorOutOfMemory     = 0x91;
}

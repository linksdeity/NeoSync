// 40-byte binary file attribute structure for the AlphaSmart Neo.
// Ported from ASFileAttributes.h — Copyright (c) 2008-2013 tSoniq (original reverse-engineering).
//
// Binary layout:
//   0x00–0x0f  File name (null-terminated, max 15 chars)
//   0x10–0x17  Password  (null-terminated, max 7 chars)
//   0x18–0x1b  MinSize   (big-endian uint32)
//   0x1c–0x1f  AllocSize (big-endian uint32)
//   0x20–0x23  Flags     (big-endian uint32)
//   0x24–0x25  FileSpace (2 bytes, big-endian uint16)
//   0x26–0x27  Unknown
namespace NeoSync.Core.Protocol;

public sealed class NeoFileAttributes
{
    public const int Size = 40;
    public const int FileNameMaxLength = 15;
    public const int PasswordMaxLength = 7;

    // Flags word bits
    public const uint FlagCurrent  = 0x02;  // file is the currently active file for the applet
    public const uint FlagUnknown1 = 0x04;  // always set for AlphaWord files

    private readonly byte[] _raw = new byte[Size];

    public NeoFileAttributes() { }

    public NeoFileAttributes(byte[] buffer, int offset = 0)
        => Buffer.BlockCopy(buffer, offset, _raw, 0, Size);

    /// <summary>Returns a copy of the raw 40-byte attribute block.</summary>
    public byte[] ToBytes()
    {
        byte[] copy = new byte[Size];
        Buffer.BlockCopy(_raw, 0, copy, 0, Size);
        return copy;
    }

    public void CopyFrom(byte[] buffer, int offset = 0)
        => Buffer.BlockCopy(buffer, offset, _raw, 0, Size);

    public string FileName
    {
        get => ReadNullTerminatedAscii(0x00, FileNameMaxLength);
        set => WriteNullTerminatedAscii(value, 0x00, FileNameMaxLength + 1);
    }

    public string Password
    {
        get => ReadNullTerminatedAscii(0x10, PasswordMaxLength);
        set => WriteNullTerminatedAscii(value, 0x10, PasswordMaxLength + 1);
    }

    public uint MinSize
    {
        get => ReadU32(0x18);
        set => WriteU32(0x18, value);
    }

    public uint AllocSize
    {
        get => ReadU32(0x1c);
        set => WriteU32(0x1c, value);
    }

    public uint Flags
    {
        get => ReadU32(0x20);
        set => WriteU32(0x20, value);
    }

    public ushort FileSpace
    {
        get => (ushort)((_raw[0x24] << 8) | _raw[0x25]);
        set { _raw[0x24] = (byte)(value >> 8); _raw[0x25] = (byte)(value & 0xff); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string ReadNullTerminatedAscii(int offset, int maxLen)
    {
        int len = 0;
        while (len < maxLen && _raw[offset + len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(_raw, offset, len);
    }

    private void WriteNullTerminatedAscii(string value, int offset, int fieldLen)
    {
        // Zero the field first
        for (int i = 0; i < fieldLen; i++) _raw[offset + i] = 0;
        if (string.IsNullOrEmpty(value)) return;
        byte[] encoded = System.Text.Encoding.ASCII.GetBytes(value);
        int copyLen = Math.Min(encoded.Length, fieldLen - 1); // reserve space for null terminator
        Buffer.BlockCopy(encoded, 0, _raw, offset, copyLen);
    }

    private uint ReadU32(int offset)
        => ((uint)_raw[offset] << 24)
         | ((uint)_raw[offset + 1] << 16)
         | ((uint)_raw[offset + 2] << 8)
         |  _raw[offset + 3];

    private void WriteU32(int offset, uint value)
    {
        _raw[offset]     = (byte)(value >> 24);
        _raw[offset + 1] = (byte)(value >> 16);
        _raw[offset + 2] = (byte)(value >> 8);
        _raw[offset + 3] = (byte)(value & 0xff);
    }
}

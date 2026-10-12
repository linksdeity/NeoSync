// Core AlphaSmart Neo device protocol.
// Ported from ASDevice.cc/h - Copyright (c) 2008-2013 tSoniq (original reverse-engineering).
//
// Security notes:
//   - Byte arrays holding file content are zeroed after use.
//   - No content is written to temp files or logged.
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NeoSync.Core.Protocol;
using NeoSync.Core.Usb;
// INeoTransport is used only internally; NeoDevice exposes NeoUsbTransport as the public constructor parameter.

namespace NeoSync.Core.Device;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NeoDevice : IDisposable
{
    // Minimum ASM protocol version the device must support
    private const int MinProtocolVersion = 0x0230;

    // System applet ID used for dialogueStart default
    private const ushort AppletIdSystem = 0x0000;

    // Applet header size (from ASApplet.h: kASAppletHeaderSize = 0x84)
    private const int AppletHeaderSize = 0x84;

    // Hard limits from ASDevice.cc
    private const int MaxHeadersPerRead = 7;
    private const int MaxBlockWrite     = 1024;

    // Applet header field offsets (from ASApplet.h)
    private const int AppletHeaderOffsetSignature  = 0x00;
    private const int AppletHeaderOffsetFlags      = 0x10;   // uint32, bit 0 = hidden
    private const int AppletHeaderOffsetAppletId   = 0x14;
    private const int AppletHeaderOffsetFileCount  = 0x17;
    private const int AppletHeaderOffsetName       = 0x18;
    private const int AppletNameLength             = 36;
    private const uint AppletSignature             = 0xc0ffeead;

    private readonly NeoUsbTransport _transport;
    private bool _disposed;

    public NeoDevice(NeoUsbTransport transport)
        => _transport = transport;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Enumerates all applets present on the device.</summary>
    public List<NeoApplet> EnumerateApplets()
    {
        var applets = new List<NeoApplet>();

        if (!DialogueStart()) return applets;

        int startIndex = 0;
        while (true)
        {
            byte[] buf = new byte[AppletHeaderSize * MaxHeadersPerRead];
            if (!RawReadAppletHeaders(buf, startIndex, MaxHeadersPerRead, out int count)) break;
            if (count == 0) break;

            for (int i = 0; i < count; i++)
            {
                int offset = i * AppletHeaderSize;
                NeoApplet? applet = ParseAppletHeader(buf, offset);
                if (applet != null) applets.Add(applet);
            }

            startIndex += count;
            if (count < MaxHeadersPerRead) break;
        }

        DialogueEnd(true);
        return applets;
    }

    /// <summary>
    /// Sends a restart command to the Neo. The device will reboot and
    /// re-enumerate in HID keyboard mode (PID=0xBD04).
    /// </summary>
    public void Restart()
    {
        if (!DialogueStart()) return;

        var msg = new NeoMessage(NeoMessageCodes.RequestRestart);
        if (!SendMessage(msg)) return;

        // Wait for the response - the device reboots after sending it
        var response = new NeoMessage();
        ReceiveMessage(response); // ignore result; device may cut off before responding

        Thread.Sleep(500); // give the device time to begin its reboot sequence
    }

    /// <summary>
    /// Returns available RAM on the device in bytes (0 on error).
    /// Mirrors ASDevice::systemMemory - uses GET_AVAIL_SPACE.
    /// </summary>
    public uint GetAvailableRam()
    {
        if (!DialogueStart()) return 0;

        var msg = new NeoMessage(NeoMessageCodes.RequestGetAvailSpace);
        uint ram = 0;
        bool ok = Exchange(msg);
        if (ok && msg.Command == NeoMessageCodes.ResponseGetAvailSpace)
            ram = msg.Argument(5, 2) * 256u;  // value is in 256-byte units

        DialogueEnd(ok);
        return ram;
    }

    /// <summary>
    /// Returns available flash/ROM space on the device in bytes (0 on error). Same
    /// GET_AVAIL_SPACE response as GetAvailableRam() (the message carries both a flash32
    /// and a ram16 field - AlphaSync's ASDevice::systemMemory reads both, this codebase
    /// previously only read the RAM half and silently discarded the flash field).
    /// </summary>
    public uint GetAvailableRom()
    {
        if (!DialogueStart()) return 0;

        var msg = new NeoMessage(NeoMessageCodes.RequestGetAvailSpace);
        uint rom = 0;
        bool ok = Exchange(msg);
        if (ok && msg.Command == NeoMessageCodes.ResponseGetAvailSpace)
            rom = msg.Argument(1, 4);

        DialogueEnd(ok);
        return rom;
    }

    /// <summary>
    /// Returns the number of files actually used by an applet via GET_USED_SPACE.
    /// The applet header's FileCount field is not always reliable (e.g. AlphaWord
    /// reports 0 there even when files exist).
    /// </summary>
    public int GetAppletFileCount(NeoApplet applet)
    {
        if (!DialogueStart()) return 0;

        var msg = new NeoMessage(NeoMessageCodes.RequestGetUsedSpace);
        msg.SetArgument(1u, 1, 4);                       // non-zero = all files
        msg.SetArgument((uint)applet.AppletId, 5, 2);

        int fileCount = 0;
        bool ok = Exchange(msg);
        if (ok && msg.Command == NeoMessageCodes.ResponseGetUsedSpace)
            fileCount = (int)msg.Argument(5, 2);

        DialogueEnd(ok);
        return fileCount;
    }

    /// <summary>
    /// Reads the file attributes for a specific file slot within an applet.
    /// Returns null if the file does not exist or an error occurs.
    /// </summary>
    public NeoFileAttributes? GetFileAttributes(NeoApplet applet, int fileIndex)
    {
        if (!DialogueStart()) return null;

        byte[] buf = new byte[NeoFileAttributes.Size];
        bool ok = RawGetFileAttributes(buf, (ushort)applet.AppletId, fileIndex, out int actual);

        NeoFileAttributes? result = null;
        if (ok && actual > 0)
            result = new NeoFileAttributes(buf);

        Array.Clear(buf, 0, buf.Length);
        DialogueEnd(ok);
        return result;
    }

    /// <summary>
    /// Reads raw file content. Returns null on error.
    /// The caller should zero the returned array once done with it.
    /// </summary>
    public byte[]? ReadFile(NeoApplet applet, int fileIndex, int maxBytes = 65536)
    {
        if (!DialogueStart()) return null;

        byte[] buf = new byte[maxBytes];
        bool ok = RawReadFile(buf, maxBytes, out int actual, (ushort)applet.AppletId, fileIndex, raw: true);

        byte[]? result = null;
        if (ok && actual > 0)
        {
            result = new byte[actual];
            Buffer.BlockCopy(buf, 0, result, 0, actual);
        }

        // Zero the working buffer regardless of success
        CryptographicOperations.ZeroMemory(buf);
        DialogueEnd(ok);
        return result;
    }

    /// <summary>
    /// Writes raw file content to an existing file slot.
    /// The caller should zero <paramref name="data"/> once done with it.
    /// </summary>
    public bool WriteFile(NeoApplet applet, int fileIndex, byte[] data)
    {
        bool result = DialogueStart();
        if (result) result = RawWriteFile(data, data.Length, (ushort)applet.AppletId, fileIndex, raw: true);
        return DialogueEnd(result);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _transport.Dispose();
        }
    }

    // ── Framing (dialogueStart / dialogueEnd) ────────────────────────────────

    private bool DialogueStart(ushort appletId = AppletIdSystem)
        => Hello() && Reset() && SwitchApplet(appletId);

    private bool DialogueEnd(bool status)
    {
        Reset();
        return status;
    }

    // ── Low-level transport handshake ────────────────────────────────────────

    /// <summary>
    /// Puts the device into ASM mode and validates the protocol version.
    /// Writes 0x01, expects a 2-byte version response.
    /// </summary>
    private bool Hello()
    {
        const int shortTimeout = 100;
        const int maxRetries   = 10;

        byte[] request = [0x01];
        byte[] response = new byte[8];

        int actual = 0;
        bool ok = _transport.Write(request, 1, shortTimeout)
               && _transport.Read(response, 8, out actual, shortTimeout);

        for (int retry = 0; (!ok || actual != 2) && retry < maxRetries; retry++)
        {
            Reset();
            Thread.Sleep(100);
            ok = _transport.Write(request, 1, shortTimeout)
              && _transport.Read(response, 8, out actual, shortTimeout);
        }

        if (!ok || actual != 2) return false;

        int version = (response[0] << 8) | response[1];
        return version >= MinProtocolVersion;
    }

    /// <summary>Sends the 8-byte protocol reset sequence.</summary>
    private bool Reset()
    {
        byte[] cmd = [0x3f, 0xff, 0x00, 0x72, 0x65, 0x73, 0x65, 0x74];
        return _transport.Write(cmd, cmd.Length);
    }

    /// <summary>
    /// Sends the switch-applet command and waits for the "Switched" response.
    /// </summary>
    private bool SwitchApplet(ushort appletId = AppletIdSystem)
    {
        byte[] cmd      = [0x3f, 0x53, 0x77, 0x74, 0x63, 0x68, 0x00, 0x00];
        byte[] expected = [0x53, 0x77, 0x69, 0x74, 0x63, 0x68, 0x65, 0x64]; // "Switched"

        cmd[6] = (byte)(appletId >> 8);
        cmd[7] = (byte)(appletId & 0xff);

        byte[] response = new byte[8];
        if (!_transport.Write(cmd, 8) || !_transport.Read(response, 8, out int actual) || actual != 8)
            return false;

        for (int i = 0; i < 8; i++)
            if (response[i] != expected[i]) return false;

        return true;
    }

    // ── Message send/receive ──────────────────────────────────────────────────

    private bool SendMessage(NeoMessage message)
    {
        byte[] buf = new byte[NeoMessage.Size];
        message.CopyTo(buf);
        return _transport.Write(buf, NeoMessage.Size);
    }

    private bool ReceiveMessage(NeoMessage message)
    {
        byte[] buf = new byte[NeoMessage.Size];
        if (!_transport.Read(buf, NeoMessage.Size, out int actual) || actual != NeoMessage.Size)
            return false;
        message.CopyFrom(buf);
        return message.IsValid();
    }

    private bool Exchange(NeoMessage message)
        => SendMessage(message) && ReceiveMessage(message);

    private bool Exchange(NeoMessage message, byte expectedResponse)
    {
        if (!Exchange(message)) return false;
        return message.Command == expectedResponse;
    }

    // ── Checksum ──────────────────────────────────────────────────────────────

    private static ushort DataChecksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        for (int i = 0; i < length; i++) sum += data[offset + i];
        return (ushort)(sum & 0xffff);
    }

    // ── Extended block read/write ─────────────────────────────────────────────

    /// <summary>
    /// Reads extended data via REQUEST_BLOCK_READ / RESPONSE_BLOCK_READ loop.
    /// </summary>
    private bool ReadExtendedData(byte[] dest, int size, out int actual)
    {
        actual = 0;
        if (size == 0) return true;

        var request  = new NeoMessage(NeoMessageCodes.RequestBlockRead);
        var response = new NeoMessage();
        byte[] block = new byte[MaxBlockWrite];

        int remaining = size;
        int offset    = 0;

        while (remaining > 0)
        {
            // Send REQUEST_BLOCK_READ, receive RESPONSE_BLOCK_READ with block metadata
            if (!SendMessage(request) || !ReceiveMessage(response)) return false;

            if (response.Command == NeoMessageCodes.ResponseBlockReadEmpty) break;
            if (response.Command != NeoMessageCodes.ResponseBlockRead) return false;

            int blockSize        = (int)response.Argument(1, 4);
            ushort expectCsum    = (ushort)response.Argument(5, 2);

            if (blockSize == 0) break;
            if (offset + blockSize > dest.Length) return false;

            // Read the block data into a temp buffer, then copy to dest at correct offset
            if (!_transport.Read(block, blockSize, out int read, 0) || read != blockSize)
                return false;

            if (DataChecksum(block, 0, blockSize) != expectCsum) return false;

            Buffer.BlockCopy(block, 0, dest, offset, blockSize);
            offset    += blockSize;
            actual    += blockSize;
            remaining -= blockSize;
        }

        return true;
    }

    /// <summary>
    /// Writes extended data via REQUEST_BLOCK_WRITE / RESPONSE_BLOCK_WRITE / RESPONSE_BLOCK_WRITE_DONE loop.
    /// </summary>
    private bool WriteExtendedData(byte[] source, int length)
    {
        var request  = new NeoMessage();
        var response = new NeoMessage();

        int remaining = length;
        int offset    = 0;

        while (remaining > 0)
        {
            int blockSize = Math.Min(remaining, MaxBlockWrite);
            ushort checksum = DataChecksum(source, offset, blockSize);

            request.Init(NeoMessageCodes.RequestBlockWrite);
            request.SetArgument((uint)blockSize, 1, 4);
            request.SetArgument(checksum, 5, 2);

            if (!Exchange(request, NeoMessageCodes.ResponseBlockWrite)) return false;

            byte[] block = new byte[blockSize];
            Buffer.BlockCopy(source, offset, block, 0, blockSize);
            if (!_transport.Write(block, blockSize)) return false;
            CryptographicOperations.ZeroMemory(block);

            if (!ReceiveMessage(response)) return false;
            if (response.Command != NeoMessageCodes.ResponseBlockWriteDone) return false;

            offset    += blockSize;
            remaining -= blockSize;
        }

        return true;
    }

    // ── Raw device operations ─────────────────────────────────────────────────

    private bool RawReadAppletHeaders(byte[] buffer, int startIndex, int count, out int actual)
    {
        actual = 0;
        if (count > MaxHeadersPerRead) return false;

        var msg = new NeoMessage(NeoMessageCodes.RequestListApplets);
        msg.SetArgument((uint)startIndex, 1, 4);
        msg.SetArgument((uint)count,      5, 2);

        if (!Exchange(msg) || msg.Command != NeoMessageCodes.ResponseListApplets)
            return false;

        int size              = (int)msg.Argument(1, 4);
        ushort expectedCsum   = (ushort)msg.Argument(5, 2);

        if (size == 0) return true;
        if (size > buffer.Length) return false;

        if (!_transport.Read(buffer, size, out int bytesRead) || bytesRead != size)
            return false;

        if (DataChecksum(buffer, 0, bytesRead) != expectedCsum) return false;

        actual = bytesRead / AppletHeaderSize;
        return true;
    }

    private bool RawGetFileAttributes(byte[] attrBuf, ushort appletId, int fileIndex, out int actual)
    {
        actual = 0;

        var msg = new NeoMessage(NeoMessageCodes.RequestGetFileAttributes);
        msg.SetArgument((uint)fileIndex, 4, 1);
        msg.SetArgument(appletId,        5, 2);

        if (!Exchange(msg)) return false;

        if (msg.Command == NeoMessageCodes.ErrorParameter) return true; // file not found - not an error

        if (msg.Command != NeoMessageCodes.ResponseGetFileAttributes) return false;

        int size            = (int)msg.Argument(1, 4);
        ushort expectedCsum = (ushort)msg.Argument(5, 2);

        if (size != NeoFileAttributes.Size) return false;

        if (!_transport.Read(attrBuf, NeoFileAttributes.Size, out int bytesRead) || bytesRead != NeoFileAttributes.Size)
            return false;

        if (DataChecksum(attrBuf, 0, bytesRead) != expectedCsum) return false;

        actual = 1;
        return true;
    }

    private bool RawSetFileAttributes(byte[] attrBuf, ushort appletId, int fileIndex)
    {
        var msg = new NeoMessage(NeoMessageCodes.RequestSetFileAttributes);
        msg.SetArgument((uint)fileIndex, 1, 4);
        msg.SetArgument(appletId,        5, 2);

        if (!Exchange(msg, NeoMessageCodes.ResponseSetFileAttributes)) return false;

        return WriteExtendedData(attrBuf, NeoFileAttributes.Size);
    }

    private bool RawReadFile(byte[] dest, int size, out int actual, ushort appletId, int fileIndex, bool raw)
    {
        actual = 0;

        var request  = new NeoMessage(raw ? NeoMessageCodes.RequestReadRawFile : NeoMessageCodes.RequestReadFile);
        var response = new NeoMessage();

        request.SetArgument((uint)size,      1, 3);
        request.SetArgument((uint)fileIndex, 4, 1);
        request.SetArgument(appletId,        5, 2);

        if (!SendMessage(request)) return false;
        if (!ReceiveMessage(response)) return false;

        return ReadExtendedData(dest, size, out actual);
    }

    private bool RawWriteFile(byte[] source, int size, ushort appletId, int fileIndex, bool raw)
    {
        var request  = new NeoMessage(raw ? NeoMessageCodes.RequestWriteRawFile : NeoMessageCodes.RequestWriteFile);
        var response = new NeoMessage();

        request.SetArgument((uint)fileIndex, 1, 1);
        request.SetArgument((uint)size,      2, 3);
        request.SetArgument(appletId,        5, 2);

        if (!Exchange(request, NeoMessageCodes.ResponseWriteFile)) return false;
        if (!WriteExtendedData(source, size)) return false;

        request.Init(NeoMessageCodes.RequestConfirmWriteFile);
        return Exchange(request, NeoMessageCodes.ResponseConfirmWriteFile);
    }

    // ── Applet header parsing ─────────────────────────────────────────────────

    private static NeoApplet? ParseAppletHeader(byte[] data, int offset)
    {
        if (offset + AppletHeaderSize > data.Length) return null;

        uint sig = ReadU32(data, offset + AppletHeaderOffsetSignature);
        if (sig != AppletSignature) return null;

        uint   appletId  = ReadU16(data, offset + AppletHeaderOffsetAppletId);
        int    fileCount = data[offset + AppletHeaderOffsetFileCount];
        string name      = ReadNullTerminatedAscii(data, offset + AppletHeaderOffsetName, AppletNameLength);
        uint   flags     = ReadU32(data, offset + AppletHeaderOffsetFlags);
        bool   isHidden  = (flags & 0x01) != 0;

        return new NeoApplet
        {
            AppletId  = appletId,
            Name      = name,
            FileCount = fileCount,
            IsHidden  = isHidden
        };
    }

    private static uint ReadU32(byte[] data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static uint ReadU16(byte[] data, int offset)
        => ((uint)data[offset] << 8) | data[offset + 1];

    private static string ReadNullTerminatedAscii(byte[] data, int offset, int maxLen)
    {
        int len = 0;
        while (len < maxLen && data[offset + len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(data, offset, len).Trim();
    }
}

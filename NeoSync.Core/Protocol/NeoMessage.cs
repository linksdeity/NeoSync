// 8-byte command/response message for the AlphaSmart Neo protocol.
// Format: [cmd(1)] [payload(6)] [checksum(1)]
// Checksum = sum of bytes 0-6 mod 256. All multi-byte values are big-endian.
// Ported from ASMessage.h - Copyright (c) 2008-2013 tSoniq (original reverse-engineering).
namespace NeoSync.Core.Protocol;

internal sealed class NeoMessage
{
    private readonly byte[] _data = new byte[8];

    public const int Size = 8;

    public NeoMessage(byte command = NeoMessageCodes.ErrorProtocol)
    {
        _data[0] = command;
        UpdateChecksum();
    }

    /// <summary>Initialises all payload bytes to zero and sets a new command code.</summary>
    public void Init(byte command)
    {
        for (int i = 1; i < 7; i++) _data[i] = 0;
        _data[0] = command;
        UpdateChecksum();
    }

    public byte Command => _data[0];

    /// <summary>Returns true if the checksum byte matches bytes 0-6.</summary>
    public bool IsValid()
    {
        int sum = 0;
        for (int i = 0; i < 7; i++) sum += _data[i];
        return (byte)(sum & 0xff) == _data[7];
    }

    /// <summary>
    /// Reads a big-endian unsigned value from the payload.
    /// </summary>
    /// <param name="offset">Byte offset within the message (1–6 inclusive).</param>
    /// <param name="width">Number of bytes (1–4).</param>
    public uint Argument(int offset, int width)
    {
        if (offset < 1 || offset + width > 7 || width < 1 || width > 4)
            throw new ArgumentOutOfRangeException();

        uint value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | _data[offset + i];
        }
        return value;
    }

    /// <summary>
    /// Writes a big-endian unsigned value into the payload and updates the checksum.
    /// </summary>
    /// <param name="value">Value to write.</param>
    /// <param name="offset">Byte offset within the message (1–6 inclusive).</param>
    /// <param name="width">Number of bytes (1–4).</param>
    public void SetArgument(uint value, int offset, int width)
    {
        if (offset < 1 || offset + width > 7 || width < 1 || width > 4)
            throw new ArgumentOutOfRangeException();

        for (int i = width - 1; i >= 0; i--)
        {
            _data[offset + i] = (byte)(value & 0xff);
            value >>= 8;
        }
        UpdateChecksum();
    }

    /// <summary>Copies the raw 8 bytes into <paramref name="dest"/> starting at <paramref name="destOffset"/>.</summary>
    public void CopyTo(byte[] dest, int destOffset = 0)
        => Buffer.BlockCopy(_data, 0, dest, destOffset, Size);

    /// <summary>Fills this message from 8 raw bytes (e.g. received over USB).</summary>
    public void CopyFrom(byte[] src, int srcOffset = 0)
        => Buffer.BlockCopy(src, srcOffset, _data, 0, Size);

    private void UpdateChecksum()
    {
        int sum = 0;
        for (int i = 0; i < 7; i++) sum += _data[i];
        _data[7] = (byte)(sum & 0xff);
    }
}

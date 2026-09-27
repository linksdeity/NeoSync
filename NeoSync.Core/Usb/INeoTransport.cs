// Abstraction over the USB bulk transport so NeoDevice is testable
// independently of the actual USB hardware.
namespace NeoSync.Core.Usb;

internal interface INeoTransport : IDisposable
{
    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes from the bulk IN pipe.
    /// </summary>
    /// <param name="buffer">Destination buffer (must be at least <paramref name="length"/> bytes).</param>
    /// <param name="length">Number of bytes to read.</param>
    /// <param name="actual">Actual number of bytes received.</param>
    /// <param name="timeoutMs">Timeout in milliseconds; 0 uses the default (20 000 ms).</param>
    bool Read(byte[] buffer, int length, out int actual, int timeoutMs = 0);

    /// <summary>
    /// Writes <paramref name="length"/> bytes to the bulk OUT pipe.
    /// </summary>
    bool Write(byte[] data, int length, int timeoutMs = 0);
}

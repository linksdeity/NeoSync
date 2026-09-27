// Represents an applet (program slot) on the AlphaSmart Neo device.
// Each applet can contain multiple text files.
namespace NeoSync.Core.Device;

public sealed class NeoApplet
{
    internal uint AppletId  { get; init; }
    public string Name      { get; init; } = string.Empty;
    public int    FileCount { get; init; }
    public bool   IsHidden  { get; init; }
    /// <summary>True if this is the AlphaWord text-file applet (AppletId=0xa000).</summary>
    public bool   IsAlphaWord => AppletId == 0xa000u;

    public override string ToString() => $"{Name} ({FileCount} file{(FileCount == 1 ? "" : "s")})";
}

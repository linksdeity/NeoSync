namespace NeoSync.App;

/// <summary>A themed, owner-drawn stand-in for ToolStripProgressBar's marquee style -- the
/// native marquee's track renders via Windows' own visual style, which no color override can
/// reach. Deliberately NOT theme-aware: several status bar borders/bevels are also native-
/// rendered and can't follow a theme either, so the whole status bar area is left at its
/// normal default appearance rather than chasing every remaining native-rendered piece.</summary>
internal sealed class ThemedMarqueeStatusItem : ToolStripItem
{
    private readonly System.Windows.Forms.Timer _timer;
    private float _position;
    private static readonly Color Track = SystemColors.ControlLight;
    private static readonly Color Block = Color.SteelBlue;

    public ThemedMarqueeStatusItem()
    {
        AutoSize = false;
        Size = new Size(100, 16);
        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) =>
        {
            _position += 0.03f;
            if (_position > 1.3f) _position = -0.3f;
            Invalidate();
        };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { _position = -0.3f; _timer.Start(); } else _timer.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var rect = new Rectangle(0, 0, Width, Height);
        using (var bg = new SolidBrush(Track))
            e.Graphics.FillRectangle(bg, rect);

        int blockWidth = Math.Max(20, Width / 4);
        int blockX = (int)(_position * Width);
        var blockRect = Rectangle.Intersect(new Rectangle(blockX, 0, blockWidth, Height), rect);
        using (var fg = new SolidBrush(Block))
            e.Graphics.FillRectangle(fg, blockRect);

        using var border = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

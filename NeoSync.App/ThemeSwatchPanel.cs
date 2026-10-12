namespace NeoSync.App;

/// <summary>Row of clickable color swatches, one per AppTheme, evenly spread across the full
/// width of the Advanced menu. "Default" is shown as a plain system-colored swatch. Plain
/// Panel with manual layout (not FlowLayoutPanel) since WinForms has no built-in
/// "space-between" flow mode for evenly distributing fixed-size children.</summary>
internal sealed class ThemeSwatchPanel : Panel
{
    public event Action<AppTheme>? ThemePicked;

    private readonly List<Panel> _swatches = [];
    private AppTheme _selected = AppTheme.Default;

    public ThemeSwatchPanel()
    {
        Width  = 220;
        Height = 24;

        var tip = new ToolTip();
        foreach (AppTheme theme in AppTheme.All)
        {
            var swatch = new Panel
            {
                Width       = 22,
                Height      = 22,
                BackColor   = theme.Face ?? SystemColors.Control,
                BorderStyle = BorderStyle.FixedSingle,
                Cursor      = Cursors.Hand
            };
            tip.SetToolTip(swatch, theme.Name);
            swatch.Click += (_, _) => ThemePicked?.Invoke(theme);
            swatch.Paint += (_, e) => PaintCheckmark(e.Graphics, swatch, theme);
            _swatches.Add(swatch);
            Controls.Add(swatch);
        }
        Reflow();
    }

    /// <summary>Recolors the panel's own background -- it isn't part of the Form's control
    /// tree (ContextMenuStrip items live outside Form.Controls), so ThemeManager's recursive
    /// walk never reaches it; this must be called explicitly whenever a theme is applied.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        BackColor = theme.Face ?? SystemColors.Control;
        _selected = theme;
        foreach (Panel swatch in _swatches)
            swatch.Invalidate();
    }

    /// <summary>Marks the active swatch with a checkmark in whichever of black/white contrasts
    /// best against that swatch's own color, since the palette spans both light and dark tones.</summary>
    private void PaintCheckmark(Graphics g, Panel swatch, AppTheme theme)
    {
        if (theme.Name != _selected.Name) return;

        double luminance = (0.299 * swatch.BackColor.R + 0.587 * swatch.BackColor.G + 0.114 * swatch.BackColor.B) / 255;
        Color mark = luminance > 0.5 ? Color.Black : Color.White;
        using var pen = new Pen(mark, 2);
        Rectangle r = swatch.ClientRectangle;
        g.DrawLines(pen,
        [
            new PointF(r.Width * 0.2f, r.Height * 0.55f),
            new PointF(r.Width * 0.42f, r.Height * 0.78f),
            new PointF(r.Width * 0.8f, r.Height * 0.25f)
        ]);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Reflow();
    }

    private void Reflow()
    {
        if (_swatches.Count == 0) return;
        int swatchWidth = _swatches[0].Width;
        int gaps = _swatches.Count - 1;
        float spacing = gaps > 0 ? Math.Max(0, (Width - _swatches.Count * swatchWidth) / (float)gaps) : 0;

        float x = 0;
        foreach (Panel swatch in _swatches)
        {
            swatch.Left = (int)Math.Round(x);
            swatch.Top  = (Height - swatch.Height) / 2;
            x += swatchWidth + spacing;
        }
    }
}

using System.Windows.Forms.VisualStyles;

namespace NeoSync.App;

/// <summary>
/// A single button that toggles between two labeled actions via a small arrow zone on its
/// right edge. Clicking the arrow slides the current label out and the other one in; clicking
/// the main area invokes whichever action is currently showing.
/// </summary>
internal sealed class SlideToggleButton : Control
{
    private const int ArrowZoneWidth = 18;

    public string PrimaryText { get; }
    public string SecondaryText { get; }

    /// <summary>Whether the primary action is currently usable. The secondary action and the
    /// arrow toggle itself always remain clickable regardless of this.</summary>
    public bool PrimaryEnabled { get; set; } = true;

    public event EventHandler? PrimaryClicked;
    public event EventHandler? SecondaryClicked;

    private bool _showingSecondary;
    private int _slideOffset;
    private bool _hover;
    private bool _hoverArrow;
    private bool _pressed;
    private readonly System.Windows.Forms.Timer _animTimer;

    private int TextAreaWidth => Math.Max(0, Width - ArrowZoneWidth);

    public SlideToggleButton(string primaryText, string secondaryText)
    {
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _animTimer = new System.Windows.Forms.Timer { Interval = 12 };
        _animTimer.Tick += AnimTimer_Tick;
    }

    /// <summary>Sizes the control to comfortably fit the wider of the two labels plus the
    /// arrow zone, using the same padding feel as a normal AutoSize button.</summary>
    public void SizeToFit(int horizontalPadding = 16)
    {
        int textWidth = Math.Max(
            TextRenderer.MeasureText(PrimaryText, Font).Width,
            TextRenderer.MeasureText(SecondaryText, Font).Width);
        Width = textWidth + horizontalPadding + ArrowZoneWidth;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animTimer.Dispose();
        base.Dispose(disposing);
    }

    private void AnimTimer_Tick(object? sender, EventArgs e)
    {
        int target = _showingSecondary ? TextAreaWidth : 0;
        int step = Math.Max(2, TextAreaWidth / 6);
        if (_slideOffset < target) _slideOffset = Math.Min(target, _slideOffset + step);
        else if (_slideOffset > target) _slideOffset = Math.Max(target, _slideOffset - step);
        Invalidate();
        if (_slideOffset == target) _animTimer.Stop();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool wasPressed = _pressed;
        _pressed = false;
        Invalidate();
        if (!wasPressed || e.Button != MouseButtons.Left) return;
        if (!ClientRectangle.Contains(e.Location)) return;
        if (_animTimer.Enabled) return; // ignore clicks mid-slide

        if (e.X >= TextAreaWidth)
        {
            _showingSecondary = !_showingSecondary;
            _animTimer.Start();
        }
        else if (_showingSecondary)
        {
            SecondaryClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (PrimaryEnabled)
        {
            PrimaryClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hoverArrow = e.X >= TextAreaWidth;
        if (hoverArrow != _hoverArrow) { _hoverArrow = hoverArrow; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _hoverArrow = _pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle rect = ClientRectangle;

        var state = !Enabled ? PushButtonState.Disabled
            : _pressed ? PushButtonState.Pressed
            : _hover   ? PushButtonState.Hot
            : PushButtonState.Normal;
        ButtonRenderer.DrawButton(g, rect, state);

        int textAreaWidth = TextAreaWidth;
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // Clip the two sliding labels to the text zone so neither bleeds into the arrow zone.
        using (Region oldClip = g.Clip)
        {
            g.SetClip(new Rectangle(0, 0, textAreaWidth, rect.Height));

            Brush primaryBrush = (!_showingSecondary && !PrimaryEnabled) ? SystemBrushes.GrayText : SystemBrushes.ControlText;
            float primaryX   = -_slideOffset;
            float secondaryX = textAreaWidth - _slideOffset;
            g.DrawString(PrimaryText,   Font, primaryBrush,             new RectangleF(primaryX,   0, textAreaWidth, rect.Height), sf);
            g.DrawString(SecondaryText, Font, SystemBrushes.ControlText, new RectangleF(secondaryX, 0, textAreaWidth, rect.Height), sf);

            g.Clip = oldClip;
        }

        string arrow = _showingSecondary ? "◀" : "▶";
        var arrowRect = new Rectangle(textAreaWidth, 0, ArrowZoneWidth, rect.Height);
        Brush arrowBrush = _hoverArrow ? SystemBrushes.Highlight : SystemBrushes.ControlText;
        g.DrawString(arrow, Font, arrowBrush, arrowRect, sf);

        using var sep = new Pen(SystemColors.ControlDark);
        g.DrawLine(sep, textAreaWidth, 3, textAreaWidth, rect.Height - 3);
    }
}

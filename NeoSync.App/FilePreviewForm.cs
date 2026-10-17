namespace NeoSync.App;

using System.Runtime.InteropServices;

/// <summary>Window showing a file's full decoded text - the tree only ever shows a short
/// first-words preview, this is for seeing (and now editing) everything.</summary>
internal sealed class FilePreviewForm : Form
{
    private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    private const int EM_GETLINECOUNT        = 0x00BA;
    private const int EM_LINESCROLL          = 0x00B6;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly TextBox _txtBody;
    private readonly ThemedScrollBar _scrollBar;
    private readonly System.Windows.Forms.Timer _scrollPollTimer;
    private readonly Button _btnSave;
    private readonly Label _lblStatus;
    private Panel? _pnlBody;
    private readonly Action<string, Action<bool>> _onSave;
    private readonly uint _availableRam;
    private FlowLayoutPanel _pnlToolbar = null!;
    private FlowLayoutPanel _pnlButtons = null!;
    private AppTheme _appTheme;
    private float _fontSize = 10f;
    private bool _isDirty;
    private bool _suppressTextChanged;
    private string _originalText;
    private string _lastGoodText;

    public FilePreviewForm(string fileName, string text, AppTheme appTheme, Point ownerLocation,
        uint availableRam, Action<string> onDownload, Action<string, Action<bool>> onSave)
    {
        _appTheme = appTheme;
        _availableRam = availableRam;
        _onSave = onSave;

        Text          = fileName;
        StartPosition = FormStartPosition.Manual;
        Font          = new Font("Segoe UI", 9f);
        // Same default size as the main window, offset diagonally so it doesn't sit directly
        // on top of it - the two can be seen and worked with side by side.
        Size          = new Size(760, 860);
        Location      = new Point(ownerLocation.X + 48, ownerLocation.Y + 48);
        MinimumSize   = new Size(380, 240);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        string normalizedText = text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        _originalText = normalizedText;
        _lastGoodText = normalizedText;

        _txtBody = new TextBox
        {
            Multiline  = true,
            ReadOnly   = false,
            ScrollBars = ScrollBars.None,
            WordWrap   = true,
            Font       = new Font("Segoe UI", _fontSize),
            Text       = normalizedText,
            BorderStyle = BorderStyle.None
        };
        // Wired after the initial Text assignment above, so loading the file doesn't itself
        // count as an edit.
        _txtBody.TextChanged += TxtBody_TextChanged;
        // A TextBox with ScrollBars.None doesn't wheel-scroll on its own - drive it manually.
        _txtBody.MouseWheel += (_, e) =>
        {
            if (!_txtBody.IsHandleCreated) return;
            int dy = -(e.Delta / 120) * 3; // 3 lines per notch, matching the usual OS default
            SendMessage(_txtBody.Handle, EM_LINESCROLL, 0, dy);
            if (e is HandledMouseEventArgs hme) hme.Handled = true;
        };

        // Native scrollbars can't be recolored to match a theme - this draws our own instead,
        // driven by polling EM_GETFIRSTVISIBLELINE/EM_GETLINECOUNT (cheap integer reads, unlike
        // the per-line pixel/wrap math that caused problems for the line-numbering feature).
        _scrollBar = new ThemedScrollBar();
        _scrollBar.LineScrollRequested += delta =>
        {
            if (_txtBody.IsHandleCreated) SendMessage(_txtBody.Handle, EM_LINESCROLL, 0, delta);
        };
        _scrollPollTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _scrollPollTimer.Tick += (_, _) => RefreshScrollBar();
        _scrollPollTimer.Start();

        // One uniform size for every toolbar button, wide enough to fit "Download" (the
        // longest label) so nothing looks mismatched next to the single-glyph buttons.
        var buttonSize = new Size(110, 34);

        _btnSave = new Button
        {
            Text = "Save", AutoSize = false, Size = buttonSize, TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 16, 0), Enabled = false
        };
        _btnSave.Click += (_, _) => DoSave();

        var btnDownload = new Button
        {
            Text = "Download", AutoSize = false, Size = buttonSize,
            TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 16, 0)
        };
        btnDownload.Click += (_, _) => onDownload(_txtBody.Text);

        // "+"/"-" rather than arrows - this app already uses \u25b2/\u25bc-style arrows for the
        // Advanced dropdown and the slide-toggle control, where they mean something else entirely.
        var btnFontUp = new Button
        {
            Text = "A+", AutoSize = false, Size = buttonSize, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 2, 0)
        };
        var btnFontDown = new Button
        {
            Text = "A-", AutoSize = false, Size = buttonSize, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8f), Margin = new Padding(0, 0, 16, 0)
        };
        btnFontUp.Click   += (_, _) => ChangeFontSize(+1);
        btnFontDown.Click += (_, _) => ChangeFontSize(-1);

        var tip = new ToolTip();

        // One small swatch button per AppTheme (same convention as the Advanced menu's theme
        // picker, including "Dark Mode" as just another entry), pre-selected on whichever
        // theme is currently active.
        var pnlThemeSwatches = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0)
        };
        var swatchButtons = new List<Button>();
        foreach (AppTheme theme in AppTheme.All)
        {
            var swatch = new Button
            {
                AutoSize    = false,
                Size        = new Size(18, 34),
                FlatStyle   = FlatStyle.Popup,
                BackColor   = theme.Face ?? SystemColors.Control,
                Margin      = new Padding(0, 0, 2, 0)
            };
            tip.SetToolTip(swatch, theme.Name);
            swatch.Click += (_, _) =>
            {
                SelectSwatch(swatchButtons, swatch);
                ApplyTheme(theme);
            };
            swatchButtons.Add(swatch);
            pnlThemeSwatches.Controls.Add(swatch);
        }
        SelectSwatch(swatchButtons, swatchButtons[Array.IndexOf(AppTheme.All, _appTheme)]);

        var pnlToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(8)
        };
        pnlToolbar.Controls.AddRange([_btnSave, btnDownload, btnFontUp, btnFontDown, pnlThemeSwatches]);

        var btnClose = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        _lblStatus = new Label
        {
            AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 0, 0), TextAlign = ContentAlignment.MiddleLeft
        };
        var pnlButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock          = DockStyle.Bottom,
            AutoSize      = true,
            Padding       = new Padding(8)
        };
        pnlButtons.Controls.Add(btnClose);
        pnlButtons.Controls.Add(_lblStatus);

        var pnlBody = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.Fixed3D };
        pnlBody.Controls.Add(_txtBody);
        pnlBody.Controls.Add(_scrollBar);
        pnlBody.Resize += (_, _) => RepositionBody(pnlBody);
        _pnlBody = pnlBody;

        Controls.Add(pnlBody);
        Controls.Add(pnlToolbar);
        Controls.Add(pnlButtons);
        AcceptButton = btnClose;
        CancelButton = btnClose;

        FormClosing += FilePreviewForm_FormClosing;
        FormClosed  += (_, _) => _scrollPollTimer.Dispose();
        // The first focused control on a shown form gets its text auto-selected by default -
        // not wanted here, so explicitly clear it once the window is actually visible.
        // Also re-run layout here: the constructor-time RepositionBody call above used
        // pnlBody's pre-show (not-yet-final) size, so a long file could need the scrollbar
        // without it actually getting laid out until some later resize happened to trigger it.
        Shown += (_, _) =>
        {
            _txtBody.SelectionStart = 0; _txtBody.SelectionLength = 0;
            RefreshScrollBar();
            RepositionBody(pnlBody);
        };

        ApplyTheme(_appTheme);
        _pnlToolbar = pnlToolbar;
        _pnlButtons = pnlButtons;
        RepositionBody(pnlBody);
    }

    private void RepositionBody(Panel pnlBody)
    {
        int scrollBarWidth = _scrollBar.Visible ? 16 : 0;
        int bodyWidth = Math.Max(pnlBody.ClientSize.Width - scrollBarWidth, 1);
        _txtBody.SetBounds(0, 0, bodyWidth, pnlBody.ClientSize.Height);
        _scrollBar.SetBounds(bodyWidth, 0, scrollBarWidth, pnlBody.ClientSize.Height);
    }

    /// <summary>Polled rather than event-driven - plain TextBox has no public scroll
    /// notification - but cheap: just two integer SendMessage reads, not the fragile
    /// per-line pixel/wrap math the line-numbering feature needed.</summary>
    private void RefreshScrollBar()
    {
        if (!_txtBody.IsHandleCreated) return;

        int firstVisibleLine = (int)SendMessage(_txtBody.Handle, EM_GETFIRSTVISIBLELINE, 0, 0);
        int lineCount = (int)SendMessage(_txtBody.Handle, EM_GETLINECOUNT, 0, 0);
        int visibleCapacity = Math.Max(_txtBody.ClientSize.Height / Math.Max(_txtBody.Font.Height, 1), 1);

        bool needed = lineCount > visibleCapacity;
        if (needed != _scrollBar.Visible)
        {
            _scrollBar.Visible = needed;
            if (_pnlBody is not null) RepositionBody(_pnlBody);
        }

        _scrollBar.UpdateState(firstVisibleLine, lineCount, visibleCapacity);
    }

    private void FilePreviewForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isDirty) return;

        DialogResult result = MessageBox.Show(this,
            "You have unsaved changes. Close without saving?",
            "NeoSync - Unsaved Changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) e.Cancel = true;
    }

    /// <summary>Strips characters the Neo can't represent and enforces the device's available
    /// RAM, both live as the user types or pastes - simpler and more foolproof than letting
    /// unsupported input in and highlighting it after the fact.</summary>
    private void TxtBody_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged) return;

        string text = _txtBody.Text;
        string filtered = new(text.Where(NeoCharacterSet.IsSupported).ToArray());
        bool hadIllegalChars = filtered.Length != text.Length;

        byte[] encoded = MainForm.EncodeAlphaWordText(filtered);
        if (_availableRam > 0 && encoded.Length + 1024 > _availableRam)
        {
            ShowStatus("File is full - no more text can be added.", isWarning: true);
            SetTextPreservingCursor(_lastGoodText);
            return;
        }

        if (hadIllegalChars)
        {
            ShowStatus("Removed character(s) the Neo can't display.", isWarning: true);
            SetTextPreservingCursor(filtered);
        }
        else
        {
            ShowStatus(string.Empty, isWarning: false);
        }

        _lastGoodText = filtered;
        _isDirty = filtered != _originalText;
        _btnSave.Enabled = _isDirty;
    }

    private void SetTextPreservingCursor(string newText)
    {
        int cursor = Math.Min(_txtBody.SelectionStart, newText.Length);
        _suppressTextChanged = true;
        _txtBody.Text = newText;
        _txtBody.SelectionStart = cursor;
        _txtBody.SelectionLength = 0;
        _suppressTextChanged = false;
    }

    private void DoSave()
    {
        _btnSave.Enabled = false;
        ShowStatus("Saving\u2026", isWarning: false);
        _onSave(_txtBody.Text, success =>
        {
            if (success)
            {
                _originalText = _lastGoodText; // this is now the clean baseline, not the pre-save text
                _isDirty = false;
                ShowStatus("Saved.", isWarning: false);
            }
            else
            {
                _btnSave.Enabled = true;
                ShowStatus("Save failed.", isWarning: true);
            }
        });
    }

    private void ShowStatus(string message, bool isWarning)
    {
        _lblStatus.ForeColor = isWarning ? Color.Firebrick : (_appTheme.Text ?? SystemColors.ControlText);
        _lblStatus.Text = message;
    }

    private void ChangeFontSize(int delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 7f, 28f);
        _txtBody.Font = new Font(_txtBody.Font.FontFamily, _fontSize);
    }

    /// <summary>Marks one swatch as selected (thicker black border) and resets the rest.</summary>
    private static void SelectSwatch(List<Button> swatches, Button selected)
    {
        foreach (Button swatch in swatches)
        {
            bool isSelected = swatch == selected;
            swatch.FlatAppearance.BorderSize  = isSelected ? 2 : 1;
            swatch.FlatAppearance.BorderColor = isSelected ? Color.Black : SystemColors.ControlDark;
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        _appTheme = theme;
        // "Default" has no override colors, so fall back to system colors.
        (Color back, Color fore) = theme.Face.HasValue
            ? (theme.Face.Value, theme.Text ?? SystemColors.WindowText)
            : (SystemColors.Window, SystemColors.WindowText);
        _txtBody.BackColor = back;
        _txtBody.ForeColor = fore;

        Color track = theme.Hilight ?? SystemColors.ControlLight;
        Color thumb = theme.Accent ?? theme.Shadow ?? SystemColors.ControlDark;
        _scrollBar.SetColors(track, thumb);
    }

    /// <summary>Custom-painted vertical scrollbar for _txtBody - a native TextBox's scrollbar
    /// can't be recolored to match a theme at all, so this replaces it entirely. Only tracks
    /// proportional position (first visible line / total lines), not exact per-row alignment,
    /// which keeps it far simpler and more reliable than the line-numbering feature's attempts
    /// to map logical lines to wrapped visual rows.</summary>
    private sealed class ThemedScrollBar : Control
    {
        public event Action<int>? LineScrollRequested;

        private int _firstVisibleLine;
        private int _lineCount = 1;
        private int _visibleCapacity = 1;
        private Color _trackColor = SystemColors.Control;
        private Color _thumbColor = SystemColors.ControlDark;
        private Rectangle _thumbRect;
        private bool _dragging;
        private int _dragOffsetY;
        private int _lastDragMoveTick;

        public ThemedScrollBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                      ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetColors(Color track, Color thumb)
        {
            _trackColor = track;
            _thumbColor = thumb;
            Invalidate();
        }

        public void UpdateState(int firstVisibleLine, int lineCount, int visibleCapacity)
        {
            _firstVisibleLine = firstVisibleLine;
            _lineCount = Math.Max(lineCount, 1);
            _visibleCapacity = Math.Max(visibleCapacity, 1);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var trackBrush = new SolidBrush(_trackColor))
                e.Graphics.FillRectangle(trackBrush, ClientRectangle);

            _thumbRect = ComputeThumbRect();
            using var thumbBrush = new SolidBrush(_thumbColor);
            e.Graphics.FillRectangle(thumbBrush, _thumbRect);
        }

        private Rectangle ComputeThumbRect()
        {
            if (_lineCount <= _visibleCapacity) return new Rectangle(1, 0, Math.Max(Width - 2, 1), Height);

            const int minThumbHeight = 24;
            int thumbHeight = Math.Max(minThumbHeight, (int)((double)_visibleCapacity / _lineCount * Height));
            int maxScrollLine = Math.Max(_lineCount - _visibleCapacity, 1);
            int availableTravel = Math.Max(Height - thumbHeight, 0);
            int thumbY = Math.Clamp((int)((double)_firstVisibleLine / maxScrollLine * availableTravel), 0, availableTravel);
            return new Rectangle(1, thumbY, Math.Max(Width - 2, 1), thumbHeight);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_thumbRect.Contains(e.Location))
            {
                _dragging = true;
                _dragOffsetY = e.Y - _thumbRect.Y;
            }
            else if (_lineCount > _visibleCapacity)
            {
                int page = Math.Max(_visibleCapacity - 1, 1);
                LineScrollRequested?.Invoke(e.Y < _thumbRect.Y ? -page : page);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging || _lineCount <= _visibleCapacity) return;

            // Cap at ~60Hz - a fast drag on a large document can otherwise fire MouseMove
            // faster than a synchronous scroll+repaint of the textbox can keep up with,
            // which is what shows up as the thumb/text lagging behind the cursor.
            int now = Environment.TickCount;
            if (now - _lastDragMoveTick < 16) return;
            _lastDragMoveTick = now;

            ApplyDragPosition(e.Y);
        }

        private void ApplyDragPosition(int mouseY)
        {
            int availableTravel = Math.Max(Height - _thumbRect.Height, 1);
            int newThumbY = Math.Clamp(mouseY - _dragOffsetY, 0, availableTravel);
            int maxScrollLine = Math.Max(_lineCount - _visibleCapacity, 1);
            int targetLine = Math.Clamp((int)Math.Round((double)newThumbY / availableTravel * maxScrollLine), 0, maxScrollLine);

            int delta = targetLine - _firstVisibleLine;
            if (delta == 0) return;

            LineScrollRequested?.Invoke(delta);
            // Update optimistically rather than waiting for the next poll (up to 50ms away) -
            // without this, rapid MouseMove events during a drag all compute their delta
            // against the same stale position, compounding into wild overshoot.
            _firstVisibleLine = targetLine;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            // Snap to the exact release position - the 60Hz throttle above may have skipped
            // the last move or two right before the button came up.
            if (_dragging && _lineCount > _visibleCapacity) ApplyDragPosition(e.Y);
            _dragging = false;
        }
    }
}

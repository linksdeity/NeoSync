namespace NeoSync.App;

/// <summary>
/// Applies an AppTheme's colors to a form and its controls, and to the Advanced dropdown menu
/// via a custom ProfessionalColorTable (plain BackColor/ForeColor assignment isn't enough for
/// ContextMenuStrip chrome - the renderer controls that). Deliberately scoped to just that one
/// menu (via its own Renderer property, not the global ToolStripManager.Renderer). The status
/// bar is excluded by reference (passed in as statusBar) rather than left to always look
/// native - it's simpler and more reliably neutral-looking to just leave it alone entirely.
/// "Default" resets everything back to normal WinForms/system colors rather than storing
/// captured originals, since none of these controls are customized away from their WinForms
/// defaults elsewhere.
/// </summary>
internal static class ThemeManager
{
    public static void Apply(Control root, AppTheme theme, ContextMenuStrip menuToTheme, Button? accentButton = null, Control? statusBar = null)
    {
        if (theme.Face is null)
        {
            ResetRecursive(root, statusBar);
            menuToTheme.Renderer = null; // falls back to the ambient ToolStripManager renderer
            return;
        }

        ApplyRecursive(root, theme, accentButton, statusBar);
        menuToTheme.Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable(theme));
    }

    private static void ApplyRecursive(Control control, AppTheme theme, Button? accentButton, Control? statusBar)
    {
        // The status bar (and everything inside it) is deliberately left at its normal
        // system colors regardless of theme - see its own construction comment for why.
        if (control == statusBar) return;

        switch (control)
        {
            // The one "primary action" button stands out with the theme's title-bar accent
            // color, paired with white text to match how these schemes pair Accent elsewhere.
            case Button btn when btn == accentButton:
                btn.FlatStyle = FlatStyle.Popup;
                btn.BackColor = theme.Accent!.Value;
                btn.ForeColor = Color.White;
                btn.FlatAppearance.BorderColor         = theme.Shadow!.Value;
                btn.FlatAppearance.MouseOverBackColor  = theme.Hilight!.Value;
                btn.FlatAppearance.MouseDownBackColor  = theme.Shadow!.Value;
                break;
            case Button btn:
                btn.FlatStyle = FlatStyle.Popup;
                btn.BackColor = theme.Face!.Value;
                btn.ForeColor = theme.Text!.Value;
                btn.FlatAppearance.BorderColor         = theme.Shadow!.Value;
                btn.FlatAppearance.MouseOverBackColor  = theme.Hilight!.Value;
                btn.FlatAppearance.MouseDownBackColor  = theme.Shadow!.Value;
                break;
            case TreeView tv:
                tv.BackColor = theme.Face!.Value;
                tv.ForeColor = theme.Text!.Value;
                break;
            case SlideToggleButton stb:
                stb.ApplyTheme(theme);
                break;
            default:
                control.BackColor = theme.Face!.Value;
                break;
        }

        foreach (Control child in control.Controls)
            ApplyRecursive(child, theme, accentButton, statusBar);
    }

    private static void ResetRecursive(Control control, Control? statusBar)
    {
        if (control == statusBar) return;

        switch (control)
        {
            case Button btn:
                btn.FlatStyle = FlatStyle.Standard;
                btn.UseVisualStyleBackColor = true;
                btn.ForeColor = SystemColors.ControlText;
                break;
            case TreeView tv:
                tv.BackColor = SystemColors.Window;
                tv.ForeColor = SystemColors.WindowText;
                break;
            case SlideToggleButton stb:
                stb.ApplyTheme(AppTheme.Default);
                break;
            default:
                control.BackColor = SystemColors.Control;
                break;
        }

        foreach (Control child in control.Controls)
            ResetRecursive(child, statusBar);
    }

    /// <summary>Recolors menu/status-bar chrome to match the theme - ToolStrip-family controls
    /// render through this table rather than their own BackColor/ForeColor properties.</summary>
    private sealed class ThemeColorTable(AppTheme theme) : ProfessionalColorTable
    {
        // Only ever assigned to menuAdvanced's Renderer (a ContextMenuStrip) - no
        // StatusStripGradient overrides here, the status bar is deliberately left untouched.
        public override Color MenuItemSelected             => theme.Hilight!.Value;
        public override Color MenuItemSelectedGradientBegin => theme.Hilight!.Value;
        public override Color MenuItemSelectedGradientEnd   => theme.Hilight!.Value;
        public override Color MenuItemBorder                => theme.Shadow!.Value;
        public override Color MenuBorder                    => theme.Shadow!.Value;
        public override Color ImageMarginGradientBegin       => theme.Face!.Value;
        public override Color ImageMarginGradientMiddle      => theme.Face!.Value;
        public override Color ImageMarginGradientEnd         => theme.Face!.Value;
        public override Color ToolStripDropDownBackground    => theme.Face!.Value;
        public override Color ButtonSelectedHighlight         => theme.Hilight!.Value;
        public override Color ButtonSelectedBorder            => theme.Shadow!.Value;
        public override Color ToolStripBorder                 => theme.Shadow!.Value;
    }
}

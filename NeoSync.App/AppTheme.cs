namespace NeoSync.App;

/// <summary>
/// Classic Windows 2000 "Appearance" color schemes, recreated from
/// https://github.com/tomasz1986/classic2000 (public domain / Unlicense) -- that project
/// faithfully reproduces the original named scheme RGB values, which is what's used here
/// rather than an invented palette. "Default" means "don't override anything - use normal
/// system/WinForms colors."
/// </summary>
internal sealed class AppTheme
{
    public string Name { get; }
    public Color? Accent  { get; }  // ActiveTitle - used for the Connect button and highlights
    public Color? Face    { get; }  // ButtonFace/Window - main background and button face
    public Color? Shadow  { get; }  // ButtonShadow - borders and pressed/shadow tone
    public Color? Hilight { get; }  // ButtonHilight - lighter tone for contrast areas (tree view)
    public Color? Text    { get; }  // WindowText - foreground text color

    private AppTheme(string name, Color? accent, Color? face, Color? shadow, Color? hilight, Color? text)
    {
        Name = name; Accent = accent; Face = face; Shadow = shadow; Hilight = hilight; Text = text;
    }

    public static readonly AppTheme Default = new("Default", null, null, null, null, null);
    public static readonly AppTheme Slate  = new("Slate",  Color.FromArgb(85, 128, 151), Color.FromArgb(157, 185, 200), Color.FromArgb(85, 128, 151),  Color.FromArgb(206, 220, 227), Color.Black);
    public static readonly AppTheme Spruce = new("Spruce", Color.FromArgb(89, 151, 100), Color.FromArgb(162, 200, 169), Color.FromArgb(89, 151, 100),  Color.FromArgb(208, 227, 211), Color.Black);
    public static readonly AppTheme Plum   = new("Plum",   Color.FromArgb(72, 64, 96),   Color.FromArgb(168, 152, 144), Color.FromArgb(120, 96, 88),   Color.FromArgb(216, 208, 200), Color.Black);
    public static readonly AppTheme Brick  = new("Brick",  Color.FromArgb(128, 0, 0),    Color.FromArgb(194, 191, 165), Color.FromArgb(141, 137, 97),  Color.FromArgb(225, 224, 210), Color.Black);
    public static readonly AppTheme Maple  = new("Maple",  Color.FromArgb(128, 0, 0),    Color.FromArgb(230, 216, 174), Color.FromArgb(198, 166, 70),  Color.FromArgb(242, 236, 215), Color.Black);
    public static readonly AppTheme Desert = new("Desert", Color.FromArgb(0, 128, 128),  Color.FromArgb(213, 204, 187), Color.FromArgb(162, 141, 104), Color.FromArgb(234, 230, 221), Color.Black);
    public static readonly AppTheme Rose   = new("Rose",   Color.FromArgb(159, 96, 112), Color.FromArgb(207, 175, 183), Color.FromArgb(159, 96, 112),  Color.FromArgb(231, 216, 220), Color.Black);

    public static readonly AppTheme[] All = [Default, Slate, Spruce, Plum, Brick, Maple, Desert, Rose];

    public static AppTheme ByName(string? name) => All.FirstOrDefault(t => t.Name == name) ?? Default;
}

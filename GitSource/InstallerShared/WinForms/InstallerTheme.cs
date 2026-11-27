using System.Runtime.InteropServices;

namespace GameInstaller.WinForms;

internal static class InstallerTheme
{
    private static bool Dark => !SystemInformation.HighContrast && Application.ColorMode == SystemColorMode.Dark;
    private static readonly Color Background = Color.FromArgb(25, 27, 43);
    private static readonly Color Surface = Color.FromArgb(20, 22, 35);
    private static readonly Color Foreground = Color.FromArgb(246, 245, 255);
    private static readonly Color Accent = Color.FromArgb(182, 164, 255);
    private static readonly Color AccentText = Color.FromArgb(26, 21, 47);
    private static readonly Color DisabledAccent = Color.FromArgb(131, 126, 151);
    private static readonly Color Border = Color.FromArgb(69, 73, 99);
    internal static Color MutedText => Dark ? Color.FromArgb(189, 185, 211) : SystemColors.ControlText;
    internal static Color AccentTextColor => Dark ? Accent : SystemColors.HotTrack;
    internal static Color Divider => Dark ? Border : SystemColors.ControlDark;
    internal static Color StageBar => Dark ? Surface : SystemColors.Control;
    internal static Color ChoiceAccent => Dark ? Accent : SystemColors.WindowText;
    internal static Color ChoiceMark => Dark ? AccentText : SystemColors.Window;
    internal static void Apply(Form form)
    {
        if (!Dark)
            return;
        form.BackColor = Background;
        form.ForeColor = Foreground;
        form.HandleCreated += (_, _) => ApplyCaption(form.Handle);
        if (form.IsHandleCreated)
            ApplyCaption(form.Handle);
    }

    private static void ApplyCaption(IntPtr window)
    {
        int dark = 1;
        int background = ColorTranslator.ToWin32(Color.FromArgb(20, 22, 33));
        int text = ColorTranslator.ToWin32(Foreground);
        DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(window, 35, ref background, sizeof(int));
        DwmSetWindowAttribute(window, 36, ref text, sizeof(int));
    }

    internal static Color Heading(Color? requested)
    {
        if (SystemInformation.HighContrast)
            return SystemColors.ControlText;
        if (!Dark)
            return requested ?? Color.FromArgb(29, 82, 135);
        return requested is { } color && color.GetBrightness() >= .58 ? color : Foreground;
    }

    internal static void Apply(LinkLabel link)
    {
        if (!Dark)
            return;
        link.LinkColor = link.VisitedLinkColor = Accent;
        link.ActiveLinkColor = Foreground;
    }

    internal static void Apply(TextBox readOnly)
    {
        if (!Dark)
            return;
        readOnly.BackColor = Surface;
        readOnly.ForeColor = Foreground;
    }

    internal static void Console(TextBox log)
    {
        if (!Dark)
            return;
        log.BackColor = Surface;
        log.ForeColor = Foreground;
    }

    internal static void Primary(Button button)
    {
        if (!Dark)
            return;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(204, 192, 255);
        button.UseVisualStyleBackColor = false;
        button.EnabledChanged += (_, _) => ColorPrimary(button);
        ColorPrimary(button);
    }

    internal static void Secondary(Button button)
    {
        if (!Dark)
            return;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Surface;
        button.ForeColor = Foreground;
    }

    private static void ColorPrimary(Button button)
    {
        button.BackColor = button.Enabled ? Accent : DisabledAccent;
        button.ForeColor = button.Enabled ? AccentText : Foreground;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);
}

namespace GameInstaller.WinForms;

internal static class InstallerWindow
{
    internal const int BodyPixels = 23;
    internal static void ScaleText(Form window, Size reference, Control? heading = null,
        Control? console = null, Action<float>? layout = null)
    {
        Font? bodyFont = null;
        Font? headingFont = null;
        Font? consoleFont = null;
        float previousScale = 0;

        void Fit()
        {
            float width = window.ClientSize.Width / (float)reference.Width;
            float height = window.ClientSize.Height / (float)reference.Height;
            float scale = Math.Clamp(Math.Min(width, height * 1.18f), 0.92f, 2.4f);
            if (Math.Abs(scale - previousScale) < 0.04f)
                return;
            previousScale = scale;

            window.SuspendLayout();
            ReplaceFont(window, ref bodyFont, new Font("Segoe UI", BodyPixels * scale, FontStyle.Regular, GraphicsUnit.Pixel));
            if (heading != null)
                ReplaceFont(heading, ref headingFont, new Font("Segoe UI", 44 * scale, FontStyle.Bold, GraphicsUnit.Pixel));
            if (console != null)
                ReplaceFont(console, ref consoleFont, new Font("Consolas", 20 * scale, FontStyle.Regular, GraphicsUnit.Pixel));
            layout?.Invoke(scale);
            window.ResumeLayout(true);
        }

        window.ClientSizeChanged += (_, _) => Fit();
        window.Shown += (_, _) => Fit();
        window.Disposed += (_, _) =>
        {
            bodyFont?.Dispose();
            headingFont?.Dispose();
            consoleFont?.Dispose();
        };
        Fit();
    }

    private static void ReplaceFont(Control control, ref Font? previous, Font font)
    {
        control.Font = font;
        previous?.Dispose();
        previous = font;
    }

    internal static void FitWorkArea(Form window, double widthShare = 0.58, double heightShare = 0.62)
    {
        window.Shown += (_, _) =>
        {
            Rectangle area = Screen.FromControl(window.Owner ?? window).WorkingArea;
            int margin = 16 * window.DeviceDpi / 96;
            int maxWidth = Math.Max(1, area.Width - margin * 2);
            int maxHeight = Math.Max(1, area.Height - margin * 2);
            window.MinimumSize = new Size(Math.Min(window.MinimumSize.Width, maxWidth), Math.Min(window.MinimumSize.Height, maxHeight));
            int width = Math.Min(maxWidth, Math.Max(window.Width, (int)(area.Width * widthShare)));
            int height = Math.Min(maxHeight, Math.Max(window.Height, (int)(area.Height * heightShare)));
            window.Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        };
    }
}

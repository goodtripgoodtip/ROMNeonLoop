namespace GameInstaller.WinForms;

internal sealed class InstallerCheckBox : CheckBox
{
    internal InstallerCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = true;
        CheckedChanged += (_, _) => Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => InstallerChoice.Measure(this);

    protected override void OnPaint(PaintEventArgs args) => InstallerChoice.Paint(this, args.Graphics, Checked, radio: false);
}

internal sealed class InstallerRadioButton : RadioButton
{
    internal InstallerRadioButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = true;
        CheckedChanged += (_, _) => Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => InstallerChoice.Measure(this);

    protected override void OnPaint(PaintEventArgs args) => InstallerChoice.Paint(this, args.Graphics, Checked, radio: true);
}

internal static class InstallerChoice
{
    private static int MarkSize(Control control) => Math.Max(20, (int)Math.Ceiling(control.Font.GetHeight() * 0.86));

    internal static Size Measure(Control control)
    {
        var text = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.NoPadding);
        int mark = MarkSize(control);
        return new Size(mark + 12 + text.Width + 6, Math.Max(mark, text.Height) + 8);
    }

    internal static void Paint(Control control, Graphics graphics, bool isChecked, bool radio)
    {
        graphics.Clear(control.BackColor);
        Color foreground = control.Enabled ? control.ForeColor : SystemColors.GrayText;
        Color accent = control.Enabled ? InstallerTheme.ChoiceAccent : SystemColors.GrayText;
        int size = MarkSize(control);
        var mark = new Rectangle(2, (control.Height - size) / 2, size - 1, size - 1);
        using var border = new Pen(accent, Math.Max(1, size / 14f));
        using var fill = new SolidBrush(accent);
        if (radio)
        {
            graphics.DrawEllipse(border, mark);
            if (isChecked)
            {
                mark.Inflate(-size / 4, -size / 4);
                graphics.FillEllipse(fill, mark);
            }
        }
        else if (isChecked)
        {
            graphics.FillRectangle(fill, mark);
            using var tick = new Pen(InstallerTheme.ChoiceMark, Math.Max(2, size / 9f));
            graphics.DrawLines(tick,
            [
                new Point(mark.Left + size / 5, mark.Top + size / 2),
                new Point(mark.Left + size * 2 / 5, mark.Top + size * 7 / 10),
                new Point(mark.Left + size * 4 / 5, mark.Top + size * 3 / 10)
            ]);
        }
        else
            graphics.DrawRectangle(border, mark);
        var label = new Rectangle(size + 12, 0, Math.Max(0, control.Width - size - 12), control.Height);
        TextRenderer.DrawText(graphics, control.Text, control.Font, label, foreground,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (control.Focused)
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(control.ClientRectangle, -1, -1));
    }
}

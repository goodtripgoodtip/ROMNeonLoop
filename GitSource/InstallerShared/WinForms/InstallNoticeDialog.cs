namespace GameInstaller.WinForms;

internal sealed class InstallNoticeDialog : Form
{
    internal InstallNoticeDialog(InstallerNotice notice)
    {
        Text = notice.Title;
        Font = new Font("Segoe UI", 13);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ClientSize = new Size(920, 580);
        MinimumSize = new Size(520, 390);
        InstallerWindow.FitWorkArea(this, 0.48, 0.50);
        InstallerTheme.Apply(this);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(25) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label { Text = notice.Title, AutoSize = true, UseMnemonic = false };
        layout.Controls.Add(title, 0, 0);
        var terms = new TextBox
        {
            Text = notice.Text.ReplaceLineEndings(Environment.NewLine), Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, AccessibleName = notice.Title, Margin = new Padding(0, 10, 0, 10)
        };
        InstallerTheme.Apply(terms);
        layout.Controls.Add(terms, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 36), DialogResult = DialogResult.Cancel };
        InstallerTheme.Secondary(close);
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        InstallerWindow.ScaleText(this, new Size(920, 580), title);
        CancelButton = close;
        ActiveControl = close;
    }
}

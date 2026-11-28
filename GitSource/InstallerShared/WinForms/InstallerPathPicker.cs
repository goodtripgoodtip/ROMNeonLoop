namespace GameInstaller.WinForms;

internal sealed class InstallerPathPicker : UserControl
{
    private readonly TextBox path = new() { Dock = DockStyle.Fill, AccessibleName = "File or folder path" };
    private readonly Label status = new() { AutoSize = true, UseMnemonic = false };
    private readonly bool folders;
    private readonly bool allowNew;
    private readonly Action<string> choose;

    internal InstallerPathPicker(string? initial, bool folders, Action<string> choose, Action back,
        string pattern = "*", bool allowNew = false)
    {
        this.folders = folders;
        this.allowNew = allowNew;
        this.choose = choose;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(layout);
        InstallerTheme.Apply(path);
        path.Text = initial ?? "";
        var browse = MakeButton("Browse…");
        var use = MakeButton(folders ? "Use this folder" : "Use this file");
        var goBack = MakeButton("Back");
        InstallerTheme.Primary(use);
        var label = new Label { Text = folders ? "Folder path" : "File path", AutoSize = true };
        layout.Controls.Add(label, 0, 0);
        layout.SetColumnSpan(label, 2);
        layout.Controls.Add(path, 0, 1);
        layout.Controls.Add(browse, 1, 1);
        layout.Controls.Add(status, 0, 2);
        layout.SetColumnSpan(status, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        buttons.Controls.AddRange([goBack, use]);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        browse.Click += (_, _) =>
        {
            try
            {
                string? selection = Browse(FindForm(), path.Text, folders, allowNew, pattern);
                if (selection == null)
                    return;
                path.Text = selection;
                status.Text = "";
                path.Focus();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                status.Text = "Windows couldn’t open the picker. Enter or paste the full path instead.";
            }
        };
        goBack.Click += (_, _) => back();
        use.Click += (_, _) => Choose();
        path.KeyDown += (_, args) =>
        {
            if (args.KeyCode is not (Keys.Enter or Keys.Escape))
                return;
            args.SuppressKeyPress = true;
            if (args.KeyCode == Keys.Enter)
                Choose();
            else
                back();
        };
        ClientSizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(180, ClientSize.Width - 12), 0);
    }

    internal static string? Browse(IWin32Window? owner, string? initial, bool folders, bool allowNew = false,
        string pattern = "*")
    {
        string? directory = FindStart(initial);
        if (folders)
        {
            using var dialog = new FolderBrowserDialog
            {
                AutoUpgradeEnabled = true, Description = "Choose a folder", UseDescriptionForTitle = true,
                ShowNewFolderButton = allowNew, InitialDirectory = directory ?? "",
                SelectedPath = Directory.Exists(initial) ? initial! : ""
            };
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
        }

        using var files = new OpenFileDialog
        {
            AutoUpgradeEnabled = true, Title = "Choose a file", InitialDirectory = directory ?? "",
            FileName = File.Exists(initial) ? Path.GetFileName(initial) : "",
            CheckFileExists = true, CheckPathExists = true, RestoreDirectory = true,
            Filter = pattern == "*" ? "All files|*.*" : $"Matching files ({pattern})|{pattern}|All files|*.*"
        };
        return files.ShowDialog(owner) == DialogResult.OK ? files.FileName : null;
    }

    private static string? FindStart(string? initial)
    {
        string? candidate = initial?.Trim().Trim('"');
        if (string.IsNullOrEmpty(candidate) || !Path.IsPathFullyQualified(candidate))
            return null;
        while (candidate != null && !Directory.Exists(candidate))
            candidate = Path.GetDirectoryName(candidate);
        return candidate;
    }

    private void Choose()
    {
        try
        {
            string value = path.Text.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(value))
                throw new IOException("Enter or browse for a full path.");
            value = Path.GetFullPath(value);
            if (folders ? !allowNew && !Directory.Exists(value) : !File.Exists(value))
                throw new IOException(folders ? "Choose an existing folder." : "Choose an existing file.");
            choose(value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            status.Text = error.Message;
        }
    }

    private static Button MakeButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(92, 40) };
        InstallerTheme.Secondary(button);
        return button;
    }
}

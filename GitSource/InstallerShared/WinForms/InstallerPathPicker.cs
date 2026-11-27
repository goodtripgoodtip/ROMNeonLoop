namespace GameInstaller.WinForms;

internal sealed class InstallerPathPicker : UserControl
{
    private readonly TextBox path = new() { Dock = DockStyle.Top, AccessibleName = "File or folder path" };
    private readonly ListBox entries = new() { Dock = DockStyle.Fill, IntegralHeight = false, Height = 190, AccessibleName = "Files and folders" };
    private readonly Label browseLocation = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label status = new() { AutoSize = true };
    private readonly bool folders;
    private readonly bool allowNew;
    private readonly string pattern;
    private readonly Action<string> choose;
    private string? directory;

    internal InstallerPathPicker(string? initial, bool folders, Action<string> choose, Action back,
        string pattern = "*", bool allowNew = false)
    {
        this.folders = folders;
        this.allowNew = allowNew;
        this.pattern = pattern;
        this.choose = choose;
        Dock = DockStyle.Top;
        AutoSize = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        InstallerTheme.Apply(path);
        entries.BackColor = path.BackColor;
        entries.ForeColor = path.ForeColor;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var listHeight = new RowStyle(SizeType.Absolute, 210);
        layout.RowStyles.Add(listHeight);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(path);
        layout.Controls.Add(browseLocation);
        layout.Controls.Add(entries);
        layout.Controls.Add(status);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var up = new Button { Text = "Up one folder", AutoSize = true };
        var use = new Button { Text = folders ? "Use this folder" : "Use this file", AutoSize = true };
        var cancel = new Button { Text = "Back", AutoSize = true };
        InstallerTheme.Secondary(up);
        InstallerTheme.Secondary(cancel);
        InstallerTheme.Primary(use);
        buttons.Controls.AddRange([up, use, cancel]);
        layout.Controls.Add(buttons);
        up.Click += (_, _) => Browse(directory == null ? null : Directory.GetParent(directory)?.FullName);
        cancel.Click += (_, _) => back();
        use.Click += (_, _) => Choose();
        entries.SelectedIndexChanged += (_, _) =>
        {
            if (entries.SelectedItem is Entry entry)
                path.Text = entry.Path;
        };
        entries.DoubleClick += (_, _) =>
        {
            if (entries.SelectedItem is Entry entry && entry.Folder)
                Browse(entry.Path);
            else
                Choose();
        };
        if (Parent != null)
            listHeight.Height = Math.Clamp(Parent.ClientSize.Height / 2, 145, 430);
        ParentChanged += (_, _) =>
        {
            if (Parent != null)
                listHeight.Height = Math.Clamp(Parent.ClientSize.Height / 2, 145, 430);
        };
        path.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                args.SuppressKeyPress = true;
                Choose();
            }
        };
        string? start = initial;
        if (!string.IsNullOrWhiteSpace(start) && Path.IsPathFullyQualified(start))
            while (start != null && !Directory.Exists(start))
                start = Path.GetDirectoryName(start);
        Browse(Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (allowNew && !string.IsNullOrWhiteSpace(initial))
            path.Text = initial;
    }

    private void Browse(string? candidate)
    {
        try
        {
            Entry[] shown = string.IsNullOrEmpty(candidate) ?
                DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => new Entry(drive.Name, drive.Name, true)).ToArray() :
                Directory.EnumerateDirectories(candidate).Order(StringComparer.OrdinalIgnoreCase)
                    .Select(folder => new Entry(Path.GetFileName(folder), folder, true))
                    .Concat(folders ? [] : Directory.EnumerateFiles(candidate, pattern).Order(StringComparer.OrdinalIgnoreCase)
                        .Select(file => new Entry(Path.GetFileName(file), file, false))).ToArray();
            directory = candidate;
            browseLocation.Text = candidate == null ? "Available drives" : "Browsing " + candidate;
            path.Text = candidate ?? "";
            entries.Items.Clear();
            entries.Items.AddRange(shown);
            status.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            status.Text = "Windows couldn’t read this folder. Enter another path.";
        }
    }

    private void Choose()
    {
        try
        {
            string value = path.Text.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(value))
                throw new IOException("Enter the full path.");
            value = Path.GetFullPath(value);
            if (!folders && Directory.Exists(value))
            {
                Browse(value);
                return;
            }
            if (folders ? !allowNew && !Directory.Exists(value) : !File.Exists(value))
                throw new IOException("Choose an item that exists.");
            choose(value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            status.Text = error.Message;
        }
    }

    private sealed record Entry(string Name, string Path, bool Folder)
    {
        public override string ToString() => Folder ? Name + "  ›" : Name;
    }
}

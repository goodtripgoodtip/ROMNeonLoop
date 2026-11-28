using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GameInstaller.WinForms;

internal sealed class SetupPackage : UserControl
{
    internal const string FirstRunArgument = "--setup";
    internal const string ModSubtitle = "A 2064 Mod";
    private const string PayloadManifest = "NeonLoop.payload.json";
    private readonly ZipArchive package;
    private readonly TextBox folder = new() { Dock = DockStyle.Fill };
    private readonly InstallerCheckBox desktop = new() { Text = "Create a desktop shortcut", Checked = true };
    private readonly InstallerCheckBox startMenu = new() { Text = "Add NeonLoop to the Start menu", Checked = true };
    private readonly Label status = new() { AutoSize = true };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Button browse = new() { Text = "Browse…", AutoSize = true };
    private readonly Button install = new() { Text = "Continue", AutoSize = true };
    private readonly Button uninstall = new() { Text = "Uninstall", AutoSize = true, Visible = false };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly Button back = new() { Text = "Back", AutoSize = true, Visible = false };
    private readonly Func<string, bool, bool, SteamGame, string, Task> onPrepared;
    private string selectedGameFolder;
    private bool working;
    internal bool Busy => working;
    internal Button ContinueButton => install;

    internal static bool TryOpen(string executable, out ZipArchive package)
    {
        package = null;
        if (executable == null)
            return false;
        var stream = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long payloadEnd = PayloadEnd(stream);
            if (payloadEnd == 0)
                return false;
            package = new ZipArchive(new PayloadStream(stream, payloadEnd), ZipArchiveMode.Read);
            return true;
        }
        finally
        {
            if (package == null)
                stream.Dispose();
        }
    }

    private static long PayloadEnd(FileStream stream)
    {
        Span<byte> footer = stackalloc byte[8];
        bool HasMarker(FileStream file, long end)
        {
            if (end < 8)
                return false;
            file.Position = end - 8;
            Span<byte> marker = stackalloc byte[8];
            file.ReadExactly(marker);
            return marker.SequenceEqual("NLOOPPK1"u8);
        }
        if (HasMarker(stream, stream.Length))
            return stream.Length - footer.Length;
        stream.Position = 0;
        using var header = new System.Reflection.PortableExecutable.PEReader(stream,
            System.Reflection.PortableExecutable.PEStreamOptions.LeaveOpen);
        var certificate = header.PEHeaders.PEHeader?.CertificateTableDirectory;
        if (certificate is not { RelativeVirtualAddress: > 0, Size: > 0 } table ||
            (long)table.RelativeVirtualAddress + table.Size > stream.Length)
            return 0;
        for (int padding = 0; padding < 8; ++padding)
        {
            long end = table.RelativeVirtualAddress - padding;
            if (HasMarker(stream, end))
                return end - footer.Length;
            if (end <= 0)
                break;
            stream.Position = end - 1;
            if (stream.ReadByte() != 0)
                break;
        }
        return 0;
    }

    private sealed class PayloadStream(FileStream inner, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => inner.Position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, (int)Math.Min(count, Math.Max(0, length - Position)));
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0)
                throw new IOException("Invalid installer offset.");
            return inner.Seek(target, SeekOrigin.Begin);
        }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static string PackageVersion(ZipArchive package)
    {
        var marker = package.GetEntry(ManagedInstall.Marker) ?? throw new InvalidDataException("Download NeonLoop again. This copy is incomplete.");
        using var reader = new StreamReader(marker.Open());
        string version = reader.ReadToEnd().Trim();
        if (!ManagedInstall.ValidVersion(version))
            throw new InvalidDataException("Download NeonLoop again. This copy is incomplete.");
        return version;
    }

    internal SetupPackage(ZipArchive package, Func<string, bool, bool, SteamGame, string, Task> onPrepared, Action<string> onUninstall,
        string initialFolder = null, Action goBack = null, bool createDesktop = true, bool createStartMenu = true)
    {
        this.package = package;
        this.onPrepared = onPrepared;
        desktop.Checked = createDesktop;
        startMenu.Checked = createStartMenu;
        _ = PackageVersion(package);
        Font = new Font("Segoe UI", 11);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        InstallerTheme.Apply(folder);
        InstallerTheme.Secondary(browse);
        InstallerTheme.Secondary(uninstall);
        InstallerTheme.Secondary(cancel);
        InstallerTheme.Secondary(back);
        InstallerTheme.Primary(install);
        cancel.Click += (_, _) => FindForm()?.Close();
        back.Visible = goBack != null;
        back.Click += (_, _) => goBack?.Invoke();

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 11,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(54, 40, 54, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(layout, 0, 0);
        var title = new Label { Text = "NeonLoop", AutoSize = true, ForeColor = InstallerTheme.Heading(null), Margin = Padding.Empty };
        var subtitle = new Label { Text = ModSubtitle, AutoSize = true,
            ForeColor = InstallerTheme.AccentTextColor, Margin = new Padding(2, 0, 0, 0) };
        var divider = new Panel { Height = 1, Dock = DockStyle.Top, BackColor = InstallerTheme.Divider };
        var location = new Label { Text = "Install location", AutoSize = true };
        AddWide(layout, title, 0);
        AddWide(layout, subtitle, 1);
        AddWide(layout, divider, 2);
        AddWide(layout, location, 3);
        folder.AccessibleName = "NeonLoop installation folder";
        folder.Text = initialFolder ?? ManagedInstall.FindInstalled() ?? ManagedInstall.DefaultFolder;
        folder.Margin = Padding.Empty;
        var field = new Panel { Height = 42, Dock = DockStyle.Fill, Padding = new Padding(1),
            Margin = new Padding(0, 0, 10, 0), BackColor = InstallerTheme.Divider };
        var fieldSurface = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        fieldSurface.BackColor = folder.BackColor;
        folder.BorderStyle = BorderStyle.None;
        fieldSurface.Controls.Add(folder);
        field.Controls.Add(fieldSurface);
        browse.MinimumSize = new Size(105, 42);
        browse.Margin = Padding.Empty;
        layout.Controls.Add(field, 0, 4);
        layout.Controls.Add(browse, 1, 4);
        desktop.Margin = new Padding(0, 18, 0, 5);
        startMenu.Margin = new Padding(0, 5, 0, 7);
        AddWide(layout, desktop, 5);
        AddWide(layout, startMenu, 6);
        var previous = PreviousInstall();
        if (previous != null)
        {
            var legacy = new LinkLabel { Text = "Open previous NeonLoop install", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            InstallerTheme.Apply(legacy);
            legacy.LinkClicked += (_, _) => ManagedInstall.OpenFolder(previous);
            AddWide(layout, legacy, 7);
        }
        status.Margin = new Padding(0, 10, 0, 3);
        AddWide(layout, status, 8);
        AddWide(layout, progress, 9);
        var locate = new LinkLabel { Text = "Choose Steam game folder…", AutoSize = true };
        InstallerTheme.Apply(locate);
        AddWide(layout, locate, 10);
        install.MinimumSize = new Size(144, 46);
        install.Margin = Padding.Empty;
        var actions = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2,
            Padding = new Padding(54, 12, 54, 34), Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 1; i < actions.ColumnCount; i++)
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var requirement = new Label { Text = "Requires 2064: Read Only Memories on Steam. The default folder works without administrator access.", AutoSize = true,
            ForeColor = InstallerTheme.MutedText, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        actions.Controls.Add(requirement, 0, 0);
        actions.SetColumnSpan(requirement, actions.ColumnCount);
        actions.Controls.Add(back, 1, 1);
        actions.Controls.Add(cancel, 2, 1);
        actions.Controls.Add(uninstall, 3, 1);
        actions.Controls.Add(install, 4, 1);
        shell.Controls.Add(actions, 0, 1);
        void ChooseLocation(string current, Action<string> accept, bool allowNew)
        {
            if (working)
                return;
            try
            {
                string selection = InstallerPathPicker.Browse(FindForm(), current, folders: true, allowNew: allowNew);
                if (selection != null)
                    accept(selection);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                Win32Exception or System.Runtime.InteropServices.COMException)
            {
                status.Text = "Windows couldn’t open the folder picker. Enter a full path instead.";
            }
            layout.Hide();
            actions.Hide();
            InstallerPathPicker picker = null;
            void Return()
            {
                picker?.Dispose();
                layout.Show();
                actions.Show();
            }
            picker = new InstallerPathPicker(current, true, path => { accept(path); Return(); },
                Return, allowNew: allowNew);
            picker.Padding = new Padding(42, 32, 42, 0);
            shell.Controls.Add(picker, 0, 0);
        }
        browse.Click += (_, _) => ChooseLocation(folder.Text, path => folder.Text = path, true);
        locate.LinkClicked += (_, _) => ChooseLocation(selectedGameFolder, path =>
        {
            selectedGameFolder = path;
            status.Text = "Steam game folder selected. Continue to verify it.";
        }, false);
        install.Click += async (_, _) =>
        {
            await InstallAsync(() => locate.Visible = true);
        };
        uninstall.Click += (_, _) =>
        {
            if (ManagedInstall.ExistingFolder(folder.Text) is { } existing)
                onUninstall(existing);
            else
                RefreshActions();
        };
        folder.TextChanged += (_, _) => RefreshActions();
        RefreshActions();

        var textSizes = new (Control Control, int Pixels, FontStyle Style)[]
        {
            (title, 56, FontStyle.Bold), (this, InstallerWindow.BodyPixels, FontStyle.Regular),
            (location, InstallerWindow.BodyPixels, FontStyle.Bold), (browse, InstallerWindow.BodyPixels, FontStyle.Bold),
            (install, InstallerWindow.BodyPixels, FontStyle.Bold), (uninstall, InstallerWindow.BodyPixels, FontStyle.Bold),
            (cancel, InstallerWindow.BodyPixels, FontStyle.Regular), (back, InstallerWindow.BodyPixels, FontStyle.Regular)
        };
        var scaledFonts = new List<Font>();
        float previousScale = 0;
        void FitContent()
        {
            float scale = Math.Clamp(MathF.Min(ClientSize.Width / 1020f, ClientSize.Height / 570f), 0.88f, 2.4f);
            int available = Math.Max(200, ClientSize.Width - (int)MathF.Round(114 * scale));
            status.MaximumSize = requirement.MaximumSize = new Size(available, 0);
            if (Math.Abs(previousScale - scale) < 0.04f)
                return;
            previousScale = scale;
            int Px(int value) => (int)MathF.Round(value * scale);
            SuspendLayout();
            Font[] previousFonts = scaledFonts.ToArray();
            scaledFonts.Clear();
            foreach (var item in textSizes)
            {
                var font = new Font(Font.FontFamily, Px(item.Pixels), item.Style, GraphicsUnit.Pixel);
                scaledFonts.Add(font);
                item.Control.Font = font;
            }
            foreach (Font font in previousFonts)
                font.Dispose();
            layout.Padding = new Padding(Px(51), Px(36), Px(51), 0);
            divider.Margin = new Padding(0, Px(20), 0, Px(23));
            location.Margin = new Padding(0, 0, 0, Px(10));
            field.Height = Px(47);
            field.Margin = new Padding(0, 0, Px(11), 0);
            fieldSurface.Padding = new Padding(Px(10), Px(9), Px(10), 0);
            browse.MinimumSize = new Size(Px(105), Px(47));
            desktop.Margin = new Padding(0, Px(21), 0, Px(6));
            startMenu.Margin = new Padding(0, Px(7), 0, Px(8));
            actions.Padding = new Padding(Px(51), Px(12), Px(51), Px(25));
            install.MinimumSize = new Size(Px(144), Px(47));
            uninstall.MinimumSize = new Size(Px(130), Px(47));
            uninstall.Margin = new Padding(0, 0, Px(10), 0);
            ResumeLayout(true);
        }
        Resize += (_, _) => FitContent();
        Disposed += (_, _) => scaledFonts.ForEach(font => font.Dispose());
        FitContent();
    }

    private static void AddWide(TableLayoutPanel layout, Control control, int row)
    {
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }

    private static string PreviousInstall()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Directory.EnumerateDirectories(local, "NeonLoop*")
            .Where(path => Path.GetFileName(path) == "NeonLoop" || Path.GetFileName(path).StartsWith("NeonLoop v", StringComparison.Ordinal))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, ManagedInstall.Marker)) &&
                File.Exists(Path.Combine(path, ManagedInstall.Launcher)));
    }

    private void RefreshActions()
    {
        string existing = ManagedInstall.ExistingFolder(folder.Text);
        uninstall.Visible = existing != null;
        install.Text = existing == null ? "Continue" : "Reinstall";
        status.Text = existing == null ? "" :
            $"NeonLoop {ManagedInstall.ReadVersion(existing)} is already installed here. Reinstall keeps your music and settings.";
    }

    private async Task InstallAsync(Action offerGamePicker)
    {
        working = true;
        install.Enabled = uninstall.Enabled = browse.Enabled = folder.Enabled = desktop.Enabled = startMenu.Enabled =
            back.Enabled = cancel.Enabled = false;
        status.Text = "Looking for 2064: Read Only Memories in Steam…";
        status.Visible = true;
        progress.Style = ProgressBarStyle.Marquee;
        progress.Visible = true;
        try
        {
            string destination = ManagedInstall.Validate(folder.Text.Trim());
            string previous = ManagedInstall.ReadVersion(destination);
            if (previous == null && ManagedInstall.IsProtectedLocation(destination))
                throw new IOException("Choose a folder you can write to without administrator access. NeonLoop saves its game data and settings there.");
            ValidateExisting(destination, previous);
            SteamGame game;
            try
            {
                string saved = previous != null ? SteamGame.SelectedFolder(Path.Combine(ManagedInstall.RuntimeFolder(destination), "Effects")) : null;
                game = await Task.Run(() => VerifySteam(package, selectedGameFolder, saved));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                offerGamePicker();
                throw;
            }
            ManagedInstall.ReviewRuntime(destination, game.Installed);
            await onPrepared(destination, desktop.Checked, startMenu.Checked, game, previous);
        }
        catch (Win32Exception error) when (ManagedInstall.WasDeclined(error))
        {
            status.Text = "Copy cancelled. Choose Browse to use a different location, or try again.";
        }
        catch (Exception error)
        {
            status.Text = error is UnauthorizedAccessException
                ? "Windows couldn’t save NeonLoop here. Choose Browse and try a folder you can write to."
                : error.GetBaseException().Message;
        }
        finally
        {
            working = false;
            if (!IsDisposed)
            {
                install.Enabled = uninstall.Enabled = browse.Enabled = folder.Enabled = desktop.Enabled = startMenu.Enabled =
                    back.Enabled = cancel.Enabled = true;
                progress.Visible = false;
                progress.Style = ProgressBarStyle.Blocks;
            }
        }
    }

    internal static string ReadEntry(ZipArchive package, string name)
    {
        var entry = package.GetEntry("Effects/" + name)
            ?? throw new InvalidDataException("The installer is missing " + name + ".");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    internal static SteamGame VerifySteam(ZipArchive package, string selected, string saved = null)
    {
        string[] seal = ReadEntry(package, "bin/MusicPack/Ready.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).ToArray();
        if (selected == null && !string.IsNullOrWhiteSpace(saved))
        {
            try { return SteamGame.FindSupported(seal, saved); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
        }
        return SteamGame.FindSupported(seal, selected);
    }

    internal static void Extract(ZipArchive package, string destination, string expectedVersion, IProgress<int> progress = null)
    {
        destination = ManagedInstall.Validate(destination);
        _ = PackageVersion(package);
        using var launchLock = new Mutex(false, EffectsLauncher.LaunchMutex);
        if (!EffectsLauncher.TryAcquireLaunchLock(launchLock))
            throw new IOException("Close NeonLoop and try again.");
        try
        {
            EffectsLauncher.CheckNoGame();
            ValidateExisting(destination, expectedVersion);
            InstallFiles.EnsureSpace(destination, PayloadEntries(package).Sum(entry => entry.Length), "copy NeonLoop");
            Publish(package, destination, expectedVersion, progress);
        }
        finally
        {
            launchLock.ReleaseMutex();
        }
    }

    private static void ValidateExisting(string destination, string expectedVersion)
    {
        string actual = ManagedInstall.ReadVersion(destination);
        if (actual != expectedVersion || (actual != null && !ManagedInstall.IsManaged(destination)))
            throw new IOException("The installation changed. Go back and choose the location again.");
        if (actual == null && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose a new or empty folder to keep the files already here.");
    }

    private static void Publish(ZipArchive package, string destination, string previous, IProgress<int> progress)
    {
        string staging = destination + ".setup-" + Guid.NewGuid().ToString("N");
        string backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            int done = 0;
            var owned = new List<string>();
            var payload = PayloadEntries(package).ToArray();
            var manifest = ReadPayloadManifest(package, payload);
            foreach (var entry in payload)
            {
                string path = ManagedInstall.PayloadFile(staging, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.Equals(ManagedInstall.OwnedFiles, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The setup download contains an invalid file path.");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (Stream input = entry.Open())
                using (var output = new FileStream(path, FileMode.CreateNew))
                    InstallFiles.Transfer(input, output, entry.Length, null, CancellationToken.None);
                if (manifest != null)
                {
                    using var data = File.OpenRead(path);
                    if (!Convert.ToHexString(SHA256.HashData(data)).Equals(manifest[entry.FullName].sha256,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Installer verification failed for " + entry.FullName + ". Download a fresh copy.");
                }
                owned.Add(entry.FullName);
                progress?.Report(++done * 100 / payload.Length);
            }
            if (!File.Exists(Path.Combine(staging, ManagedInstall.Launcher)))
                throw new InvalidDataException("The setup download is missing the NeonLoop launcher.");
            bool hasData = previous != null && ManagedInstall.HasCentralRuntime(destination);
            string centralData = Path.Combine(destination, "Data");
            string backedUpData = Path.Combine(backup, "Data");
            var preserved = previous == null ? [] : PreserveLocalFiles(destination, staging, hasData ? centralData : null);
            File.WriteAllLines(Path.Combine(staging, ManagedInstall.OwnedFiles), owned);
            ValidateExisting(destination, previous);
            if (previous != null)
                Directory.Move(destination, backup);
            else if (Directory.Exists(destination))
                Directory.Delete(destination);
            try
            {
                Directory.Move(staging, destination);
                if (hasData)
                    Directory.Move(backedUpData, centralData);
            }
            catch
            {
                if (Directory.Exists(destination) && !Directory.Exists(staging))
                    Directory.Move(destination, staging);
                if (Directory.Exists(backup) && !Directory.Exists(destination))
                    Directory.Move(backup, destination);
                throw;
            }
            if (Directory.Exists(backup))
            {
                try
                {
                    ManagedInstall.RemovePayload(backup);
                    foreach (string relative in preserved)
                    {
                        string oldFile = Path.Combine(backup, relative);
                        string newFile = Path.Combine(destination, relative);
                        if (File.Exists(oldFile) && File.Exists(newFile) && SameContent(oldFile, newFile))
                            File.Delete(oldFile);
                    }
                    if (Directory.Exists(backup) && !GameInstaller.InstallFiles.Tree(backup, CancellationToken.None).Any(item => !item.Directory))
                        InstallFiles.ClearRetiredFolder(backup);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    _ = GameInstaller.InstallFiles.Tree(staging, CancellationToken.None).Count();
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string[] PreserveLocalFiles(string destination, string staging, string data)
    {
        var owned = new HashSet<string>(ManagedInstall.ReadOwnedFiles(destination), StringComparer.OrdinalIgnoreCase);
        owned.Add(Path.Combine(destination, ManagedInstall.OwnedFiles));
        var preserved = new List<string>();
        foreach (var item in GameInstaller.InstallFiles.Tree(destination, CancellationToken.None, exclude: data))
        {
            if (item.Directory || owned.Contains(item.Path))
                continue;
            string relative = Path.GetRelativePath(destination, item.Path);
            string target = GameInstaller.InstallFiles.Under(staging, relative);
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("A file you added conflicts with the new NeonLoop version. Move it first: " + relative);
            InstallFiles.Copy(new FileInfo(item.Path), target, null, CancellationToken.None);
            preserved.Add(relative);
        }
        return preserved.ToArray();
    }

    private static bool SameContent(string first, string second) =>
        GameInstaller.InstallFiles.HashAsync(first, CancellationToken.None).GetAwaiter().GetResult().Equals(
            GameInstaller.InstallFiles.HashAsync(second, CancellationToken.None).GetAwaiter().GetResult(), StringComparison.Ordinal);

    private sealed record PayloadHash(long size, string sha256);

    private static Dictionary<string, PayloadHash> ReadPayloadManifest(ZipArchive package, ZipArchiveEntry[] payload)
    {
        var entry = package.GetEntry(PayloadManifest);
        if (entry == null)
            return null;
        if (entry.Length > 1024 * 1024)
            throw new InvalidDataException("The installer file list is too large.");
        using var source = entry.Open();
        var checks = JsonSerializer.Deserialize<Dictionary<string, PayloadHash>>(source);
        if (checks == null || checks.Count != payload.Length || payload.Any(file =>
            !checks.TryGetValue(file.FullName, out var check) || check == null ||
            file.Length != check.size || check.sha256 == null || !GameInstaller.InstallFiles.IsHash(check.sha256)))
            throw new InvalidDataException("The installer’s files do not match its file list.");
        return checks;
    }

    private static IEnumerable<ZipArchiveEntry> PayloadEntries(ZipArchive package) =>
        package.Entries.Where(entry => entry.FullName != PayloadManifest &&
            !entry.FullName.StartsWith(SetupAssets.Prefix, StringComparison.Ordinal));
}

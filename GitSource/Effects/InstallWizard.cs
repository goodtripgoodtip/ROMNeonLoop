using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameInstaller;
using GameInstaller.WinForms;

internal sealed class InstallWizard : Form
{
    private enum SetupStage { Location, Review, Install, Play }

    private const string FindGame = "Looking for 2064: Read Only Memories in Steam…";
    private const string UseF10 = "Press F10 in the game to change the look, music, or gameplay.";
    private string effects;
    private string installFolder = ManagedInstall.Folder;
    private bool managed;
    private bool desktopShortcut;
    private bool startMenuShortcut;
    private bool shortcutsPending;
    private readonly Label title = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label description = TextLabel("");
    private readonly Label action = TextLabel("");
    private readonly Label next = TextLabel("");
    private readonly TextBox console = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 11), AccessibleName = "Setup activity", Margin = new Padding(0)
    };
    private readonly TableLayoutPanel content = Stack();
    private readonly TableLayoutPanel progress = Stack();
    private readonly FlowLayoutPanel buttons = ButtonRow();
    private readonly TableLayoutPanel consentBar = Stack();
    private readonly ProgressBar total = new() { Dock = DockStyle.Fill, AccessibleName = "Overall installation progress" };
    private readonly ProgressBar current = new() { Dock = DockStyle.Fill, AccessibleName = "Current action progress" };
    private readonly LinkLabel details = new() { Text = "Show setup details", AutoSize = true };
    private readonly Button openLog = new() { Text = "Copy details", AutoSize = true, Enabled = false, Visible = false };
    private readonly List<string> history = [];
    private readonly Control diagnostics;
    private readonly FlowLayoutPanel shortcuts = new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0) };
    private readonly TableLayoutPanel layout;
    private readonly TableLayoutPanel wizardPage;
    private readonly Panel body;
    private readonly TableLayoutPanel stages = new() { Dock = DockStyle.Top, AutoSize = true,
        ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, AccessibleRole = AccessibleRole.PageTabList };
    private readonly Panel stageBody = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly List<Button> stageButtons = [];
    private readonly List<Label> wrapped = [];
    private readonly InstallerNotice notice = InstallerNotice.HobbyRelease;
    private readonly ZipArchive setupArchive;
    private ZipArchive resumeArchive;
    private InstallSession session;
    private CancellationTokenSource operation;
    private Action saveCustom = () => { };
    private string[] customMovies = [];
    private bool custom;
    private bool localOnly;
    private bool consent;
    private bool easyHardware;
    private string[] easyPacks;
    private bool verbose;
    private bool closeAfterOperation;
    private bool resizeQueued;
    private TextBox? longTextPanel;
    private float longTextShare;
    private string activeStep;
    private string lastLine;
    private StreamWriter journal;
    private string selectedFolder;
    private SetupPackage locationPage;
    private Mutex packageLaunchLock;
    private SetupStage currentStage;
    internal RunningGame Game { get; private set; }
    internal bool OutcomeOk { get; private set; }

    internal InstallWizard(string effects, bool firstRun = false, bool desktopShortcut = false, bool startMenuShortcut = false,
        ZipArchive package = null, bool privacyOnly = false, bool uninstallOnly = false,
        Action removalWork = null, string startupProblem = null)
    {
        this.effects = effects;
        setupArchive = package;
        this.desktopShortcut = desktopShortcut;
        this.startMenuShortcut = startMenuShortcut;
        managed = ManagedInstall.IsManaged(installFolder);
        shortcutsPending = firstRun && managed;
        Text = $"NeonLoop Setup · {SetupPackage.ModSubtitle} · {typeof(InstallWizard).Assembly.GetName().Version!.ToString(3)}";
        Icon = EffectsLauncher.InstallerIcon ?? Icon;
        Font = new Font("Segoe UI", 14);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(720, 510);
        ClientSize = new Size(1020, 650);
        StartPosition = FormStartPosition.CenterScreen;
        InstallerWindow.FitWorkArea(this, 0.54, 0.55);
        InstallerTheme.Apply(this);
        InstallerTheme.Console(console);
        InstallerTheme.Apply(details);
        title.Font = new Font(Font.FontFamily, 32, FontStyle.Bold);
        title.ForeColor = InstallerTheme.Heading(null);

        layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 4, Padding = new Padding(38, 30, 38, 0), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        var header = Stack();
        Add(header, title);
        Wrap(header, description);
        Add(header, new Panel { Dock = DockStyle.Top, Height = 1, BackColor = InstallerTheme.Divider, Margin = new Padding(0, 8, 0, 19) });
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(content, 0, 1);
        Add(progress, action);
        Add(progress, current, 22);
        Add(progress, next);
        Add(progress, total, 18);
        progress.Visible = false;
        layout.Controls.Add(progress, 0, 2);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Margin = Padding.Empty;
        diagnostics = ConsolePanel();
        layout.Controls.Add(diagnostics, 0, 3);
        body = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        body.Controls.Add(layout);
        wizardPage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        wizardPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        wizardPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wizardPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wizardPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wizardPage.Controls.Add(body, 0, 0);
        wizardPage.Controls.Add(consentBar, 0, 1);
        wizardPage.Controls.Add(buttons, 0, 2);
        stages.BackColor = InstallerTheme.StageBar;
        foreach (SetupStage step in Enum.GetValues<SetupStage>())
        {
            stages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var tab = new Button { Text = step.ToString(), Dock = DockStyle.Fill,
                AutoSize = true, AccessibleRole = AccessibleRole.PageTab, Margin = Padding.Empty };
            InstallerTheme.Secondary(tab);
            tab.FlatAppearance.BorderSize = 0;
            tab.Click += (_, _) => SelectStage(step);
            stageButtons.Add(tab);
            stages.Controls.Add(tab, (int)step, 0);
        }
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.Controls.Add(stages, 0, 0);
        frame.Controls.Add(stageBody, 0, 1);
        Controls.Add(frame);
        if (package != null)
        {
            locationPage = new SetupPackage(package, OpenPackagedInstallAsync, OpenPackagedRemoval);
            MinimumSize = new Size(700, 535);
            ClientSize = new Size(1020, 615);
            ShowStage(SetupStage.Location, locationPage);
            AcceptButton = locationPage.ContinueButton;
        }
        details.LinkClicked += (_, _) =>
        {
            ShowDetails(!verbose);
        };
        openLog.Click += (_, _) => CopyDetails();
        Resize += (_, _) => FitLabels();
        ResizeEnd += (_, _) => QueuePageFit();
        DpiChanged += (_, _) => QueuePageFit();
        InstallerWindow.ScaleText(this, new Size(1000, 540), title, console, scale =>
        {
            int Px(int value) => (int)MathF.Round(value * scale);
            layout.Padding = new Padding(Px(42), Px(32), Px(42), 0);
            buttons.Padding = new Padding(Px(42), Px(12), Px(42), Px(20));
            consentBar.Padding = new Padding(Px(42), Px(8), Px(42), 0);
            foreach (Button tab in stageButtons)
                tab.MinimumSize = new Size(0, Px(50));
            progress.RowStyles[1].Height = Px(22);
            progress.RowStyles[3].Height = Px(18);
            FitLabels();
        });
        FormClosing += (_, args) =>
        {
            if (locationPage?.Busy == true)
            {
                args.Cancel = true;
                return;
            }
            if (operation == null)
                return;
            args.Cancel = true;
            closeAfterOperation = true;
            CancelWork();
        };
        Shown += async (_, _) =>
        {
            if (removalWork != null)
            {
                await RunRemovalAsync(removalWork);
                return;
            }
            if (locationPage != null || uninstallOnly || startupProblem != null)
                return;
            await DiscoverAsync(SteamGame.SelectedFolder(effects));
            if (!IsDisposed && privacyOnly && session?.Ready == true)
                PrivacyOptions(applyNow: true);
            else if (!IsDisposed && firstRun && session?.Ready == false)
                await ReviewAsync();
        };
        Disposed += (_, _) =>
        {
            resumeArchive?.Dispose();
            if (packageLaunchLock == null)
                return;
            packageLaunchLock.ReleaseMutex();
            packageLaunchLock.Dispose();
        };
        if (startupProblem != null)
            ShowOutcome("Setup paused", startupProblem);
        else if (uninstallOnly)
            ConfirmRemoval(installFolder, Close);
        else if (locationPage == null)
            Page("Setting up NeonLoop", FindGame, stage: SetupStage.Review);
    }

    private async Task RunRemovalAsync(Action work)
    {
        stages.Visible = false;
        Page("Uninstalling NeonLoop", "Removing the items you confirmed.", stage: SetupStage.Install);
        progress.Visible = true;
        current.Style = ProgressBarStyle.Marquee;
        total.Visible = next.Visible = false;
        FormClosingEventHandler wait = (_, args) => args.Cancel = true;
        FormClosing += wait;
        try
        {
            await Task.Run(work);
            OutcomeOk = true;
            ShowOutcome("Uninstall complete", "NeonLoop and its downloaded files were removed.");
        }
        catch (Win32Exception error) when (ManagedInstall.WasDeclined(error))
        {
            ShowOutcome("Uninstall paused", "Windows permission was declined. Open Uninstall again to finish any remaining steps.");
        }
        catch (Exception error)
        {
            ShowOutcome("Uninstall paused", error.GetBaseException().Message + " Open Uninstall again to retry.");
        }
        finally { FormClosing -= wait; }
    }

    private void ShowOutcome(string heading, string message)
    {
        stages.Visible = false;
        Page(heading, message, stage: SetupStage.Play);
        Primary("Close", Close);
    }

    private async Task OpenPackagedInstallAsync(string destination, bool desktop, bool startMenu, string steamFolder)
    {
        var launchLock = new Mutex(false, EffectsLauncher.LaunchMutex);
        bool ownsLock = false;
        try
        {
            ownsLock = EffectsLauncher.TryAcquireLaunchLock(launchLock);
            if (!ownsLock)
                throw new IOException("Close the other NeonLoop window and try again.");
            ManagedInstall.Register(destination);
            effects = await Task.Run(() => ManagedInstall.PrepareEffects(destination, refresh: true));
        }
        catch
        {
            if (ownsLock)
                launchLock.ReleaseMutex();
            launchLock.Dispose();
            throw;
        }
        packageLaunchLock = launchLock;
        installFolder = destination;
        EffectsLauncher.SelectInstallation(destination, effects);
        managed = true;
        desktopShortcut = desktop;
        startMenuShortcut = startMenu;
        shortcutsPending = true;
        Rectangle area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(area.Width - 32, 720 * DeviceDpi / 96),
            Math.Min(area.Height - 32, 510 * DeviceDpi / 96));
        locationPage.Dispose();
        locationPage = null;
        await DiscoverAsync(steamFolder);
        if (!IsDisposed && session != null)
            await ReviewAsync();
    }

    private void ShowStage(SetupStage stage, Control page)
    {
        currentStage = stage;
        if (page.Parent != stageBody)
            stageBody.Controls.Add(page);
        for (int i = 0; i < stageButtons.Count; i++)
        {
            bool selected = i == (int)stage;
            var tab = stageButtons[i];
            tab.BackColor = selected ? InstallerTheme.Divider : InstallerTheme.StageBar;
            tab.ForeColor = selected ? ForeColor : InstallerTheme.MutedText;
            tab.AccessibleDescription = selected ? "Current setup stage" : "Setup stage";
            tab.TabStop = selected || (i == (int)SetupStage.Review && session != null) ||
                (i == (int)SetupStage.Play && session?.Ready == true);
        }
    }

    private async void SelectStage(SetupStage stage)
    {
        if (operation != null || locationPage != null || session == null)
            return;
        if (stage == SetupStage.Review && currentStage != SetupStage.Review)
            await ReviewAsync();
        else if (stage == SetupStage.Play && session.Ready)
            Home();
    }

    private Control ConsolePanel()
    {
        var pane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        details.Anchor = AnchorStyles.Left;
        header.Controls.Add(details, 0, 0);
        header.Controls.Add(openLog, 1, 0);
        pane.Controls.Add(header, 0, 0);
        pane.Controls.Add(console, 0, 1);
        openLog.Enabled = true;
        return pane;
    }

    private void Page(string heading, string text, bool help = false, SetupStage? stage = null)
    {
        if (stage is { } step)
            ShowStage(step, wizardPage);
        AcceptButton = null;
        CancelButton = null;
        longTextPanel = null;
        wrapped.RemoveAll(label => label != description);
        while (content.Controls.Count > 0)
        {
            Control item = content.Controls[0];
            if (item == shortcuts)
            {
                while (shortcuts.Controls.Count > 0)
                    shortcuts.Controls[0].Dispose();
                content.Controls.Remove(shortcuts);
            }
            else
                item.Dispose();
        }
        content.RowStyles.Clear();
        content.RowCount = 0;
        while (buttons.Controls.Count > 0)
            buttons.Controls[0].Dispose();
        while (consentBar.Controls.Count > 0)
            consentBar.Controls[0].Dispose();
        consentBar.RowStyles.Clear();
        consentBar.RowCount = 0;
        consentBar.Visible = false;
        progress.Visible = false;
        diagnostics.Visible = help;
        ShowDetails(false);
        saveCustom = () => { };
        title.Text = heading;
        description.Text = text;
        FitLabels();
        QueuePageFit();
    }

    private void Home(string status = null, bool showSetup = false)
    {
        if (!showSetup && session?.Ready == true && session.Request?.OptionsPending == true &&
            (!session.HasIncompleteSelection || session.Request.AllowLimited))
        {
            GameplayOptions();
            return;
        }
        if (!showSetup && session?.HasIncompleteSelection == true && !session.Request.AllowLimited)
        {
            Finish(new IOException("Some selected features still need setup."));
            return;
        }
        bool play = session?.Ready == true && !showSetup;
        Page(play ? "Ready to play" : "Install NeonLoop", status ?? (session == null
            ? "Install 2064: Read Only Memories in Steam first. If you already have it, choose Find game."
            : play ? UseF10 : "Your Steam game is ready."), help: custom && !play,
            stage: play ? SetupStage.Play : SetupStage.Review);
        if (play)
        {
            string shortcutMessage = FinishShortcuts();
            if (shortcutMessage != null)
                Add(content, TextLabel(shortcutMessage));
            ShowLocation();
            Primary("Play Now", async () => await PlayAsync());
            AddShortcuts(Link("Get music packs", () => MusicOptions()), Link("Setup options", SetupOptions));
            if (session.HasIncompleteSelection)
                Add(content, Link("Resume setup", () => { custom = false; EasySelection(); }));
        }
        else if (session != null)
        {
            var mode = Link(custom ? "Use recommended settings" : "Setup options", () =>
            {
                saveCustom();
                custom = !custom;
                Home(showSetup: true);
            });
            if (custom)
                Add(content, CustomOptions());
            if (!showSetup || custom)
                AddShortcuts(mode);
            else
            {
                AddShortcuts(Link("Customize installation", () => { custom = true; Home(showSetup: true); }),
                    Link("Get music packs", () => MusicOptions()));
            }
            Primary("Install", async () => await ReviewAsync());
        }
        else
        {
            AddShortcuts(Link("How to find your game in Steam", () =>
            {
                Page("Find your Steam game", "In your Steam Library, right-click 2064: Read Only Memories and choose Manage → Browse local files.");
                AddButton("Back", () => Home());
                Primary("Find game…", async () => await LocateAsync());
            }));
            Primary("Find game…", async () => await LocateAsync());
        }
        if (!play && managed && (showSetup || session == null))
            Add(content, Link("Remove NeonLoop", ConfirmRemoval));
        CancelButton = AddButton("Close", Close);
    }

    private string FinishShortcuts()
    {
        if (!shortcutsPending)
            return null;
        shortcutsPending = false;
        try
        {
            ManagedInstall.SaveShortcuts(installFolder, desktopShortcut, startMenuShortcut);
            return null;
        }
        catch (Exception error)
        {
            LogError(error);
            return "Windows couldn’t update your shortcuts. You can open NeonLoop from its install location.";
        }
    }

    private void ShowLocation()
    {
        var location = TextLabel("Installed in " + installFolder);
        Wrap(content, location);
        Add(content, Link("Open install location", () =>
        {
            try { ManagedInstall.OpenFolder(installFolder); }
            catch (Exception error)
            {
                LogError(error);
                Wrap(content, TextLabel("Windows couldn’t open the install location."));
                QueuePageFit();
            }
        }));
    }

    private void SetupOptions()
    {
        Page("Setup options", "Manage this NeonLoop installation.");
        ShowLocation();
        if (managed)
            AddShortcuts(Link("Customize installation", () => { custom = true; Home(showSetup: true); }),
                Link("Remove NeonLoop", ConfirmRemoval));
        else
            AddShortcuts(Link("Customize installation", () => { custom = true; Home(showSetup: true); }));
        if (session?.CanPlay == true)
            Add(content, Link("Gameplay options", GameplayOptions));
        AddButton("Back", () => Home());
    }

    private void OpenPackagedRemoval(string folder)
    {
        locationPage.Hide();
        wizardPage.Show();
        ConfirmRemoval(folder, () =>
        {
            wizardPage.Hide();
            locationPage.Show();
            ShowStage(SetupStage.Location, locationPage);
            AcceptButton = locationPage.ContinueButton;
        });
    }

    private void ConfirmRemoval() => ConfirmRemoval(installFolder, () =>
    {
        if (session?.Ready == true) SetupOptions();
        else Home();
    });

    private void ConfirmRemoval(string folder, Action cancel)
    {
        stages.Visible = false;
        var steps = new (string Title, string Details)[]
        {
            ("Shortcuts and firewall rules", "Remove NeonLoop’s shortcuts and any firewall rules it added for its private game copy."),
            ("Downloads and settings", "Remove the private game copy, cached music and cutscenes, helper packages, and NeonLoop settings. Your Steam game and saves stay."),
            ("Installed files", "Remove the files installed by NeonLoop and its Windows Apps entry. Other files in the install folder stay.")
        };
        void ShowStep(int index)
        {
            var step = steps[index];
            Page($"Uninstall NeonLoop · {index + 1} of {steps.Length}", step.Details, stage: SetupStage.Location);
            Wrap(content, TextLabel(step.Title));
            Wrap(content, TextLabel(index == 1 ? ManagedInstall.RuntimeFolder(folder) : folder));
            var cancelButton = AddButton("Cancel", () =>
            {
                stages.Visible = true;
                cancel();
            });
            CancelButton = cancelButton;
            cancelButton.DialogResult = DialogResult.None;
            if (index > 0)
                AddButton("Back", () => ShowStep(index - 1));
            if (index + 1 < steps.Length)
            {
                Wrap(content, TextLabel("Removal starts after you confirm all three steps."));
                Primary("Confirm and continue", () => ShowStep(index + 1));
                return;
            }
            Primary("Confirm uninstall", () =>
            {
                try
                {
                    ManagedInstall.BeginRemoval(folder, true);
                    Close();
                }
                catch (Exception error)
                {
                    Wrap(content, TextLabel(error.GetBaseException().Message));
                    QueuePageFit();
                }
            });
        }
        ShowStep(0);
    }

    private void AddShortcuts(params LinkLabel[] links)
    {
        shortcuts.Controls.AddRange(links);
        Add(content, shortcuts);
    }

    private Control CustomOptions()
    {
        var panel = Stack();
        var cueNames = session.InstalledLibraries;
        InstallerCheckBox[] scenes = cueNames.Select(key => new InstallerCheckBox
        {
            Text = MusicPack.Name(key), Checked = customMovies.Contains(key)
        }).ToArray();
        Add(panel, TextLabel("Replacement cutscenes"));
        if (scenes.Length != 0)
            foreach (InstallerCheckBox choice in scenes)
                Add(panel, choice);
        else
            Add(panel, Link("Get music packs for remixed cutscenes", () => { saveCustom(); MusicOptions(); }));

        var offline = new InstallerCheckBox { Text = "Use downloads I already have", Checked = localOnly };
        var gpu = new InstallerCheckBox { Text = "Check for NVIDIA acceleration", Checked = consent };
        Add(panel, offline);
        Add(panel, gpu);
        Add(panel, Link("Game network privacy", () => PrivacyOptions()));
        Add(panel, Link("Find Steam game again…", async () =>
        {
            saveCustom();
            await LocateAsync();
        }));
        saveCustom = () =>
        {
            localOnly = offline.Checked;
            consent = gpu.Checked;
            customMovies = cueNames.Where((_, index) => scenes[index].Checked).ToArray();
        };
        return panel;
    }

    private void PrivacyOptions(bool applyNow = false)
    {
        saveCustom();
        Page("Game network privacy", "Choose how the NeonLoop game copy can connect.", stage: SetupStage.Review);
        Add(content, ReadOnlyPanel(NetworkPrivacy.Details, "Base game network activity", compact: true));
        var allow = new InstallerRadioButton { Text = "Allow the game’s normal network access", Checked = session.Network != "block" };
        var block = new InstallerRadioButton { Text = "Block outgoing traffic from the NeonLoop copy", Checked = session.Network == "block" };
        Add(content, allow);
        Add(content, block);
        Wrap(content, TextLabel("If you choose Block, " + NetworkPrivacy.BlockSummary));
        AddButton("Back", () => Home(showSetup: !applyNow));
        Primary("Save choice", async () =>
        {
            session.Network = block.Checked ? "block" : "allow";
            if (applyNow)
            {
                try
                {
                    _ = StartOperation();
                    await Task.Run(() =>
                    {
                        EffectsLauncher.CheckNoGame();
                        NetworkPrivacy.Ensure(effects, session.Steam.BuildId, session.Network);
                        InstallFiles.WriteText(Path.Combine(effects, "NetworkChoice.txt"), session.Network);
                    });
                }
                catch (Exception error)
                {
                    Finish(error);
                    return;
                }
                finally { EndOperation(); }
            }
            if (!IsDisposed)
                Home(showSetup: !applyNow);
        });
    }

    private void MusicOptions(string status = null)
    {
        Page("Music packs", status ?? "Add a soundtrack here. Choose it under Music in the F10 menu when you play.");
        if (session.CanPlay)
        {
            ShowLocation();
            Primary("Play Now", async () => await PlayAsync());
        }
        foreach (string key in MusicPack.Libraries)
        {
            if (session.Pack.HasLibrary(key))
            {
                Add(content, TextLabel(MusicPack.Name(key) + " · Installed"));
                continue;
            }
            InstallAsset asset = session.MusicAssets.FirstOrDefault(item => item.key == key);
            if (asset != null)
                Add(content, Link("Download " + asset.label, async () => await GetMusicAsync(asset)));
        }
        if (session.MusicAssets.Length != 0)
            Add(content, Link("Add a downloaded pack…", async () => await PickMusicAsync()));
        AddButton("Back", () => Home());
    }

    private async Task PickMusicAsync()
    {
        Page("Choose a music pack", "Select the ZIP file you downloaded.");
        Add(content, new InstallerPathPicker(session.LocalFiles.LastFolder, false,
            async path => await RunMusicStepAsync(token => Task.Run(() => session.InstallPack(path, Report, token), token)),
            () => MusicOptions(), "*.zip"));
        await Task.CompletedTask;
    }

    private async Task GetMusicAsync(InstallAsset asset)
    {
        try
        {
            using var opened = Process.Start(new ProcessStartInfo(asset.url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            LogError(error);
            MusicOptions("The download couldn’t open. You can choose a pack you already downloaded.");
            return;
        }

        await RunMusicStepAsync(async token =>
        {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloads))
                throw new IOException("After the download finishes, choose Add a downloaded pack.");
            string stem = Path.GetFileNameWithoutExtension(asset.filename);
            var rejected = new HashSet<(string Path, DateTime Written)>();
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                foreach (string path in Directory.EnumerateFiles(downloads, stem + "*.zip"))
                {
                    var info = new FileInfo(path);
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name != stem && !(name.StartsWith(stem + " (", StringComparison.Ordinal) &&
                        name.EndsWith(')') && int.TryParse(name.AsSpan(stem.Length + 2, name.Length - stem.Length - 3), out _)))
                        continue;
                    if (!info.Exists || info.Length != asset.bytes || rejected.Contains((path, info.LastWriteTimeUtc)))
                        continue;
                    try
                    {
                        await Task.Run(() => session.InstallPack(path, Report, token), token);
                        return;
                    }
                    catch (InvalidDataException error)
                    {
                        rejected.Add((path, info.LastWriteTimeUtc));
                        WriteActivity(error.Message);
                        action.Text = "This pack couldn’t be added. Try downloading it again.";
                    }
                    catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                    {
                    }
                }
                await Task.Delay(1000, token);
            }
            throw new IOException("Setup couldn’t find your download. Choose Add a downloaded pack when it’s ready.");
        }, "Finish the download in your browser. NeonLoop will add it when it’s ready.");
    }

    private async Task RunMusicStepAsync(Func<CancellationToken, Task> action, string message = "Checking your music pack…")
    {
        Page("Adding a music pack", message);
        ShowProgress("Waiting for the music pack…");
        string status = "Music pack installed. Pick it in F10 → Music when you play.";
        bool installed = false;
        try
        {
            await action(StartOperation());
            installed = true;
        }
        catch (Exception error)
        {
            LogError(error);
            status = error is OperationCanceledException ? "Choose Add a downloaded pack when the download finishes." :
                PlayerMessage(error, "This music pack couldn’t be added. Try downloading it again.");
        }
        finally { EndOperation(); }
        if (!IsDisposed)
        {
            if (installed && session.Ready)
                Home(status);
            else
                MusicOptions(status);
        }
    }

    private Task LocateAsync()
    {
        Page("Find your Steam game", "Choose the folder that contains ROM.exe.", stage: SetupStage.Review);
        Add(content, new InstallerPathPicker(session?.Steam.Installed ?? selectedFolder, true,
            async path => await DiscoverAsync(path), () => Home()));
        return Task.CompletedTask;
    }

    private async Task DiscoverAsync(string folder)
    {
        var previous = session;
        selectedFolder = folder;
        Page("Setting up NeonLoop", FindGame, stage: SetupStage.Review);
        string status = null;
        AddButton("Stop", CancelWork);
        try
        {
            CancellationToken token = StartOperation();
            session = await Task.Run(() => Discover(folder, token), token);
            session.Package = setupArchive ?? resumeArchive;
            if (previous != null)
            {
                session.Network = previous.Network;
            }
            WriteActivity("Supported source: " + session.Steam.Installed);
        }
        catch (Exception error)
        {
            session = null;
            LogError(error);
            status = PlayerMessage(error, "NeonLoop couldn’t find your game. Open Steam or choose Find game.");
        }
        finally { EndOperation(); }
        if (!IsDisposed)
            Home(status);
    }

    private InstallSession Discover(string folder, CancellationToken token)
    {
        try { return new InstallSession(effects, folder, Report, token); }
        catch (Exception error) when (!string.IsNullOrEmpty(folder) && session == null &&
            folder == SteamGame.SelectedFolder(effects) && error is not OperationCanceledException)
        {
            return new InstallSession(effects, null, Report, token);
        }
    }

    private Task ReviewAsync(bool selectionConfirmed = false)
    {
        saveCustom();
        if (!custom && !selectionConfirmed)
        {
            EasySelection();
            return Task.CompletedTask;
        }
        session.Easy = !custom;
        session.LocalOnly = localOnly;
        session.AllowHardwareCheck = custom ? consent : easyHardware;
        if (!custom)
        {
            bool offline = session.IsOfflinePackage || session.Request?.Offline == true;
            bool fresh = session.Request?.FreshDefaults ?? NetworkPrivacy.ReadChoice(effects) == null;
            session.Request = new EasyInstallRequest(easyPacks, easyPacks, Offline: offline, FreshDefaults: fresh);
        }
        session.Movies = custom ? customMovies.Where(session.Pack.HasLibrary).ToArray() : session.Request.Movies;
        string review;
        try { review = session.Review(CancellationToken.None); }
        catch (Exception error) { Finish(error); return Task.CompletedTask; }
        string summary = session.Movies.Length == 0 ? "NeonLoop sets up its own copy of the game." :
            "NeonLoop will also prepare the cutscene remixes you picked.";
        session.ApprovedDownloads = session.DownloadsNeeded(session.ReviewedAssets());
        long bytes = session.ApprovedDownloads.Sum(asset => asset.bytes);
        string network = session.IsOfflinePackage || localOnly || session.Request?.Offline == true
            ? "Setup uses the files in the installer and any verified local files."
            : bytes == 0 ? "No additional downloads are expected." :
                "Install approves up to " + InstallFiles.Size(bytes) + " in downloads for the selected features.";
        ShowInstallReview(summary, network, review);
        return Task.CompletedTask;
    }

    private void EasySelection()
    {
        Page("Easy Install", "Choose the soundtracks to install and prepare for cutscenes.", stage: SetupStage.Review);
        easyPacks ??= session.Request?.Libraries ?? ["dark-synthwave"];
        var selections = MusicPack.Libraries.Select(key =>
            new InstallerCheckBox { Text = MusicPack.Name(key), Checked = easyPacks.Contains(key) }).ToArray();
        foreach (var item in selections)
            Add(content, item);
        var gpu = new InstallerCheckBox { Text = "Check if NVIDIA can speed up cutscene preparation", Checked = easyHardware };
        Add(content, gpu);
        Wrap(content, TextLabel("The hardware check runs only if selected. Cutscenes can take a long time on the CPU. Setup keeps completed work if you stop."));
        if (managed && !Directory.Exists(session.GameFolder))
            Add(content, Link("Change drive for the private game and downloads", SelectRuntimeDrive));
        if (session.Request?.Offline == true && session.Package == null)
            Add(content, Link("Use the offline setup file again", SelectOfflineSource));
        AddButton("Back", () => Home(showSetup: true));
        Primary("Continue", async () =>
        {
            easyPacks = MusicPack.Libraries.Where((_, index) => selections[index].Checked).ToArray();
            easyHardware = gpu.Checked && easyPacks.Length != 0;
            await ReviewAsync(selectionConfirmed: true);
        });
    }

    private void SelectRuntimeDrive()
    {
        Page("Private game and downloads", "Choose a writable folder on the drive NeonLoop should use.", stage: SetupStage.Review);
        Add(content, new InstallerPathPicker(Path.GetDirectoryName(effects), true, async path =>
        {
            if (operation != null)
                return;
            try
            {
                string previous = SteamGame.SelectedFolder(effects) ?? session.Steam.Installed;
                string runtime = await Task.Run(() => ManagedInstall.SelectRuntimeFolder(installFolder, path, session.Steam.Installed));
                string target = await Task.Run(() => ManagedInstall.PrepareEffects(installFolder, runtimeOverride: runtime));
                ManagedInstall.RememberRuntimeFolder(installFolder, runtime);
                effects = target;
                EffectsLauncher.SelectInstallation(installFolder, effects);
                await DiscoverAsync(previous);
                if (session != null)
                    EasySelection();
            }
            catch (Exception error)
            {
                Wrap(content, TextLabel(error.GetBaseException().Message));
                QueuePageFit();
            }
        }, EasySelection));
    }

    private void SelectOfflineSource()
    {
        Page("Open the offline setup file", "Choose NeonLoop-Setup-Offline.exe to resume with its bundled files.",
            stage: SetupStage.Review);
        Add(content, new InstallerPathPicker(session.LocalFiles.LastFolder, false, path =>
        {
            if (!SetupPackage.TryOpen(path, out var selected) ||
                !session.ReviewedAssets().Any(asset => SetupAssets.Embedded(selected, asset)))
            {
                selected?.Dispose();
                throw new IOException("Choose the offline setup for this version of NeonLoop.");
            }
            resumeArchive?.Dispose();
            resumeArchive = selected;
            session.Package = selected;
            EasySelection();
        }, EasySelection, "*.exe"));
    }

    private void GameplayOptions()
    {
        Page("Gameplay options", UseF10, stage: SetupStage.Play);
        uint flags = GameplayInstallSettings.Read(effects, session.Request?.FreshDefaults == true);
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = Stack();
        var right = Stack();
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);
        foreach (var group in GameplayInstallSettings.Options.GroupBy(option => option.Group))
        {
            var column = group.Key is "Exploration" or "Puzzle assists" or "PC startup" ? right : left;
            Add(column, TextLabel(group.Key));
            foreach (var option in group)
            {
                var box = new InstallerCheckBox { Text = option.Label, Checked = GameplayInstallSettings.On(flags, option.Bit) };
                box.CheckedChanged += (_, _) => flags = GameplayInstallSettings.Set(flags, option.Bit, box.Checked);
                Add(column, box);
            }
        }
        Add(content, columns);
        Primary("Save and continue", () =>
        {
            try
            {
                GameplayInstallSettings.Save(effects, flags);
                if (session.Request != null)
                {
                    session.Request = session.Request with { OptionsPending = false, FreshDefaults = false };
                    session.Request.Save(effects);
                }
                Home();
            }
            catch (Exception error)
            {
                Wrap(content, TextLabel(error.GetBaseException().Message));
                QueuePageFit();
            }
        });
    }

    private void ShowInstallReview(string summary, string network, string review)
    {
        bool agreed = false;
        void RenderReview()
        {
            Page("Ready to install", summary, stage: SetupStage.Review);
            Wrap(content, TextLabel("Install location · " + installFolder));
            if (session.Easy)
                Wrap(content, TextLabel("Easy Install skips the firewall check."));
            else if (session.Network == "block")
                Wrap(content, TextLabel(NetworkPrivacy.BlockSummary));
            else
                Wrap(content, TextLabel("Normal network access. Setup removes any NeonLoop block rules for this game copy."));
            if (!string.IsNullOrEmpty(network))
                Wrap(content, TextLabel(network));
            Add(content, Link("Show setup details", () => ShowReviewText("Setup details", review, RenderReview)));
            var acceptTerms = new InstallerCheckBox { Text = "I agree to the terms", Checked = agreed };
            Add(consentBar, Link("Read the terms", () => ShowReviewText(notice.Title, notice.Text, RenderReview)));
            Add(consentBar, acceptTerms);
            consentBar.Visible = true;
            AddButton("Back", () => Home(showSetup: custom));
            var accept = AddButton("Install", async () =>
            {
                if (agreed && operation == null)
                    await InstallAsync();
            });
            InstallerTheme.Primary(accept);
            accept.Enabled = agreed;
            AcceptButton = agreed ? accept : null;
            acceptTerms.CheckedChanged += (_, _) =>
            {
                agreed = accept.Enabled = acceptTerms.Checked;
                AcceptButton = agreed ? accept : null;
            };
        }
        RenderReview();
    }

    private void ShowReviewText(string heading, string text, Action back)
    {
        Page(heading, "", stage: SetupStage.Review);
        Add(content, ReadOnlyPanel(text, heading));
        AddButton("Back to review", back);
    }

    private TextBox ReadOnlyPanel(string text, string name, bool compact = false)
    {
        var panel = new TextBox
        {
            Text = text.ReplaceLineEndings(Environment.NewLine), Dock = DockStyle.Top, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            AccessibleName = name, Margin = new Padding(0, 4, 0, 12)
        };
        InstallerTheme.Apply(panel);
        longTextPanel = panel;
        longTextShare = compact ? 0.40f : 0.58f;
        FitTextPanel();
        return panel;
    }

    private async Task InstallAsync()
    {
        Page("Installing NeonLoop", "This may take a few minutes.", stage: SetupStage.Install);
        ShowProgress("Preparing installation…");
        Exception failure = null;
        try
        {
            CancellationToken token = StartOperation();
            await Task.Run(() =>
            {
                string path = Path.Combine(effects, "Install.log");
                using var writer = new StreamWriter(path, true) { AutoFlush = true };
                journal = writer;
                try
                {
                    RecordTerms();
                    session.Backend = session.NeedsMovieWork ? InstallHardware.Choose(session.AllowHardwareCheck, Report, token) : InstallHardware.Cpu;
                    session.Run(Step, Report, question => AskAsset(question, token),
                        (heading, text) => AskApproval(heading, text, token), token);
                }
                finally { journal = null; }
            }, token);
        }
        catch (Exception error) { failure = error; }
        finally { EndOperation(); }
        if (!IsDisposed)
            Finish(failure);
    }

    private void RecordTerms()
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notice.Text)));
        string folder = Path.Combine(effects, "Agreements");
        Directory.CreateDirectory(folder);
        InstallFiles.WriteText(Path.Combine(folder, hash + ".json"), JsonSerializer.Serialize(new
        {
            NoticeVersion = notice.Version,
            NoticeSha256 = hash,
            SteamBuild = session.Steam.BuildId,
            AcceptedUtc = DateTimeOffset.UtcNow
        }));
        Report("Accepted " + notice.Title + " (" + notice.Version + ") " + hash, -1, -1);
    }

    private async Task PlayAsync()
    {
        Page("Opening NeonLoop", "Keep Steam open while the game starts.", stage: SetupStage.Play);
        ShowProgress("Starting the game…");
        Exception failure = null;
        try
        {
            CancellationToken token = StartOperation();
            Game = await Task.Run(() => EffectsLauncher.StartGame(session, Report, token), token);
        }
        catch (Exception error) { failure = error; }
        finally { EndOperation(); }
        if (IsDisposed)
            return;
        if (Game != null)
            Close();
        else
            Finish(failure);
    }

    private void Finish(Exception failure)
    {
        if (failure != null)
            LogError(failure);
        if (failure == null)
        {
            Home();
            return;
        }
        Page("Setup paused", PlayerMessage(failure, "NeonLoop couldn’t finish. Try again or open setup details."), help: true);
        if (session != null)
        {
            Primary("Retry", async () => await ReviewAsync(selectionConfirmed: !custom && easyPacks != null));
            if (session.Ready && session.HasIncompleteSelection)
            {
                string[] missing = session.Request.Libraries.Where(key => !session.Pack.HasLibrary(key))
                    .Select(key => MusicPack.Name(key) + " soundtrack").Concat(session.Request.Movies
                        .Where(key => !session.MovieReady(key)).Select(key => MusicPack.Name(key) + " cutscenes")).ToArray();
                Wrap(content, TextLabel("Still needed · " + string.Join(", ", missing)));
                AddButton("Play with completed features", () =>
                {
                    session.Request = session.Request with { AllowLimited = true };
                    session.Request.Save(effects);
                    session.SelectSoundtrack(preserve: true);
                    Home();
                });
            }
            else if (session.CanPlay)
                AddButton("Play Now", async () => await PlayAsync());
        }
        CancelButton = AddButton("Close", Close);
    }

    private void ShowProgress(string message)
    {
        activeStep = null;
        lastLine = null;
        total.Value = current.Value = 0;
        current.Style = ProgressBarStyle.Marquee;
        next.Text = "";
        total.Visible = next.Visible = false;
        action.Text = message;
        progress.Visible = true;
        diagnostics.Visible = true;
        ShowDetails(false);
        AddButton("Stop setup", CancelWork);
    }

    private CancellationToken StartOperation()
    {
        operation = new CancellationTokenSource();
        return operation.Token;
    }

    private void EndOperation()
    {
        operation?.Dispose();
        operation = null;
        progress.Visible = false;
        openLog.Enabled = File.Exists(Path.Combine(effects, "Install.log"));
        if (closeAfterOperation)
            Close();
    }

    private void CancelWork()
    {
        if (operation == null || operation.IsCancellationRequested)
            return;
        action.Text = "Stopping setup…";
        _ = operation.CancelAsync();
    }

    private void Step(string name, string upcoming, int done, int count)
    {
        Report(name, 0, 0);
        Invoke((Action)(() =>
        {
            activeStep = name;
            total.Maximum = count;
            total.Value = done;
            action.Text = name;
            next.Text = $"{done} of {count} steps complete · Next: {upcoming}";
            total.Visible = next.Visible = true;
            QueuePageFit();
        }));
    }

    private void Report(string text, long done, long count)
    {
        string clean = InstallerLog.Clean(text);
        bool changed = lastLine != clean;
        if (changed)
        {
            lastLine = clean;
            journal?.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + clean);
        }
        Invoke((Action)(() =>
        {
            if (changed)
                WriteActivity(clean);
            if (!progress.Visible || count < 0)
                return;
            current.Style = count == 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
            if (count > 0)
                current.Value = Math.Clamp((int)(100.0 * done / count), 0, 100);
            action.Text = (verbose ? clean : activeStep ?? BriefProgress(clean) ?? "Getting things ready…")
                + (count > 0 ? $" · {current.Value}%" : "");
        }));
    }

    private static string BriefProgress(string text)
    {
        if (text.StartsWith("Starting Steam", StringComparison.Ordinal))
            return "Opening Steam…";
        if (text.StartsWith("Starting the private game", StringComparison.Ordinal))
            return "Starting the game…";
        if (text.StartsWith("Downloading ", StringComparison.Ordinal))
            return "Downloading…";
        if (text.StartsWith("Checking the music pack", StringComparison.Ordinal) || text.StartsWith("Extracting ", StringComparison.Ordinal))
            return "Adding your music pack…";
        return null;
    }

    private T AskDuringSetup<T>(Action<Action<T>> show, CancellationToken token)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke((Action)(() => show(value => answer.TrySetResult(value))));
        T result = answer.Task.WaitAsync(token).GetAwaiter().GetResult();
        Invoke((Action)(() =>
        {
            Page("Installing NeonLoop", "This may take a few minutes.", stage: SetupStage.Install);
            ShowProgress("Preparing installation…");
        }));
        return result;
    }

    private DownloadDecision AskAsset(DownloadQuestion question, CancellationToken token) =>
        AskDuringSetup<DownloadDecision>(complete => ShowDownloadPrompt(question, session.LocalFiles.LastFolder, complete), token);

    private bool AskApproval(string heading, string text, CancellationToken token) =>
        AskDuringSetup<bool>(complete =>
        {
            Page(heading, text, stage: SetupStage.Install);
            AddButton("Use CPU", () => complete(false));
            Primary("Use NVIDIA", () => complete(true));
        }, token);

    private void ShowDownloadPrompt(DownloadQuestion question, string folder, Action<DownloadDecision> complete)
    {
        Page("Setup file needed", "", stage: SetupStage.Install);
        var prompt = new DownloadPrompt(question, folder, complete);
        Add(content, prompt);
        prompt.SizeChanged += (_, _) => QueuePageFit();
    }

    private void LogError(Exception error)
    {
        if (error is OperationCanceledException)
            WriteActivity("Setup was cancelled.");
        else
        {
            string message = error.GetBaseException().Message;
            WriteActivity(message);
        }
    }

    private static string PlayerMessage(Exception error, string fallback)
    {
        error = error.GetBaseException();
        if (error is OperationCanceledException)
            return "Setup was stopped. You can try again whenever you’re ready.";
        if (error is UnauthorizedAccessException)
            return "Windows wouldn’t let setup save changes here. Try a different install location.";
        if (error.Message.Contains("signing in", StringComparison.OrdinalIgnoreCase))
            return "Sign in to Steam, then choose Play Now again.";
        if (error.Message.Contains("Close Read Only Memories", StringComparison.OrdinalIgnoreCase))
            return "Close 2064: Read Only Memories, then try again.";
        if (error.Message.StartsWith("The game closed during startup", StringComparison.Ordinal))
            return "The game closed while starting. Try Play Now again or open setup details for the error.";
        if (error.Message.Contains("different Steam build", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("match the supported build", StringComparison.OrdinalIgnoreCase))
            return "This copy of the game doesn’t work with this version of NeonLoop.";
        if (error.Message.Contains("not enough", StringComparison.OrdinalIgnoreCase) && error.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
            return "Free up some disk space or choose a different install location.";
        return fallback;
    }

    private void WriteActivity(string full)
    {
        string time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ";
        history.Add(time + full);
        if (history.Count > 500)
        {
            history.RemoveRange(0, 100);
            RenderLog();
            return;
        }
        if (verbose)
            console.AppendText(time + full + Environment.NewLine);
    }

    private void RenderLog()
    {
        console.Lines = history.ToArray();
        console.SelectionStart = console.TextLength;
        console.ScrollToCaret();
    }

    private void CopyDetails()
    {
        try
        {
            string[] sensitive = [effects, installFolder, session?.Steam.Installed, session?.Steam.Steam,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), session?.LocalFiles.LastFolder];
            string details = history.Count == 0 ? description.Text : string.Join(Environment.NewLine, history.TakeLast(80));
            foreach (string path in sensitive.Where(path => !string.IsNullOrWhiteSpace(path)).OrderByDescending(path => path.Length))
                details = details.Replace(path, "[local folder]", StringComparison.OrdinalIgnoreCase);
            Clipboard.SetText(details);
        }
        catch (Exception error) { LogError(error); }
    }

    private void ShowDetails(bool show)
    {
        verbose = show;
        details.Text = show ? "Hide setup details" : "Show setup details";
        openLog.Visible = console.Visible = show;
        layout.RowStyles[3].Height = diagnostics.Visible ? (show ? 236 : 44) * DeviceDpi / 96 : 0;
        if (show)
            RenderLog();
        QueuePageFit();
    }

    private Button AddButton(string name, Action handler)
    {
        var button = new Button { Text = name, AutoSize = true, MinimumSize = new Size(112, 44), Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 1, 7, 0) };
        InstallerTheme.Secondary(button);
        button.Click += (_, _) => handler();
        buttons.Controls.Add(button);
        return button;
    }

    private void Primary(string name, Action handler)
    {
        var button = AddButton(name, handler);
        InstallerTheme.Primary(button);
        AcceptButton = button;
        ActiveControl = button;
    }

    private static LinkLabel Link(string name, Action action)
    {
        var link = new LinkLabel { Text = name, AutoSize = true, Margin = new Padding(0, 4, 18, 7) };
        InstallerTheme.Apply(link);
        link.LinkClicked += (_, _) => action();
        return link;
    }

    private void Wrap(TableLayoutPanel stack, Label label)
    {
        label.MaximumSize = WrappedLabelSize();
        wrapped.Add(label);
        Add(stack, label);
    }

    private Size WrappedLabelSize() =>
        new(Math.Max(260, ClientSize.Width - layout.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth), 0);

    private void FitLabels()
    {
        Size available = WrappedLabelSize();
        foreach (Label label in wrapped)
            label.MaximumSize = available;
        FitTextPanel();
    }

    private void FitTextPanel()
    {
        if (longTextPanel is { IsDisposed: false } panel)
            panel.Height = Math.Clamp((int)(ClientSize.Height * longTextShare), 140,
                (longTextShare < 0.5f ? 320 : 440) * DeviceDpi / 96);
    }

    private void QueuePageFit()
    {
        if (!IsHandleCreated || resizeQueued || locationPage != null)
            return;
        resizeQueued = true;
        BeginInvoke(() =>
        {
            resizeQueued = false;
            if (IsDisposed || locationPage != null || WindowState != FormWindowState.Normal)
                return;
            layout.PerformLayout();
            wizardPage.PerformLayout();
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int cap = Math.Max(1, area.Height - 32);
            int chrome = Height - ClientSize.Height + stages.PreferredSize.Height;
            int contentWidth = Math.Max(260, body.ClientSize.Width);
            int naturalBody = Math.Max(layout.Height, layout.GetPreferredSize(new Size(contentWidth, 0)).Height);
            int footer = buttons.PreferredSize.Height + (consentBar.Visible ? consentBar.PreferredSize.Height : 0);
            int minimum = Math.Min(cap, Math.Max(510 * DeviceDpi / 96, naturalBody + footer + chrome + 12));
            int height = Math.Clamp(Math.Max(Height, minimum), minimum, cap);
            MinimumSize = new Size(Math.Min(area.Width - 32, 720 * DeviceDpi / 96), minimum);
            int top = Math.Clamp(Top + (Height - height) / 2, area.Top, Math.Max(area.Top, area.Bottom - height));
            if (height != Height || top != Top)
                SetBounds(Left, top, Width, height);
            body.AutoScroll = naturalBody > body.ClientSize.Height + 2;
            stageBody.Invalidate(invalidateChildren: true);
        });
    }

    private static Label TextLabel(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 9) };

    private static TableLayoutPanel Stack() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1, Margin = new Padding(0) };

    private static FlowLayoutPanel ButtonRow() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };

    private sealed class BufferedPanel : Panel
    {
        internal BufferedPanel() => DoubleBuffered = true;
    }

    private static void Add(TableLayoutPanel layout, Control control, int height = 0)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(control, 0, row);
    }
}

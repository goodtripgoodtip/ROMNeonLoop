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
    private enum SetupStage { Location, Options, Review, Install, Tools, Separate, Render, Play }

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
    private readonly Label timingText = TextLabel("");
    private readonly System.Windows.Forms.Timer timingTimer = new() { Interval = 1000 };
    private readonly Stopwatch operationTime = new();
    private readonly CutsceneTiming.StageTiming actionTiming = new();
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
        ColumnCount = Enum.GetValues<SetupStage>().Length, RowCount = 1, Margin = Padding.Empty, AccessibleRole = AccessibleRole.PageTabList };
    private readonly Panel stageBody = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly List<Button> stageButtons = [];
    private readonly List<Label> wrapped = [];
    private readonly InstallerNotice notice = InstallerNotice.HobbyRelease;
    private readonly ZipArchive setupArchive;
    private ZipArchive resumeArchive;
    private InstallSession session;
    private CancellationTokenSource operation;
    private CancellationTokenSource cutsceneOperation;
    private CutsceneTiming cutsceneTiming;
    private string timedAction;
    private long timedCount;
    private Action saveCustom = () => { };
    private string[] customMovies = [];
    private string[] customPacks;
    private bool custom;
    private bool localOnly;
    private bool consent;
    private bool easyHardware;
    private bool? easyMovies;
    private string[] easyPacks;
    private bool verbose;
    private bool closeAfterOperation;
    private bool launchingGame;
    private bool resizeQueued;
    private TextBox? longTextPanel;
    private bool pendingPayload;
    private bool payloadExtracted;
    private string expectedVersion;
    private bool cleanupOffered;
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
        Add(progress, timingText);
        progress.Visible = false;
        layout.Controls.Add(progress, 0, 2);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Margin = Padding.Empty;
        diagnostics = ConsolePanel();
        layout.Controls.Add(diagnostics, 0, 3);
        body = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        body.Controls.Add(layout);
        body.SizeChanged += (_, _) => QueuePageFit();
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
            stages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / stages.ColumnCount));
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
        timingTimer.Tick += (_, _) =>
        {
            RefreshTiming();
            if (Game?.Process.HasExited == true && !launchingGame)
                foreach (Button button in buttons.Controls.OfType<Button>().Where(button => (string)button.Tag == "background-play"))
                {
                    button.Text = button.AccessibleName;
                    button.Enabled = true;
                }
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
            if (launchingGame)
            {
                args.Cancel = true;
                return;
            }
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
            timingTimer.Dispose();
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

    private async Task OpenPackagedInstallAsync(string destination, bool desktop, bool startMenu, SteamGame game, string installedVersion)
    {
        string target = Path.Combine(ManagedInstall.PreviewRuntime(destination), "Effects");
        string previous = installedVersion == null ? target : Path.Combine(ManagedInstall.RuntimeFolder(destination), "Effects");
        if (!Directory.Exists(previous))
            previous = target;
        var preview = await Task.Run(() => new InstallSession(previous, null, Report, CancellationToken.None, setupArchive, game, target)
        {
            Package = setupArchive
        });
        if (!string.Equals(installFolder, destination, StringComparison.OrdinalIgnoreCase))
        {
            easyPacks = null;
            easyMovies = null;
            customPacks = null;
            customMovies = [];
        }
        ReleasePackageLock();
        installFolder = destination;
        effects = previous;
        session = preview;
        expectedVersion = installedVersion;
        pendingPayload = true;
        payloadExtracted = false;
        managed = true;
        desktopShortcut = desktop;
        startMenuShortcut = startMenu;
        shortcutsPending = true;
        Rectangle area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(area.Width - 32, 720 * DeviceDpi / 96),
            Math.Min(area.Height - 32, 510 * DeviceDpi / 96));
        locationPage.Dispose();
        locationPage = null;
        if (!IsDisposed)
            await ReviewAsync();
    }

    private void ReleasePackageLock()
    {
        if (packageLaunchLock == null)
            return;
        packageLaunchLock.ReleaseMutex();
        packageLaunchLock.Dispose();
        packageLaunchLock = null;
    }

    private async Task CommitPackageAsync(CancellationToken token)
    {
        if (!pendingPayload)
            return;
        string destination = installFolder;
        var reviewed = session;
        ReleasePackageLock();
        if (!payloadExtracted || !string.Equals(ManagedInstall.ReadVersion(destination), expectedVersion, StringComparison.Ordinal))
        {
            try
            {
                await Task.Run(() =>
                {
                    _ = SetupPackage.VerifySteam(setupArchive, reviewed.Steam.Installed);
                    SetupPackage.Extract(setupArchive, destination, expectedVersion);
                }, token);
            }
            catch (UnauthorizedAccessException) when (ManagedInstall.CanElevate(destination))
            {
                Report("Approve Windows’ prompt to update your existing NeonLoop installation.", 0, 0);
                int result = await Task.Run(() => ManagedInstall.RunProcess(Environment.ProcessPath!, true,
                    ManagedInstall.CopyArgument, destination, expectedVersion ?? ""), token);
                if (result != 0)
                    throw new IOException("NeonLoop couldn’t be copied. Try again or choose a different location.");
            }
            expectedVersion = ManagedInstall.ReadVersion(destination);
            payloadExtracted = true;
        }

        var launchLock = new Mutex(false, EffectsLauncher.LaunchMutex);
        if (!EffectsLauncher.TryAcquireLaunchLock(launchLock))
        {
            launchLock.Dispose();
            throw new IOException("Close the other NeonLoop window and try again.");
        }
        packageLaunchLock = launchLock;

        effects = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            EffectsLauncher.CheckNoGame();
            Report("Preparing storage in " + destination, 0, 0);
            ManagedInstall.ReviewRuntime(destination, reviewed.Steam.Installed);
            string runtime = ManagedInstall.ActivateRuntime(destination, token);
            return ManagedInstall.PrepareEffects(destination, refresh: true, runtimeOverride: runtime);
        }, token);
        EffectsLauncher.SelectInstallation(destination, effects);
        var installed = await Task.Run(() => new InstallSession(effects, reviewed.Steam.Installed, Report, token), token);
        installed.Package = reviewed.Package;
        installed.Request = reviewed.Request;
        installed.Network = reviewed.Network;
        installed.Backend = reviewed.Backend;
        installed.Easy = reviewed.Easy;
        installed.LocalOnly = reviewed.LocalOnly;
        installed.AllowHardwareCheck = reviewed.AllowHardwareCheck;
        installed.Movies = reviewed.Movies;
        installed.ApprovedDownloads = reviewed.ApprovedDownloads;
        ManagedInstall.Register(destination);
        if (!ManagedInstall.IsProtectedLocation(destination))
        {
            try { ManagedInstall.SaveUninstallShortcut(destination); }
            catch (Exception error) { LogError(error); }
        }
        session = installed;
        pendingPayload = false;
    }

    private void ShowStage(SetupStage stage, Control page)
    {
        currentStage = stage;
        if (page.Parent != stageBody)
            stageBody.Controls.Add(page);
        page.Show();
        page.BringToFront();
        for (int i = 0; i < stageButtons.Count; i++)
        {
            bool selected = i == (int)stage;
            var tab = stageButtons[i];
            tab.BackColor = selected ? InstallerTheme.Divider : InstallerTheme.StageBar;
            tab.ForeColor = selected ? ForeColor : InstallerTheme.MutedText;
            tab.AccessibleDescription = selected ? "Current setup stage" : "Setup stage";
            tab.TabStop = selected || (operation == null && locationPage == null && session != null &&
                ((SetupStage)i is SetupStage.Location or SetupStage.Options or SetupStage.Review ||
                    (SetupStage)i == SetupStage.Play && session.Ready));
        }
    }

    private async void SelectStage(SetupStage stage)
    {
        if (operation != null || locationPage != null || session == null || GameRunning || stage == currentStage)
            return;
        saveCustom();
        if (stage == SetupStage.Location)
        {
            if (setupArchive == null && !managed)
                return;
            if (setupArchive == null)
            {
                Home();
                return;
            }
            void Back()
            {
                locationPage.Dispose();
                locationPage = null;
                Home();
            }
            locationPage = new SetupPackage(setupArchive, OpenPackagedInstallAsync, OpenPackagedRemoval,
                installFolder, Back, desktopShortcut, startMenuShortcut);
            ShowStage(SetupStage.Location, locationPage);
            AcceptButton = locationPage.ContinueButton;
        }
        else if (stage == SetupStage.Options)
        {
            if (custom)
                Home(showSetup: true);
            else
                EasySelection();
        }
        else if (stage == SetupStage.Review)
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
        if (!showSetup && session?.HasIncompleteSelection == true && !session.Request.AllowLimited)
        {
            Finish(new IOException("Some selected features still need setup."));
            return;
        }
        bool play = session?.Ready == true && !showSetup;
        Page(play ? "Ready to play" : "Install NeonLoop", status ?? (session == null
            ? "Install 2064: Read Only Memories in Steam first. If you already have it, choose Find game."
            : play ? UseF10 : "Your Steam game is ready."), help: custom && !play,
            stage: play ? SetupStage.Play : SetupStage.Options);
        if (play)
        {
            string shortcutMessage = FinishShortcuts();
            if (shortcutMessage != null)
                Add(content, TextLabel(shortcutMessage));
            if (cleanupOffered)
            {
                cleanupOffered = false;
                _ = ShowStorage(firstVisit: true);
                return;
            }
            if (!string.IsNullOrEmpty(session.Results))
                foreach (string line in session.Results.Split(Environment.NewLine))
                    if (!line.StartsWith("Network:", StringComparison.Ordinal))
                        Wrap(content, TextLabel(line));
            ShowLocation();
            if (GameRunning)
            {
                Wrap(content, TextLabel("The game is open."));
                Primary("Close setup", Close);
            }
            else
                Primary("Play Now", async () => await PlayAsync());
            AddShortcuts(Link("Gameplay options", GameplayOptions), Link("Get music packs", () => MusicOptions()),
                Link("Setup options", SetupOptions));
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
                AddShortcuts(Link("Customize installation", () => { custom = true; Home(showSetup: true); }));
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
        if (!play && managed && (!pendingPayload || expectedVersion != null) && (showSetup || session == null))
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
            if (!ManagedInstall.IsProtectedLocation(installFolder))
                ManagedInstall.SaveUninstallShortcut(installFolder);
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
                Link("Manage downloads and tools", async () => await ShowStorage(firstVisit: false)),
                Link("Remove NeonLoop", ConfirmRemoval));
        else
            AddShortcuts(Link("Customize installation", () => { custom = true; Home(showSetup: true); }));
        if (session?.CanPlay == true)
            Add(content, Link("Gameplay options", GameplayOptions));
        AddButton("Back", () => Home());
    }

    private async Task ShowStorage(bool firstVisit)
    {
        if (operation != null)
            return;
        Page("Manage downloads and tools", "Checking how much space setup used.", stage: SetupStage.Play);
        CleanupGroup[] groups = [];
        Exception failure = null;
        try
        {
            CancellationToken token = StartOperation();
            groups = await Task.Run(() => InstallStorage.Inspect(installFolder, session, token), token);
        }
        catch (Exception error) { failure = error; LogError(error); }
        finally { EndOperation(); }
        if (IsDisposed)
            return;
        if (groups.Length == 0)
        {
            if (firstVisit)
                Home(failure == null ? null : "Setup completed. You can check cleanup later in Setup options.");
            else
            {
                Page("Manage downloads and tools", failure == null ? "There’s nothing to clean up." :
                    "Setup couldn’t read the storage locations. Open setup details to see what happened.", help: failure != null, stage: SetupStage.Play);
                AddButton("Back", SetupOptions);
            }
            return;
        }
        Page(firstVisit ? "Installation complete" : "Manage downloads and tools",
            "Remove setup files to free space. Your installed soundtracks, finished cutscenes and game settings stay.", stage: SetupStage.Play);
        if (session.HasUnfinishedCutscenes)
            Wrap(content, TextLabel("Unfinished cutscene work is kept by default so you can finish later."));
        ShowLocation();
        var checks = groups.Select(group => new InstallerCheckBox
        {
            Text = group.Title + " · " + InstallFiles.Size(group.Bytes),
            Checked = group.RemoveByDefault
        }).ToArray();
        for (int i = 0; i < groups.Length; i++)
        {
            Add(content, checks[i]);
            Wrap(content, TextLabel(groups[i].Details));
        }
        void Continue()
        {
            if (firstVisit)
                Home();
            else
                SetupOptions();
        }
        AddButton(firstVisit ? "Keep files and continue" : "Back", Continue);
        var remove = AddButton("Remove selected", async () =>
        {
            var selected = groups.Where((_, index) => checks[index].Checked).ToArray();
            if (selected.Length == 0 || operation != null)
                return;
            Page("Cleaning up NeonLoop", "Removing the setup files you selected.", stage: SetupStage.Play);
            ShowProgress("Removing setup files…");
            try
            {
                CancellationToken token = StartOperation();
                await Task.Run(() => InstallStorage.Remove(installFolder, selected, token), token);
                EndOperation();
                if (!IsDisposed)
                    Continue();
            }
            catch (Exception error)
            {
                LogError(error);
                EndOperation();
                if (!IsDisposed)
                {
                    Page("Cleanup paused", "Some files may still be in use. You can review what remains and retry.", help: true, stage: SetupStage.Play);
                    Primary("Review remaining files", async () => await ShowStorage(firstVisit));
                    AddButton("Continue", Continue);
                }
            }
        });
        InstallerTheme.Primary(remove);
        void UpdateRemove()
        {
            long bytes = groups.Where((_, index) => checks[index].Checked).Sum(group => group.Bytes);
            remove.Enabled = checks.Any(check => check.Checked);
            remove.Text = bytes > 0 ? "Free " + InstallFiles.Size(bytes) : "Remove selected";
        }
        foreach (var check in checks)
            check.CheckedChanged += (_, _) => UpdateRemove();
        UpdateRemove();
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
            if (index == 1)
                foreach (string dataFolder in ManagedInstall.DataFolders(folder).Where(Directory.Exists))
                    Wrap(content, TextLabel(dataFolder));
            else
                Wrap(content, TextLabel(folder));
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
        if (pendingPayload)
        {
            Add(panel, TextLabel("Soundtracks"));
            customPacks ??= session.Request?.Libraries ?? session.InstalledLibraries;
        }
        var cueNames = pendingPayload ? MusicPack.Libraries : session.InstalledLibraries;
        var packs = pendingPayload ? cueNames.Select(key => new InstallerCheckBox
        {
            Text = MusicPack.Name(key) + (session.Pack.HasLibrary(key) ? " · Installed" : ""),
            Checked = customPacks.Contains(key) || session.Pack.HasLibrary(key),
            Enabled = !session.Pack.HasLibrary(key)
        }).ToArray() : [];
        foreach (var pack in packs)
            Add(panel, pack);
        InstallerCheckBox[] scenes = cueNames.Select(key => new InstallerCheckBox
        {
            Text = MusicPack.Name(key), Checked = customMovies.Contains(key)
        }).ToArray();
        Add(panel, TextLabel("Optional cutscene conversion"));
        Wrap(panel, TextLabel(InstallSession.CutscenePurpose));
        Wrap(panel, TextLabel(InstallSession.ProcessingChoices));
        if (scenes.Length != 0)
            foreach (InstallerCheckBox choice in scenes)
                Add(panel, choice);
        else
            Add(panel, Link("Get music packs for remixed cutscenes", () => { saveCustom(); MusicOptions(); }));

        var offline = new InstallerCheckBox { Text = "Use downloads I already have", Checked = localOnly };
        var gpu = new InstallerCheckBox { Text = "Check my NVIDIA GPU for CUDA acceleration", Checked = consent };
        Add(panel, offline);
        Add(panel, gpu);
        for (int i = 0; i < packs.Length; i++)
        {
            var pack = packs[i];
            var scene = scenes[i];
            scene.Enabled = pack.Checked;
            pack.CheckedChanged += (_, _) =>
            {
                scene.Enabled = pack.Checked;
                if (!pack.Checked)
                    scene.Checked = false;
            };
        }
        Add(panel, Link("Game network privacy", () => PrivacyOptions()));
        if (!pendingPayload)
            Add(panel, Link("Find Steam game again…", async () =>
            {
                saveCustom();
                await LocateAsync();
            }));
        saveCustom = () =>
        {
            localOnly = offline.Checked;
            consent = gpu.Checked;
            customMovies = cueNames.Where((_, index) => scenes[index].Enabled && scenes[index].Checked).ToArray();
            if (pendingPayload)
                customPacks = cueNames.Where((_, index) => packs[index].Checked).ToArray();
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
                Add(content, Link("Download " + asset.label, () => ConfirmMusicDownload(asset)));
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

    private void ConfirmMusicDownload(InstallAsset asset)
    {
        Page("Add " + asset.label, "Setup verifies and installs this soundtrack for gameplay.");
        bool needsDownload = session.DownloadsNeeded([asset]).Length != 0;
        Wrap(content, TextLabel(needsDownload ? "Download · " + InstallFiles.Size(asset.bytes) + " from GitHub" :
            "Setup can use an embedded or local copy."));
        ShowLocation();
        AddButton("Back", () => MusicOptions());
        Primary(needsDownload ? "Download and add" : "Add soundtrack", async () => await RunMusicStepAsync(token => Task.Run(() =>
        {
            var assets = new SetupAssets(session.Package, effects, localOnly, false, [asset],
                question => AskAsset(question, token), Report);
            session.InstallPack(assets.Get(asset, token), Report, token);
        }, token), "Preparing the music pack…"));
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
        session.AllowHardwareCheck = custom ? consent : easyMovies == true && easyHardware;
        if (!custom)
        {
            bool offline = session.IsOfflinePackage || session.Request?.Offline == true;
            session.Request = new EasyInstallRequest(easyPacks, easyMovies == true ? easyPacks : [],
                Offline: offline);
        }
        else if (pendingPayload)
            session.Request = new EasyInstallRequest(customPacks ?? [], customMovies,
                Offline: session.IsOfflinePackage);
        session.Movies = custom && !pendingPayload ? customMovies.Where(key =>
            session.Pack.HasLibrary(key) || session.Request?.Libraries.Contains(key) == true).ToArray() : session.Request.Movies;
        string review;
        try { review = session.Review(CancellationToken.None); }
        catch (Exception error) { Finish(error); return Task.CompletedTask; }
        string summary = session.Movies.Length == 0
            ? "Setup prepares NeonLoop and any selected soundtracks. This setup won’t convert cutscenes."
            : "Setup prepares NeonLoop, your chosen soundtracks, and the cutscene remixes you selected.";
        session.ApprovedDownloads = session.DownloadsNeeded(session.ReviewedAssets());
        long bytes = session.ApprovedDownloads.Where(asset => asset.key != null).Sum(asset => asset.bytes);
        string network = session.IsOfflinePackage || localOnly || session.Request?.Offline == true
            ? "Setup uses the files in the installer and any verified local files."
            : bytes == 0 ? (session.NeedsMovieWork ? "" : "No downloads are expected.") :
                "Soundtracks · " + InstallFiles.Size(bytes) + " to download.";
        ShowInstallReview(summary, network, review);
        return Task.CompletedTask;
    }

    private void EasySelection()
    {
        Page("Easy Install", "Choose which soundtracks to add to your game.", stage: SetupStage.Options);
        easyPacks ??= session.Request?.Libraries ?? ["dark-synthwave"];
        easyMovies ??= session.Request?.Movies.Any(key => !session.MovieReady(key)) == true;
        var selections = MusicPack.Libraries.Select(key =>
            new InstallerCheckBox { Text = MusicPack.Name(key), Checked = easyPacks.Contains(key) }).ToArray();
        foreach (var item in selections)
            Add(content, item);
        var movies = new InstallerCheckBox { Text = "Also convert cutscenes for the selected soundtracks", Checked = easyMovies == true };
        var gpu = new InstallerCheckBox { Text = "Check my NVIDIA GPU for CUDA acceleration", Checked = easyHardware };
        Add(content, movies);
        Add(content, gpu);
        Wrap(content, TextLabel(InstallSession.CutscenePurpose));
        Wrap(content, TextLabel(InstallSession.ProcessingChoices));
        Wrap(content, TextLabel("You can play with your soundtrack while setup converts the cutscenes."));
        void UpdateCutsceneChoices()
        {
            movies.Enabled = selections.Any(item => item.Checked);
            gpu.Enabled = movies.Enabled && movies.Checked;
        }
        movies.CheckedChanged += (_, _) => UpdateCutsceneChoices();
        foreach (var item in selections)
            item.CheckedChanged += (_, _) => UpdateCutsceneChoices();
        UpdateCutsceneChoices();
        void SaveSelection()
        {
            easyPacks = MusicPack.Libraries.Where((_, index) => selections[index].Checked).ToArray();
            easyMovies = movies.Enabled && movies.Checked;
            easyHardware = gpu.Enabled && gpu.Checked;
        }
        saveCustom = SaveSelection;
        if (session.Request?.Offline == true && session.Package == null)
            Add(content, Link("Use the offline setup file again", SelectOfflineSource));
        AddButton("Back", () => { SaveSelection(); Home(showSetup: true); });
        Primary("Continue", async () =>
        {
            SaveSelection();
            await ReviewAsync(selectionConfirmed: true);
        });
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

    private static (Control panel, Func<uint> readFlags) GameplayControls(uint flags)
    {
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = Stack();
        var right = Stack();
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);
        foreach (var group in GameplayInstallSettings.Options.GroupBy(option => option.Group))
        {
            var column = group.Key is "Exploration" or "Puzzle assists" or "Game startup" ? right : left;
            Add(column, TextLabel(group.Key));
            foreach (var option in group)
            {
                var box = new InstallerCheckBox { Text = option.Label, Checked = GameplayInstallSettings.On(flags, option.Bit) };
                box.CheckedChanged += (_, _) => flags = GameplayInstallSettings.Set(flags, option.Bit, box.Checked);
                Add(column, box);
            }
        }
        return (columns, () => flags);
    }

    private void GameplayOptions()
    {
        Page("Gameplay options", UseF10, stage: SetupStage.Play);
        var (panel, readFlags) = GameplayControls(GameplayInstallSettings.Read(effects));
        Add(content, panel);
        AddButton("Back", () => Home());
        Primary("Save and continue", () =>
        {
            try
            {
                GameplayInstallSettings.SaveReviewed(effects, readFlags());
                Home();
            }
            catch (Exception error)
            {
                Wrap(content, TextLabel(error.GetBaseException().Message));
                QueuePageFit();
            }
        });
    }

    private bool ReviewGameplayBeforePlay()
    {
        if (GameplayInstallSettings.WasReviewed(effects))
            return true;
        using var dialog = new Form
        {
            Text = "NeonLoop · Gameplay options", Icon = Icon, Font = Font, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(920, 650),
            MinimumSize = new Size(700, 470), AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96)
        };
        InstallerWindow.FitWorkArea(dialog, .53, .60);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = TextLabel("Choose your gameplay settings. You can change them later with F10.");
        layout.Controls.Add(heading, 0, 0);
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var (panel, readFlags) = GameplayControls(GameplayInstallSettings.Read(effects));
        scroller.Controls.Add(panel);
        layout.Controls.Add(scroller, 0, 1);
        var errorText = TextLabel("");
        layout.Controls.Add(errorText, 0, 2);
        var buttons = ButtonRow();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        var back = new Button { Text = "Back", AutoSize = true, DialogResult = DialogResult.Cancel };
        var play = new Button { Text = "Save and Play", AutoSize = true };
        play.Click += (_, _) =>
        {
            try
            {
                GameplayInstallSettings.SaveReviewed(effects, readFlags());
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            {
                LogError(error);
                errorText.Text = "Your settings couldn’t be saved. Try again or go back to setup.";
            }
        };
        buttons.Controls.AddRange([play, back]);
        layout.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(layout);
        InstallerTheme.Apply(dialog);
        InstallerTheme.Primary(play);
        InstallerTheme.Secondary(back);
        dialog.AcceptButton = play;
        dialog.CancelButton = back;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private void ShowInstallReview(string summary, string network, string review)
    {
        bool agreed = false;
        void RenderReview()
        {
            Page("Ready to install", summary, stage: SetupStage.Review);
            Wrap(content, TextLabel("Install location · " + installFolder));
            Wrap(content, TextLabel("Steam game found · " + session.Steam.Installed));
            if (pendingPayload)
            {
                string runtime = ManagedInstall.PreviewRuntime(installFolder);
                Wrap(content, TextLabel("Private game, music and setup files · " + runtime));
                if (!GameInstaller.InstallFiles.IsInside(runtime, installFolder))
                    Wrap(content, TextLabel("Setup keeps the existing private game location so Windows permissions and any network rules still apply."));
                if (desktopShortcut || startMenuShortcut)
                    Wrap(content, TextLabel(desktopShortcut && startMenuShortcut ? "Add desktop and Start menu shortcuts." :
                        desktopShortcut ? "Add a desktop shortcut." : "Add a Start menu shortcut."));
            }
            if (session.Easy)
                Wrap(content, TextLabel("Easy Install skips the firewall check."));
            else if (session.Network == "block")
                Wrap(content, TextLabel(NetworkPrivacy.BlockSummary));
            else
                Wrap(content, TextLabel("Normal network access. Setup removes any NeonLoop block rules for this game copy."));
            if (!string.IsNullOrEmpty(network))
                Wrap(content, TextLabel(network));
            if (session.NeedsMovieWork)
            {
                Wrap(content, TextLabel(session.ProcessorSummary));
                bool local = session.IsOfflinePackage || localOnly || session.Request?.Offline == true;
                var tools = session.DownloadsNeeded(session.ToolsFor(InstallHardware.Cpu));
                long toolBytes = tools.Sum(asset => asset.bytes);
                if (!local && toolBytes > 0)
                    Wrap(content, TextLabel("Cutscene tools may need " + InstallFiles.Size(toolBytes) +
                        " in downloads." + (session.AllowHardwareCheck ? " Setup asks separately before downloading the NVIDIA runtime." : "")));
                Wrap(content, TextLabel("Python and uv set up the local tools. PyTorch and the Bandit model separate the audio. FFmpeg helps render the new clips."));
                Add(content, Link("Skip cutscene conversion", () =>
                {
                    string[] pending = session.Movies.Where(key => !session.MovieReady(key)).ToArray();
                    easyMovies = false;
                    customMovies = customMovies.Except(pending).ToArray();
                    _ = ReviewAsync(selectionConfirmed: true);
                }));
            }
            Add(content, Link("Show setup details", () => ShowReviewText("Setup details", review, RenderReview)));
            var acceptTerms = new InstallerCheckBox { Text = "I agree to the terms", Checked = agreed };
            Add(consentBar, Link("Read the terms", () => ShowReviewText(notice.Title, notice.Text, RenderReview)));
            Add(consentBar, acceptTerms);
            consentBar.Visible = true;
            AddButton("Back", () => { if (custom) Home(showSetup: true); else EasySelection(); });
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
        panel.Height = (compact ? 116 : 160) * DeviceDpi / 96;
        return panel;
    }

    private async Task InstallAsync()
    {
        CancellationToken token = StartOperation();
        cutsceneOperation = session.NeedsMovieWork ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
        cutsceneTiming = cutsceneOperation != null ? new CutsceneTiming() : null;
        CancellationToken cutsceneToken = cutsceneOperation?.Token ?? token;
        ShowInstalling(SetupStage.Install);
        Exception failure = null;
        try
        {
            await CommitPackageAsync(token);
            await Task.Run(() =>
            {
                string path = Path.Combine(effects, "Install.log");
                using var writer = new StreamWriter(path, true) { AutoFlush = true };
                journal = writer;
                try
                {
                    RecordTerms();
                    session.Run(Step, Report, AskAsset, AskApproval, TrackCutscenes, token, cutsceneToken);
                }
                finally { journal = null; }
            }, token);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (cutsceneOperation?.IsCancellationRequested == true && !token.IsCancellationRequested)
                easyMovies = false;
            cutsceneOperation?.Dispose();
            cutsceneOperation = null;
            cutsceneTiming = null;
            EndOperation();
        }
        if (!IsDisposed)
            Finish(failure);
    }

    private void ShowInstalling(SetupStage stage)
    {
        string summary = cutsceneOperation == null ? "Preparing the game and selected soundtracks." :
            "You can play while cutscenes convert, or skip unfinished clips and keep your installed music.";
        Page("Installing NeonLoop", summary, stage: stage);
        ShowProgress("Preparing installation…");
        if (stage == SetupStage.Tools)
            AddBackgroundPlay();
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
        if (GameRunning || launchingGame)
            return;
        launchingGame = true;
        try
        {
            if (!ReviewGameplayBeforePlay())
                return;
        }
        catch (Exception error)
        {
            Finish(error);
            return;
        }
        finally { launchingGame = false; }
        Game?.Dispose();
        Game = null;
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

    private bool GameRunning => Game != null && !Game.Process.HasExited;

    private async void PlayWhileConverting(Button button)
    {
        if (launchingGame || GameRunning || !session.CanPlayDuringConversion)
            return;
        launchingGame = true;
        try
        {
            if (!ReviewGameplayBeforePlay())
                return;
            if (!button.IsDisposed)
            {
                button.Enabled = false;
                button.Text = "Opening game…";
            }
            Game?.Dispose();
            Game = null;
            Game = await Task.Run(() => EffectsLauncher.StartGame(session, (message, _, _) =>
                Invoke((Action)(() => WriteActivity(InstallerLog.Clean(message)))), CancellationToken.None, whileConverting: true));
            if (!button.IsDisposed)
                button.Text = "Game is open";
            if (operation == null && !IsDisposed)
                Home();
        }
        catch (Exception error)
        {
            Game = null;
            if (!IsDisposed)
            {
                LogError(error);
                Wrap(content, TextLabel(PlayerMessage(error, "The game couldn’t start. Setup can continue.")));
                QueuePageFit();
            }
        }
        finally { launchingGame = false; }
        if (Game == null && !button.IsDisposed)
        {
            button.Text = button.AccessibleName;
            button.Enabled = true;
        }
    }

    private void Finish(Exception failure)
    {
        if (failure != null)
            LogError(failure);
        if (failure == null)
        {
            cleanupOffered = managed;
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
            if (session.NeedsMovieWork)
                AddButton("Change cutscene choices", () => { if (custom) Home(showSetup: true); else EasySelection(); });
        }
        CancelButton = AddButton("Close", Close);
    }

    private void ShowProgress(string message)
    {
        activeStep = null;
        timedAction = null;
        timedCount = 0;
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
        AddSkipCutscenes();
        WrapTiming();
        RefreshTiming();
    }

    private void WrapTiming()
    {
        foreach (Label label in new[] { timingText, action, next })
        {
            label.MaximumSize = WrappedLabelSize();
            if (!wrapped.Contains(label))
                wrapped.Add(label);
        }
    }

    private CancellationToken StartOperation()
    {
        operation = new CancellationTokenSource();
        operationTime.Restart();
        timingTimer.Start();
        return operation.Token;
    }

    private void EndOperation()
    {
        operation?.Dispose();
        operation = null;
        timingTimer.Stop();
        operationTime.Stop();
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

    private void AddSkipCutscenes()
    {
        if (cutsceneOperation == null || cutsceneOperation.IsCancellationRequested || buttons.Controls.Cast<Control>()
            .Any(button => button.Text == "Skip cutscene conversion"))
            return;
        var stop = AddButton("Skip cutscene conversion", SkipCutscenes);
        stop.AccessibleDescription = "Stop optional conversion and keep the game, soundtrack packs, and completed clips.";
    }

    private void SkipCutscenes()
    {
        if (cutsceneOperation == null || cutsceneOperation.IsCancellationRequested)
            return;
        foreach (Control button in buttons.Controls)
            if (button.Text == "Skip cutscene conversion")
                button.Enabled = false;
        action.Text = "Stopping cutscene conversion. Setup will keep completed work.";
        _ = cutsceneOperation.CancelAsync();
        RefreshTiming();
    }

    private void AddBackgroundPlay(string label = "Play while converting")
    {
        if (cutsceneOperation == null || session?.CanPlayDuringConversion != true ||
            buttons.Controls.Cast<Control>().Any(button => (string)button.Tag == "background-play"))
            return;
        Button play = null;
        play = AddButton(GameRunning ? "Game is open" : label, () => PlayWhileConverting(play));
        play.Tag = "background-play";
        play.AccessibleName = label;
        play.Enabled = !GameRunning && !launchingGame;
    }

    private void Step(string name, string upcoming, int done, int count)
    {
        Report(name, 0, 0);
        Invoke((Action)(() =>
        {
            activeStep = name;
            timedAction = null;
            total.Maximum = count;
            total.Value = done;
            action.Text = name;
            next.Text = $"{done} of {count} steps complete · Next: {upcoming}";
            total.Visible = next.Visible = true;
            if (name == "Choose cutscene processing" || name.EndsWith(" cutscenes", StringComparison.Ordinal))
            {
                ShowStage(SetupStage.Tools, wizardPage);
                AddBackgroundPlay();
            }
            RefreshTiming();
            QueuePageFit();
        }));
    }

    private void TrackCutscenes(CutsceneUpdate update)
    {
        Invoke((Action)(() =>
        {
            cutsceneTiming?.Observe(update);
            SetupStage phase = update.Stage switch
            {
                "separate" => SetupStage.Separate,
                "render" => SetupStage.Render,
                _ => SetupStage.Tools
            };
            if (currentStage != phase)
                ShowStage(phase, wizardPage);
            RefreshTiming();
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
            if (count > 0)
            {
                if (timedAction != clean || timedCount != count || done == 0)
                {
                    timedAction = clean;
                    timedCount = count;
                    actionTiming.Plan(count, resetRate: true);
                }
                actionTiming.Observe(done, operationTime.Elapsed.TotalSeconds);
            }
            else
                timedAction = null;
            current.Style = count == 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
            if (count > 0)
                current.Value = Math.Clamp((int)(100.0 * done / count), 0, 100);
            action.Text = (verbose ? clean : BriefProgress(clean) ?? cutsceneTiming?.Label ?? activeStep ?? "Getting things ready…")
                + (count > 0 ? $" · {current.Value}%" : "");
            RefreshTiming();
        }));
    }

    private void RefreshTiming()
    {
        if (!progress.Visible || operation == null)
            return;
        var duration = operationTime.Elapsed;
        string elapsed = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} hr {duration.Minutes} min" :
            $"{duration.Minutes} min {duration.Seconds:D2} sec";
        bool skipped = cutsceneOperation?.IsCancellationRequested == true && !operation.IsCancellationRequested;
        bool processing = !skipped && cutsceneTiming?.Label != null;
        string estimate = timedAction != null && !processing
            ? "Current action · " + CutsceneTiming.Format(actionTiming.Remaining(duration.TotalSeconds, active: true)) : "";
        var lines = new List<string> { "Elapsed · " + elapsed };
        if (estimate.Length != 0)
            lines[0] += "   |   " + estimate;
        if (skipped)
            lines.Add("Skipping unfinished cutscene conversion. Setup will keep completed work.");
        else if (cutsceneTiming?.HasPlan == true)
            lines.Add(cutsceneTiming.Status());
        else if (currentStage is SetupStage.Install or SetupStage.Tools or SetupStage.Separate or SetupStage.Render)
            lines.Add("Total remaining · " + (activeStep == "Installation complete" ? "done" : "estimating…"));
        string text = string.Join(Environment.NewLine, lines);
        if (timingText.Text == text)
            return;
        int previousHeight = timingText.PreferredHeight;
        timingText.Text = text;
        if (timingText.PreferredHeight != previousHeight)
            QueuePageFit();
    }

    private static string BriefProgress(string text)
    {
        if (text.StartsWith("Starting Steam", StringComparison.Ordinal))
            return "Opening Steam…";
        if (text.StartsWith("Starting the private game", StringComparison.Ordinal))
            return "Starting the game…";
        if (text.StartsWith("Downloading ", StringComparison.Ordinal) ||
            text.StartsWith("Reading local ", StringComparison.Ordinal) ||
            text.StartsWith("Reading ", StringComparison.Ordinal) && text.EndsWith(" from the installer", StringComparison.Ordinal) ||
            text.StartsWith("NVIDIA couldn’t finish.", StringComparison.Ordinal))
            return text;
        if (text.StartsWith("Checking the music pack", StringComparison.Ordinal) || text.StartsWith("Extracting ", StringComparison.Ordinal))
            return "Adding your music pack…";
        return null;
    }

    private T AskDuringSetup<T>(Action<Action<T>> show, CancellationToken token)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke((Action)(() =>
        {
            show(value => answer.TrySetResult(value));
            if (cutsceneOperation?.Token == token)
            {
                AddSkipCutscenes();
                AddBackgroundPlay("Play Now");
            }
        }));
        try { return answer.Task.WaitAsync(token).GetAwaiter().GetResult(); }
        finally { Invoke((Action)(() => ShowInstalling(cutsceneOperation?.Token == token ? SetupStage.Tools : SetupStage.Install))); }
    }

    private DownloadDecision AskAsset(DownloadQuestion question, CancellationToken token) =>
        AskDuringSetup<DownloadDecision>(complete => ShowDownloadPrompt(question, session.LocalFiles.LastFolder, complete,
            cutsceneOperation?.Token == token), token);

    private bool AskApproval(string heading, string text, CancellationToken token) =>
        AskDuringSetup<bool>(complete =>
        {
            bool cpu = heading == InstallSession.CpuDecision;
            Page(heading, text, stage: SetupStage.Tools);
            if (!cpu)
                AddButton("Consider CPU instead", () => complete(false));
            Primary(cpu ? "Convert with CPU" : "Use NVIDIA", () => complete(true));
        }, token);

    private void ShowDownloadPrompt(DownloadQuestion question, string folder, Action<DownloadDecision> complete, bool cutscenes)
    {
        Page("Setup file needed", session.DownloadPurpose(question.Asset.FileName),
            stage: cutscenes ? SetupStage.Tools : SetupStage.Install);
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
            error.Message.Contains("unsupported game build", StringComparison.OrdinalIgnoreCase) ||
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
        FitDetailsPanel();
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
    }

    private int MinimumDetailsHeight() => !diagnostics.Visible ? 0 : (verbose ? 142 : 48) * DeviceDpi / 96;

    private void FitDetailsPanel()
    {
        int height = MinimumDetailsHeight();
        if (verbose && diagnostics.Visible)
        {
            int occupied = layout.Padding.Vertical + layout.GetRowHeights().Take(3).Sum();
            height = Math.Max(height, body.ClientSize.Height - occupied - 2);
        }
        if (layout.RowStyles[3].Height != height)
            layout.RowStyles[3].Height = height;
    }

    private int MinimumTextHeight => 120 * DeviceDpi / 96;

    private int TextPanelExtraHeight => longTextPanel is { IsDisposed: false } panel
        ? Math.Max(0, panel.Height - MinimumTextHeight) : 0;

    private void FitTextPanel()
    {
        if (longTextPanel is not { IsDisposed: false } panel)
            return;
        int occupied = layout.Height - panel.Height;
        int height = Math.Max(MinimumTextHeight, body.ClientSize.Height - occupied - 2);
        if (panel.Height != height)
            panel.Height = height;
    }

    private void QueuePageFit()
    {
        if (!IsHandleCreated || resizeQueued || locationPage != null)
            return;
        resizeQueued = true;
        BeginInvoke(() =>
        {
            resizeQueued = false;
            if (IsDisposed || locationPage != null)
                return;
            layout.PerformLayout();
            wizardPage.PerformLayout();
            FitDetailsPanel();
            FitTextPanel();
            layout.PerformLayout();
            body.AutoScroll = layout.Height > body.ClientSize.Height + 2;
            if (WindowState != FormWindowState.Normal)
                return;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int cap = Math.Max(1, area.Height - 32);
            int chrome = Height - ClientSize.Height + stages.PreferredSize.Height;
            int contentWidth = Math.Max(260, body.ClientSize.Width);
            int naturalBody = Math.Max(layout.Height, layout.GetPreferredSize(new Size(contentWidth, 0)).Height) -
                (int)layout.RowStyles[3].Height + MinimumDetailsHeight() - TextPanelExtraHeight;
            int footer = buttons.PreferredSize.Height + (consentBar.Visible ? consentBar.PreferredSize.Height : 0);
            int minimum = Math.Min(cap, Math.Max(510 * DeviceDpi / 96, naturalBody + footer + chrome + 12));
            int height = Math.Clamp(Math.Max(Height, minimum), minimum, cap);
            MinimumSize = new Size(Math.Min(area.Width - 32, 720 * DeviceDpi / 96), minimum);
            int top = Math.Clamp(Top + (Height - height) / 2, area.Top, Math.Max(area.Top, area.Bottom - height));
            if (height != Height || top != Top)
                SetBounds(Left, top, Width, height);
            FitDetailsPanel();
            FitTextPanel();
            body.AutoScroll = layout.Height > body.ClientSize.Height + 2;
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

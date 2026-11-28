using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal static class EffectsLauncher
{
    private const string Args = "-force-d3d11 -screen-fullscreen 0 -screen-width 1280 -screen-height 720";
    internal const string LaunchMutex = @"Local\ReadOnlyMemoriesBorderedLaunch";
    private static string Effects = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Effects");
    private static bool uiInitialized;
    internal static System.Drawing.Icon InstallerIcon { get; private set; }
    internal static string InstallFolder { get; private set; } = ManagedInstall.Folder;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == NetworkPrivacy.RemoveArgument)
            {
                NetworkPrivacy.RemoveRules(args[1], args[2]);
                return 0;
            }
            if (args.Length == 4 && args[0] == ManagedInstall.RemoveArgument)
            {
                bool removeData = args[3] == "remove-data";
                InitializeInstallerUi();
                using var wizard = new InstallWizard(Effects,
                    removalWork: () => ManagedInstall.CompleteRemoval(args[1], int.Parse(args[2]), removeData));
                try { RunWizard(wizard); }
                finally { ManagedInstall.RetireRemovalHelper(); }
                return wizard.OutcomeOk ? 0 : 1;
            }
            if (args.Length == 2 && args[0] == ManagedInstall.RemoveFilesArgument)
            {
                string folder = ManagedInstall.Validate(args[1]);
                if (!ManagedInstall.CanElevate(folder))
                    return 1;
                try
                {
                    CheckNoGame();
                    ManagedInstall.RemovePayload(folder);
                    return 0;
                }
                catch { return 1; }
            }
            if (SetupPackage.TryOpen(Environment.ProcessPath, out var package))
            {
                ManagedInstall.CleanRemovalHelpers();
                using (package)
                {
                    if (args.Length == 3 && args[0] == ManagedInstall.CopyArgument)
                    {
                        string folder = ManagedInstall.Validate(args[1]);
                        if (!ManagedInstall.CanElevate(folder))
                            return 1;
                        SetupPackage.Extract(package, folder, args[2].Length == 0 ? null : args[2]);
                        return 0;
                    }
                    InitializeInstallerUi();
                    using var wizard = new InstallWizard(Effects, package: package);
                    RunWizard(wizard);
                }
                return 0;
            }
        }
        catch (Win32Exception error) when (ManagedInstall.WasDeclined(error))
        {
            ShowNotice("Windows permission was declined. Open setup again to retry.");
            return 1;
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            return 1;
        }
        if (args.Length is 3 or 4 && args[0] == NetworkPrivacy.ApplyArgument)
        {
            try
            {
                string target = args.Length == 4 ? ManagedInstall.CheckFirewallTarget(args[3]) : Effects;
                if (args.Length == 3 && ManagedInstall.IsManaged(ManagedInstall.Folder))
                    return 1;
                NetworkPrivacy.Apply(target, args[2], args[1]);
                return 0;
            }
            catch { return 1; }
        }
        bool privacyOnly = args.Length == 1 && args[0] == NetworkPrivacy.SettingsArgument;
        bool uninstallOnly = args.Length == 1 && args[0] == ManagedInstall.UninstallArgument;
        try
        {
            ManagedInstall.CleanRemovalHelpers();
            if (Path.GetFileName(Environment.ProcessPath).StartsWith("NeonLoop-Setup", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This setup file is incomplete. Download a fresh copy of NeonLoop Setup.");
            using var launchLock = new Mutex(false, LaunchMutex);
            if (!TryAcquireLaunchLock(launchLock))
            {
                ShowNotice("NeonLoop is already open. Close it or the game, then try again.");
                return 1;
            }

            try
            {
                InitializeInstallerUi();
                if (uninstallOnly && !ManagedInstall.IsManaged(ManagedInstall.Folder))
                    throw new IOException("Windows couldn’t identify this NeonLoop installation.");
                if (!uninstallOnly)
                    Effects = ManagedInstall.PrepareEffects(ManagedInstall.Folder);
                using var wizard = new InstallWizard(Effects, args.FirstOrDefault() == SetupPackage.FirstRunArgument,
                    args.Contains("--desktop-shortcut"), args.Contains("--start-menu-shortcut"), privacyOnly: privacyOnly, uninstallOnly: uninstallOnly);
                RunWizard(wizard);
            }
            finally
            {
                launchLock.ReleaseMutex();
            }
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            return 1;
        }
        return 0;
    }

    internal static bool TryAcquireLaunchLock(Mutex launchLock)
    {
        try { return launchLock.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static void ShowStartupError(Exception error)
    {
        string message = error.GetBaseException().Message;
        try
        {
            if (ManagedInstall.IsManaged(InstallFolder))
            {
                string runtime = ManagedInstall.RuntimeFolder(InstallFolder);
                string log = Path.Combine(runtime, "setup-error.log");
                Directory.CreateDirectory(runtime);
                GameInstaller.InstallFiles.CheckNoLinks(log);
                File.WriteAllText(log, DateTimeOffset.Now.ToString("O") + Environment.NewLine + error);
                message += Environment.NewLine + Environment.NewLine + "Setup log: " + log;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
        ShowNotice(message);
    }

    private static void ShowNotice(string message)
    {
        InitializeInstallerUi();
        using var wizard = new InstallWizard(Effects, startupProblem: message);
        RunWizard(wizard);
    }

    private static void InitializeInstallerUi()
    {
        if (uiInitialized)
            return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        if (Environment.ProcessPath is { } executable)
            InstallerIcon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
        uiInitialized = true;
    }

    private static void RunWizard(InstallWizard wizard)
    {
        wizard.ShowDialog();
        if (wizard.Game != null)
            using (RunningGame game = wizard.Game)
                game.Process.WaitForExit();
    }

    internal static void SelectInstallation(string folder, string effects)
    {
        InstallFolder = folder;
        Effects = effects;
    }

    internal static RunningGame StartGame(InstallSession session, InstallProgress progress, CancellationToken token,
        bool whileConverting = false)
    {
        if (!(whileConverting ? session.CanPlayDuringConversion : session.CanPlay))
            throw new InvalidOperationException("Finish the private game and network setup first.");
        CheckNoGame();
        token.ThrowIfCancellationRequested();
        GameplayInstallSettings.Ensure(session.Effects);
        progress("Starting Steam if it is not already running: " + session.Steam.Steam, 0, 0);
        StartSteam(session.Steam.Steam, token);
        CheckNoGame();
        token.ThrowIfCancellationRequested();

        string choice = Path.Combine(Effects, "MusicLibrary.txt");
        string gameFolder = session.GameFolder;
        MusicPack musicPack = session.Pack;

        var start = new ProcessStartInfo(Path.Combine(gameFolder, "ROM.exe"), Args);
        start.WorkingDirectory = gameFolder;
        start.UseShellExecute = false;
        start.EnvironmentVariables["SteamAppId"] = SteamGame.AppId;
        start.EnvironmentVariables["SteamGameId"] = SteamGame.AppId;
        start.EnvironmentVariables["ROM_FX_PROFILE"] = Path.Combine(Effects, "Profiles.json");
        start.EnvironmentVariables["ROM_REPLACEMENT_SCORE"] = musicPack.HasSoundtrack ? musicPack.Root : "";
        start.EnvironmentVariables["ROM_MUSIC_SELECTION"] = choice;
        start.EnvironmentVariables["ROM_MUSIC_INSTALLED"] = musicPack.InstalledKeys;
        foreach (InstallAsset asset in session.MusicAssets)
            if (InstallAssets.IsMusicDownload(asset))
                start.EnvironmentVariables["ROM_MUSIC_DOWNLOAD_" + asset.key] = asset.url;
        start.EnvironmentVariables["ROM_CUTSCENES"] = session.Cutscenes;
        start.EnvironmentVariables["ROM_GAMEPLAY_SETTINGS"] = GameplayInstallSettings.PathFor(session.Effects);
        start.EnvironmentVariables["ROM_GAMEPLAY_WINDOW"] = Path.Combine(Effects, "GameplayWindow.txt");
        start.EnvironmentVariables["ROM_GAMEPLAY_HISTORY"] = Path.Combine(gameFolder, ".neonloop-dialogue-" + Guid.NewGuid().ToString("N"));
        start.EnvironmentVariables["ROM_GAMEPLAY_DATA"] = Path.Combine(Effects, "GameplayData");
        start.EnvironmentVariables["ROM_PROFILE_LOGO"] = Path.Combine(Effects, "ProfileLogo.rgba");

        DateTime startedUtc = DateTime.UtcNow;
        string history = start.EnvironmentVariables["ROM_GAMEPLAY_HISTORY"];
        progress("Starting the private game and applying its saved window placement: " + gameFolder, 0, 0);
        token.ThrowIfCancellationRequested();
        Process game = Process.Start(start);
        if (game == null)
            throw new InvalidOperationException("Windows could not start Read Only Memories.");
        var running = new RunningGame(game, history);
        try
        {
            EffectsWindow.Maximize(game, gameFolder, startedUtc, Effects);
        }
        catch
        {
            running.Dispose();
            throw;
        }
        return running;
    }

    internal static void CheckNoGame()
    {
        Process[] running = Process.GetProcessesByName("ROM");
        foreach (Process game in running)
            game.Dispose();
        if (running.Length != 0)
            throw new InvalidOperationException("Close Read Only Memories normally, then open Play with Effects again.");
    }

    private static void StartSteam(string steamFolder, CancellationToken token)
    {
        Process[] running = Process.GetProcessesByName("steam");
        foreach (Process steam in running)
            steam.Dispose();
        if (running.Length == 0)
        {
            if (steamFolder == null)
                throw new InvalidOperationException("Open the Steam desktop app, then choose Play Now again.");
            using (Process steam = Process.Start(Path.Combine(steamFolder, "steam.exe")))
            {
                if (steam == null)
                    throw new InvalidOperationException("Windows could not start Steam.");
            }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(45))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(steamFolder, "steamclient64.dll")) && SteamResponding())
                    return;
                Thread.Sleep(500);
            }
            throw new InvalidOperationException("Open Steam and finish signing in, then open Play with Effects again.");
        }
    }

    private static bool SteamResponding()
    {
        Process[] running = Process.GetProcessesByName("steam");
        bool ready = false;
        foreach (Process steam in running)
        {
            using (steam)
                ready |= steam.MainWindowHandle != IntPtr.Zero;
        }
        return ready;
    }

}

internal sealed class RunningGame : IDisposable
{
    private readonly string history;
    internal Process Process { get; private set; }

    internal RunningGame(Process process, string history)
    {
        Process = process;
        this.history = history;
    }

    public void Dispose()
    {
        bool ended;
        try { ended = Process.HasExited; }
        finally { Process.Dispose(); }
        if (ended)
        {
            InstallFiles.RemovePartial(history);
            InstallFiles.RemovePartial(history + ".pending");
        }
    }
}

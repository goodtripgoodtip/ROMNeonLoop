using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Files = GameInstaller.InstallFiles;

internal static class ManagedInstall
{
    internal const string Launcher = "Play with Effects.exe";
    internal const string Marker = "NeonLoop.install";
    internal const string OwnedFiles = "NeonLoop.files";
    internal const string CopyArgument = "--install-payload";
    internal const string RemoveArgument = "--remove-neonloop";
    internal const string RemoveFilesArgument = "--remove-neonloop-files";
    internal const string UninstallArgument = "--uninstall";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string EntryPrefix = "GoodTripGoodTip.NeonLoop.";
    internal static string ProgramRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GoodTripGoodTip");
    private static string LegacyDefaultFolder => Path.Combine(ProgramRoot, "NeonLoop");
    internal static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "GoodTripGoodTip", "NeonLoop");
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoodTripGoodTip", "NeonLoop");
    internal static string Folder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    internal static string Validate(string input)
    {
        if (!Path.IsPathFullyQualified(input))
            throw new IOException("Choose a full install location on this PC.");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        string drive = Path.GetPathRoot(path)!;
        if (drive.StartsWith(@"\\") || path.Equals(drive, StringComparison.OrdinalIgnoreCase) ||
            path[drive.Length..].Split(Path.DirectorySeparatorChar).Any(part => part.Equals("steamapps", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose a local folder outside your Steam library.");
        foreach (string part in path[drive.Length..].Split(Path.DirectorySeparatorChar))
            Files.CheckSegment(part);
        Files.CheckNoLinks(path);
        Files.RequireSeparate(path, DataRoot);
        return path;
    }

    internal static bool CanElevate(string folder) => Files.IsInside(folder, ProgramRoot);

    internal static string ReadVersion(string folder)
    {
        string marker = Path.Combine(folder, Marker);
        string launcher = Path.Combine(folder, Launcher);
        Files.CheckNoLinks(marker);
        Files.CheckNoLinks(launcher);
        if (!File.Exists(marker) || !File.Exists(launcher))
            return null;
        string version = File.ReadAllText(marker).Trim();
        return ValidVersion(version) ? version : null;
    }

    internal static bool ValidVersion(string version) =>
        version.Length is > 1 and < 48 && version[0] == 'v' && char.IsAsciiDigit(version[1]) &&
        version.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-');

    internal static bool IsManaged(string folder) =>
        ReadVersion(folder) != null && File.Exists(Path.Combine(folder, OwnedFiles));

    internal static string FindInstalled()
    {
        if (ExistingFolder(DefaultFolder) is { } defaultInstall)
            return defaultInstall;
        if (ExistingFolder(LegacyDefaultFolder) is { } legacyInstall)
            return legacyInstall;
        using var uninstall = Registry.CurrentUser.OpenSubKey(UninstallKey);
        foreach (string name in uninstall?.GetSubKeyNames() ?? [])
        {
            if (!name.StartsWith(EntryPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            using var entry = uninstall.OpenSubKey(name);
            if (entry?.GetValue("InstallLocation") is string path && ExistingFolder(path) is { } installed)
                return installed;
        }
        foreach (var special in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
        {
            string directory = Environment.GetFolderPath(special);
            if (directory.Length == 0)
                continue;
            string shortcut = Path.Combine(directory, "NeonLoop.lnk");
            if (!File.Exists(shortcut))
                continue;
            try
            {
                Files.CheckNoLinks(shortcut);
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
                string target = shell.CreateShortcut(shortcut).TargetPath;
                if (string.Equals(Path.GetFileName(target), Launcher, StringComparison.OrdinalIgnoreCase) &&
                    ExistingFolder(Path.GetDirectoryName(target)) is { } installed)
                    return installed;
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or
                IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { }
        }
        return null;
    }

    internal static string ExistingFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            string folder = Validate(path.Trim());
            return IsManaged(folder) ? folder : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    internal static void Register(string folder)
    {
        using var entry = Registry.CurrentUser.CreateSubKey(UninstallKey + "\\" + EntryPrefix + RuntimeId(folder));
        string executable = Path.Combine(folder, Launcher);
        entry.SetValue("DisplayName", "NeonLoop");
        entry.SetValue("DisplayVersion", ReadVersion(folder));
        entry.SetValue("Publisher", "Good Trip Good Tip");
        entry.SetValue("InstallLocation", folder);
        entry.SetValue("DisplayIcon", $"\"{executable}\",0");
        entry.SetValue("UninstallString", $"\"{executable}\" {UninstallArgument}");
        entry.SetValue("NoModify", 1);
        entry.SetValue("NoRepair", 1);
    }

    internal static bool HasRuntimePayload(string effects) =>
        new[] { "bin/Runtime/dxgi.dll", "bin/Runtime/ROMEffects.addon64",
            "bin/MusicPack/ReplacementScore.dll", "bin/MusicPack/RomScoreDSP.dll",
            "CutsceneSetup/GenerateCutscenes.py", "CutsceneSetup/ReleaseAssets.json", "CutsceneSetup/DownloadAssets.json" }
        .All(relative => new FileInfo(Path.Combine(effects, relative)) is { Exists: true, Length: > 0 });

    internal static string RuntimeFolder(string folder)
    {
        using var entry = Registry.CurrentUser.OpenSubKey(UninstallKey + "\\" + EntryPrefix + RuntimeId(folder));
        if (entry?.GetValue("RuntimeLocation") is not string custom)
            return Path.Combine(DataRoot, RuntimeId(folder));
        if (!Path.IsPathFullyQualified(custom) || !custom.EndsWith(RuntimeTail(folder), StringComparison.OrdinalIgnoreCase) ||
            !new DriveInfo(Path.GetPathRoot(custom)!).IsReady)
            throw new IOException("NeonLoop couldn’t verify the private game location. Reconnect the selected drive and retry.");
        custom = Path.GetFullPath(custom);
        Files.RequireSeparate(custom, folder);
        string marker = Path.Combine(custom, "NeonLoop.runtime");
        Files.CheckNoLinks(marker);
        if (Directory.Exists(custom) && Directory.EnumerateFileSystemEntries(custom).Any() &&
            (!File.Exists(marker) || !File.ReadAllText(marker).Equals(folder, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("NeonLoop couldn’t verify the private game location.");
        return custom;
    }

    private static string RuntimeTail(string folder) => Path.Combine("GoodTripGoodTip", "NeonLoop", RuntimeId(folder));

    internal static string SelectRuntimeFolder(string folder, string parent, string steamGame)
    {
        folder = Validate(folder);
        string runtime = Path.GetFullPath(Path.Combine(parent, RuntimeTail(folder)));
        if (Path.GetPathRoot(runtime)!.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Choose a folder on a local drive.");
        Files.RequireSeparate(runtime, folder);
        Files.RequireSeparate(runtime, steamGame);
        Files.CheckNoLinks(runtime);
        string marker = Path.Combine(runtime, "NeonLoop.runtime");
        if (Directory.Exists(runtime) && Directory.EnumerateFileSystemEntries(runtime).Any() &&
            (!File.Exists(marker) || !File.ReadAllText(marker).Equals(folder, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose an unused folder for the private game copy.");
        Directory.CreateDirectory(runtime);
        InstallFiles.WriteText(marker, folder);
        return runtime;
    }

    internal static void RememberRuntimeFolder(string installation, string runtime)
    {
        using var entry = Registry.CurrentUser.CreateSubKey(UninstallKey + "\\" + EntryPrefix + RuntimeId(installation));
        entry.SetValue("RuntimeLocation", runtime);
    }

    private static string RuntimeId(string folder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..24];

    internal static string CheckFirewallTarget(string path, string folder = null)
    {
        folder = Validate(folder ?? Folder);
        if (!IsManaged(folder) || !Path.IsPathFullyQualified(path))
            throw new IOException("The private game location is invalid.");
        path = Path.GetFullPath(path);
        string tail = Path.Combine(RuntimeTail(folder), "Effects");
        if (!path.EndsWith(Path.DirectorySeparatorChar + tail, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The private game location is invalid.");
        Files.CheckNoLinks(path);
        return path;
    }

    internal static string PrepareEffects(string folder, bool refresh = false, string runtimeOverride = null)
    {
        string source = Path.Combine(folder, "Effects");
        if (!IsManaged(folder))
            return source;
        string runtime = runtimeOverride ?? RuntimeFolder(folder);
        string target = Path.Combine(runtime, "Effects");
        string stamp = Path.Combine(runtime, "payload-version");
        string version = ReadVersion(folder);
        Files.CheckNoLinks(stamp);
        if (!refresh && File.Exists(stamp) && File.ReadAllText(stamp) == version && HasRuntimePayload(target))
            return target;
        if (!HasRuntimePayload(source))
            throw new IOException("NeonLoop files are missing. Run NeonLoop Setup again and choose this install location to repair them.");

        foreach (var item in Files.Tree(source, CancellationToken.None))
        {
            if (item.Directory)
                continue;
            string relative = Path.GetRelativePath(source, item.Path);
            string destination = Files.Under(target, relative);
            if (File.Exists(destination) && relative is "Profiles.json" or "ReShade.ini" or "MusicLibrary.txt")
                continue;
            Files.CopyFileAsync(item.Path, destination, CancellationToken.None).GetAwaiter().GetResult();
        }

        Directory.CreateDirectory(runtime);
        InstallFiles.WriteText(stamp, version);
        return target;
    }

    internal static int RunProcess(string executable, bool elevate, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (elevate)
            start.Verb = "runas";
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows couldn’t start NeonLoop setup.");
        process.WaitForExit();
        return process.ExitCode;
    }

    internal static bool WasDeclined(Win32Exception error) => error.NativeErrorCode == 1223;

    internal static void OpenFolder(string folder)
    {
        using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static void WithShortcut(string folder, Environment.SpecialFolder special, bool create)
    {
        string directory = Environment.GetFolderPath(special);
        if (string.IsNullOrEmpty(directory))
            throw new IOException("Windows couldn’t find your shortcut folder.");
        string path = Path.Combine(directory, "NeonLoop.lnk");
        string target = Path.Combine(folder, Launcher);
        if (!create && !File.Exists(path))
            return;
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        if (File.Exists(path) && !string.Equals((string)link.TargetPath, target, StringComparison.OrdinalIgnoreCase))
            return;
        if (!create)
        {
            File.Delete(path);
            return;
        }
        link.TargetPath = target;
        link.WorkingDirectory = folder;
        link.Description = "Play NeonLoop";
        link.IconLocation = target + ",0";
        link.Save();
    }

    internal static void SaveShortcuts(string folder, bool desktop, bool startMenu)
    {
        WithShortcut(folder, Environment.SpecialFolder.DesktopDirectory, desktop);
        WithShortcut(folder, Environment.SpecialFolder.Programs, startMenu);
    }

    internal static void RemovePayload(string folder)
    {
        folder = Validate(folder);
        GameInstaller.InstallFiles.CheckNoLinks(Path.Combine(folder, OwnedFiles));
        if (!IsManaged(folder))
            throw new IOException("Windows couldn’t identify this NeonLoop installation.");
        var files = ReadOwnedFiles(folder);
        string launcher = Path.Combine(folder, Launcher);
        string marker = Path.Combine(folder, Marker);
        foreach (string file in files.Except(new[] { launcher, marker }, StringComparer.OrdinalIgnoreCase))
            File.Delete(file);
        File.Delete(launcher);
        File.Delete(Path.Combine(folder, OwnedFiles));
        File.Delete(marker);
        foreach (string directory in files.SelectMany(file => ParentFolders(file, folder))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length).Append(folder))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
    }

    internal static string[] ReadOwnedFiles(string folder)
    {
        string manifest = Path.Combine(folder, OwnedFiles);
        Files.CheckNoLinks(manifest);
        var files = File.ReadAllLines(manifest).Select(relative => PayloadFile(folder, relative)).ToArray();
        if (!files.Contains(Path.Combine(folder, Launcher), StringComparer.OrdinalIgnoreCase) ||
            !files.Contains(Path.Combine(folder, Marker), StringComparer.OrdinalIgnoreCase))
            throw new IOException("The NeonLoop file list is incomplete.");
        foreach (string file in files)
            Files.CheckNoLinks(file);
        return files;
    }

    internal static string PayloadFile(string folder, string relative)
    {
        string name = relative.Replace('\\', '/');
        if (name is not (Launcher or Marker or "LICENSE" or "HOBBY-TERMS.txt" or "Start Here.txt" or "Soundtrack Credits.md" or "DSP Source.md") &&
            !name.StartsWith("Effects/", StringComparison.Ordinal) &&
            !name.StartsWith("Third-party notices/", StringComparison.Ordinal) &&
            !name.StartsWith("DSP Source/", StringComparison.Ordinal))
            throw new InvalidDataException("The NeonLoop package contains an unexpected file.");
        return Files.Under(folder, name);
    }

    private static IEnumerable<string> ParentFolders(string file, string root)
    {
        for (string directory = Path.GetDirectoryName(file)!; Files.IsInside(directory, root); directory = Path.GetDirectoryName(directory)!)
            yield return directory;
    }

    internal static void BeginRemoval(string folder, bool removeData)
    {
        EffectsLauncher.CheckNoGame();
        folder = Validate(folder);
        string executable = Environment.ProcessPath!;
        string temporary = null;
        if (Files.IsInside(executable, folder))
        {
            Directory.CreateDirectory(RemovalHelpers);
            temporary = Path.Combine(RemovalHelpers, "NeonLoop.Remove." + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(executable, temporary);
            executable = temporary;
        }
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        foreach (string arg in new[] { RemoveArgument, folder, Environment.ProcessId.ToString(), removeData ? "remove-data" : "keep-data" })
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows couldn’t start NeonLoop removal.");
        }
        catch
        {
            if (temporary != null)
                File.Delete(temporary);
            throw;
        }
    }

    internal static void CompleteRemoval(string folder, int parentId, bool removeData)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!parent.WaitForExit(30000))
                throw new IOException("Close NeonLoop and try removing it again.");
        }
        catch (ArgumentException) { }
        folder = Validate(folder);
        using var launchLock = new Mutex(false, EffectsLauncher.LaunchMutex);
        if (!EffectsLauncher.TryAcquireLaunchLock(launchLock))
            throw new IOException("Close NeonLoop and try removing it again.");
        try
        {
            EffectsLauncher.CheckNoGame();
            if (!IsManaged(folder))
                throw new IOException("Windows couldn’t identify this NeonLoop installation.");
            _ = ReadOwnedFiles(folder);
            string runtime = RuntimeFolder(folder);
            if (removeData)
                NetworkPrivacy.RemoveInstalledRules(folder, runtime);
            SaveShortcuts(folder, false, false);
            if (removeData)
            {
                RemoveRuntime(runtime);
                string defaultRuntime = Path.Combine(DataRoot, RuntimeId(folder));
                if (!defaultRuntime.Equals(runtime, StringComparison.OrdinalIgnoreCase))
                    RemoveRuntime(defaultRuntime);
            }
            if (CanElevate(folder))
            {
                if (RunProcess(Environment.ProcessPath!, true, RemoveFilesArgument, folder) != 0)
                    throw new IOException("The NeonLoop files couldn’t be removed. You can run Uninstall again.");
            }
            else
                RemovePayload(folder);
            Registry.CurrentUser.DeleteSubKey(UninstallKey + "\\" + EntryPrefix + RuntimeId(folder), false);
        }
        finally { launchLock.ReleaseMutex(); }
    }

    private static void RemoveRuntime(string runtime)
    {
        Files.CheckNoLinks(runtime);
        if (!Directory.Exists(runtime))
            return;
        string marker = Path.Combine(runtime, "NeonLoop.runtime");
        var entries = Files.Tree(runtime, CancellationToken.None, preserveLinks: true)
            .Where(item => !item.Path.Equals(marker, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Path.Length).ToArray();
        foreach (var item in entries)
        {
            Files.CheckNoLinks(Path.GetDirectoryName(item.Path)!);
            if ((File.GetAttributes(item.Path) & FileAttributes.Directory) != 0)
                Directory.Delete(item.Path);
            else
                File.Delete(item.Path);
        }
        File.Delete(marker);
        Directory.Delete(runtime);
    }

    private static string RemovalHelpers => Path.Combine(DataRoot, "RemovalHelpers");

    internal static void CleanRemovalHelpers()
    {
        try
        {
            Files.CheckNoLinks(RemovalHelpers);
            if (!Directory.Exists(RemovalHelpers))
                return;
            foreach (var file in new DirectoryInfo(RemovalHelpers).EnumerateFiles("NeonLoop.Remove.*.exe"))
                if (file.Name.Length == 52 && Guid.TryParseExact(file.Name.AsSpan(16, 32), "N", out _) &&
                    (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-2))
                    Files.TryDeleteFile(file.FullName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
}

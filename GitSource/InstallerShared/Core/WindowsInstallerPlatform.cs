using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GameInstaller;

public sealed class WindowsInstallerPlatform(InstallerDefinition definition, string? launcherExecutable = null,
    InstallerPackage? package = null) : IInstallerPlatform
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string OwnerFile = ".gameinstaller-launcher.json";
    private const string LauncherName = "ProductLauncher.exe";
    private const string Worker = "--gameinstaller-elevated";
    private const string Handoff = "--gameinstaller-uninstall-worker";
    public const string OpenArgument = "--gameinstaller-open";
    public const string UninstallArgument = "--gameinstaller-uninstall";
    private sealed record LauncherOwner(string ProductId, string Publisher, string Sha256);

    public string? FindInstallation(InstallerDefinition product)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key(product));
        if (key?.GetValue("InstallLocation") is not string directory ||
            key.GetValue("GameInstallerOwner") as string != Identity(product))
            return null;
        return ReadOwner(directory) is { } owner && OwnedBy(product, owner) ? directory : null;
    }

    public async Task RegisterAsync(InstallerRegistration registration, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        using (RegistryKey? previous = Registry.CurrentUser.OpenSubKey(Key(definition)))
            if (previous is not null && !OwnsRegistry(previous, registration))
                throw new IOException("This product already has a Windows entry for a different install location.");

        string launcher = Launcher(registration.Directory);
        if (!Same(registration.Directory, registration.StateDirectory!))
        {
            if (ReadOwner(registration.Directory) is not { } owner || !OwnedBy(definition, owner) || !File.Exists(launcher) ||
                !MatchesRequestedLauncher(owner, launcher))
                await RunElevatedAsync(["deploy"], cancellationToken).ConfigureAwait(false);
        }
        else
            await DeployLauncherAsync(registration.Directory, launcherExecutable, package, cancellationToken).ConfigureAwait(false);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(Key(definition));
        key.SetValue("GameInstallerOwner", Identity(definition));
        key.SetValue("InstallLocation", registration.Directory);
        key.SetValue("DisplayName", registration.Name);
        key.SetValue("DisplayVersion", registration.Version);
        key.SetValue("Publisher", registration.Publisher);
        key.SetValue("DisplayIcon", $"{launcher},0");
        key.SetValue("UninstallString", $"\"{launcher}\" {UninstallArgument} \"{registration.Directory}\" \"{definition.Id}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        SetShortcut(registration, Environment.SpecialFolder.DesktopDirectory, registration.Shortcuts.Desktop);
        SetShortcut(registration, Environment.SpecialFolder.Programs, registration.Shortcuts.StartMenu);
    }

    public void RemoveShortcuts(InstallerRegistration registration)
    {
        ValidateRegistration(registration);
        SetShortcut(registration, Environment.SpecialFolder.DesktopDirectory, false);
        SetShortcut(registration, Environment.SpecialFolder.Programs, false);
    }

    public async Task RemoveRegistrationAsync(InstallerRegistration registration, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        if (!Same(registration.Directory, registration.StateDirectory!) && ReadOwner(registration.Directory) is not null)
            await RunElevatedAsync(["remove"], cancellationToken).ConfigureAwait(false);
        else
            await DeleteLauncherAsync(registration.Directory, cancellationToken).ConfigureAwait(false);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key(definition));
        if (key is not null && OwnsRegistry(key, registration))
            Registry.CurrentUser.DeleteSubKey(Key(definition), throwOnMissingSubKey: false);
    }

    public IReadOnlyList<InstallerFirewallRule> FindPolicyChanges(IReadOnlyList<InstallerFirewallRule> rules, InstallerNetworkChoice choice) =>
        rules.Count == 0 ? [] : ChangedRules(rules, choice);

    public async Task ApplyPolicyAsync(InstallerRegistration registration, IReadOnlyList<InstallerFirewallRule> rules,
        InstallerNetworkChoice choice, CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        if (choice is not (InstallerNetworkChoice.Allow or InstallerNetworkChoice.BlockOutbound))
            throw new ArgumentException("Choose an applicable firewall operation.", nameof(choice));
        if (rules.Count == 0)
            return;
        await RunElevatedAsync(["firewall", choice.ToString(), registration.StateDirectory!, .. rules.Select(rule => rule.Name)], cancellationToken)
            .ConfigureAwait(false);
        if (ChangedRules(rules, choice).Count != 0)
            throw new IOException("Windows did not apply the reviewed firewall changes.");
    }

    public static async Task<int?> RunWorkerAsync(InstallerDefinition definition, string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length < 3 || args[0] != Worker)
            return null;
        if (args[1] != definition.Id)
            return 2;
        args = args[1..];
        try
        {
            var platform = new WindowsInstallerPlatform(definition);
            if (args.Length == 2 && args[1] is "deploy" or "remove" && definition.Managed?.AllowProgramFiles == true)
            {
                string destination = InstallerService.GetManagedProductDirectory(definition, programFiles: true);
                if (args[1] == "deploy")
                {
                    string app = Environment.ProcessPath ?? throw new InvalidOperationException("The setup executable is unavailable.");
                    await platform.DeployLauncherAsync(destination, null, InstallerPackage.Open(app), cancellationToken).ConfigureAwait(false);
                }
                else
                    await platform.DeleteLauncherAsync(destination, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            if (args.Length >= 5 && args[1] == "firewall" &&
                Enum.TryParse(args[2], out InstallerNetworkChoice choice) && choice is InstallerNetworkChoice.Allow or InstallerNetworkChoice.BlockOutbound)
            {
                platform.ApplyOwnedFirewall(Path.GetFullPath(args[3]), args[4..], choice);
                return 0;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return error is UnauthorizedAccessException ? 5 : 1;
        }
        return 2;
    }

    public static async Task<bool> PrepareUninstallHostAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args is [Handoff, _, _, string parent] && int.TryParse(parent, out int processId))
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (Same(process.MainModule?.FileName ?? "", Launcher(args[1])))
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException) { }
            return false;
        }
        if (args is not [UninstallArgument, string directory, string productId] || Environment.ProcessPath is not { } executable || !Same(executable, Launcher(directory)))
            return false;
        string temporary = Path.Combine(Path.GetTempPath(), "GameInstaller", $"uninstall-{Guid.NewGuid():N}", LauncherName);
        await InstallFiles.CopyFileAsync(executable, temporary, cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo(temporary) { UseShellExecute = false };
        start.ArgumentList.Add(Handoff);
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(productId);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using Process? child = Process.Start(start);
        return child is not null;
    }

    public static bool TryGetLaunchDirectory(string[] args, out string? directory, out bool uninstall)
    {
        uninstall = args.Length >= 2 && args[0] is UninstallArgument or Handoff;
        directory = args.Length >= 2 && (uninstall || args[0] == OpenArgument) ? args[1] : null;
        return directory is not null;
    }

    public static string? GetCommandProductId(string[] args) => args switch
    {
        [OpenArgument or UninstallArgument or Handoff, _, string id, ..] => id,
        [Worker, string id, ..] => id,
        _ => null
    };

    public static void CleanupUninstallHost()
    {
        string? executable = Environment.ProcessPath;
        string? folder = Path.GetDirectoryName(executable);
        if (executable is null || folder is null || !Same(Path.GetDirectoryName(folder)!, Path.Combine(Path.GetTempPath(), "GameInstaller")) ||
            Path.GetFileName(executable) != LauncherName || !Path.GetFileName(folder).StartsWith("uninstall-", StringComparison.Ordinal))
            return;
        string literal = executable.Replace("'", "''");
        string script = $"Wait-Process -Id {Environment.ProcessId} -Timeout 20 -ErrorAction SilentlyContinue\nRemove-Item -LiteralPath '{literal}' -ErrorAction SilentlyContinue\nif ((Get-ChildItem -LiteralPath ('{literal}' | Split-Path) -Force | Measure-Object).Count -eq 0) {{ Remove-Item -LiteralPath ('{literal}' | Split-Path) -ErrorAction SilentlyContinue }}";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { CreateNoWindow = true, UseShellExecute = false };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using Process? cleanup = Process.Start(start);
    }

    internal static InstallerFirewallRule MakeRule(InstallerDefinition product, string executable)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())))[..20];
        string group = $"{product.Managed!.Publisher} {product.Id} Network Privacy";
        return new InstallerFirewallRule($"{product.Id} outbound block {key}", group, executable);
    }

    private void ApplyOwnedFirewall(string stateDirectory, string[] names, InstallerNetworkChoice choice)
    {
        if (definition.Mode != InstallMode.PrivateGameCopy || definition.Managed is not { PrivateNetworkPrograms.Count: > 0 } managed)
            throw new InvalidOperationException("The product has no private network executables.");
        RuntimeLocation? recorded = InstallStore.ReadRuntime(stateDirectory);
        var store = new InstallStore(stateDirectory, definition.Id, recorded?.Directory);
        if (!store.IsManagedOwner())
            throw new InvalidDataException("The setup state does not belong to this product.");
        if (recorded is not null)
            store.ValidateRuntimeOwner();
        WindowsInstallState state = store.ReadWindows() ?? throw new InvalidOperationException("The setup operation is missing.");
        InstallerRegistration registration = state.Registration ?? throw new InvalidOperationException("The product registration is missing.");
        ValidateRegistration(registration);
        if (!Same(stateDirectory, registration.StateDirectory!) || !Same(store.Runtime, registration.RuntimeDirectory!))
            throw new InvalidDataException("The private copy no longer matches this installation.");
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            InstallerFirewallRule rule = state.Rules.Single(item => item.Name == name);
            string installs = Path.Combine(store.Runtime, "Installs");
            string relative = Path.GetRelativePath(installs, rule.Program);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, 3);
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _) || parts[1] != "Content" || rule != MakeRule(definition, rule.Program) ||
                !managed.PrivateNetworkPrograms.Any(path => Same(InstallFiles.Under(store.Content(parts[0]), path), rule.Program)))
                throw new InvalidDataException("The firewall target is outside the configured private copy.");
            InstallFiles.CheckNoLinks(rule.Program);
            if (choice == InstallerNetworkChoice.BlockOutbound)
            {
                if (store.ReadReceipt(parts[0]) is not { SourceDirectory: { } original } receipt ||
                    !receipt.Files.Any(file => Same(InstallFiles.Under(store.Content(parts[0]), file.Path), rule.Program)) || !File.Exists(rule.Program))
                    throw new InvalidDataException("The private executable is no longer owned by this install.");
                InstallFiles.RequireSeparate(original, store.Runtime);
            }
            ApplyOneRule(rule, choice);
        }
    }

    private static List<InstallerFirewallRule> ChangedRules(IReadOnlyList<InstallerFirewallRule> rules, InstallerNetworkChoice choice)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        if (choice == InstallerNetworkChoice.BlockOutbound)
            foreach (int profile in new[] { 1, 2, 4 })
                if ((policy.CurrentProfileTypes & profile) != 0 && !policy.FirewallEnabled[profile])
                    throw new InvalidOperationException("Enable Windows Firewall on the active profile before using the outbound block.");
        var changes = new List<InstallerFirewallRule>();
        foreach (InstallerFirewallRule rule in rules)
        {
            object? existing = FindRule(policy.Rules, rule);
            if (choice == InstallerNetworkChoice.Allow ? existing is not null : existing is null || !IsBlockRule((dynamic)existing))
                changes.Add(rule);
        }
        return changes;
    }

    private static bool IsBlockRule(dynamic rule) => rule.Enabled && rule.Direction == 2 && rule.Action == 0 &&
        rule.Profiles == int.MaxValue && rule.Protocol == 256;

    private static void ApplyOneRule(InstallerFirewallRule target, InstallerNetworkChoice choice)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        dynamic rules = policy.Rules;
        dynamic? current = FindRule(rules, target);
        if (choice == InstallerNetworkChoice.Allow)
        {
            if (current is not null)
                rules.Remove(target.Name);
            return;
        }
        dynamic rule = current ?? CreateCom("HNetCfg.FWRule");
        rule.Name = target.Name;
        rule.Grouping = target.Group;
        rule.Description = target.Description;
        rule.ApplicationName = target.Program;
        rule.Protocol = 256;
        rule.Direction = 2;
        rule.Action = 0;
        rule.Profiles = int.MaxValue;
        rule.Enabled = true;
        if (current is null)
            rules.Add(rule);
    }

    private static object? FindRule(dynamic rules, InstallerFirewallRule target)
    {
        foreach (dynamic rule in rules)
        {
            if (!string.Equals((string)rule.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if ((string)rule.Grouping != target.Group || !Same((string)rule.ApplicationName, target.Program))
                throw new InvalidDataException("An unrelated Windows Firewall rule uses the expected name.");
            return rule;
        }
        return null;
    }

    private async Task DeployLauncherAsync(string directory, string? source, InstallerPackage? bundled, CancellationToken token)
    {
        string target = Launcher(directory);
        InstallFiles.CheckNoLinks(target);
        LauncherOwner? owner = ReadOwner(directory);
        if ((owner is not null && !OwnedBy(definition, owner)) || (File.Exists(target) && owner is null))
            throw new IOException("The destination already contains an unrelated launcher.");
        string executable = source ?? Environment.ProcessPath ?? throw new InvalidOperationException("The setup executable is unavailable.");
        if (source is null && bundled?.HasLauncher != true && File.Exists(Path.ChangeExtension(executable, ".dll")))
            throw new InvalidOperationException("Publish a self-contained launcher before registering it with Windows.");
        if (Same(executable, target) || (bundled?.HasLauncher != true && owner is not null && File.Exists(target) &&
            owner.Sha256 == await InstallFiles.HashAsync(executable, token).ConfigureAwait(false) && owner.Sha256 == await InstallFiles.HashAsync(target, token).ConfigureAwait(false)))
            return;
        Directory.CreateDirectory(directory);
        string temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (bundled?.HasLauncher == true)
                await bundled.ExtractLauncherAsync(temporary, token).ConfigureAwait(false);
            else
                await InstallFiles.CopyFileAsync(executable, temporary, token).ConfigureAwait(false);
            string hash = await InstallFiles.HashAsync(temporary, token).ConfigureAwait(false);
            if (!File.Exists(target) || hash != await InstallFiles.HashAsync(target, token).ConfigureAwait(false))
                File.Move(temporary, target, overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(directory, OwnerFile),
                JsonSerializer.Serialize(new LauncherOwner(definition.Id, definition.Managed!.Publisher, hash)), token).ConfigureAwait(false);
        }
        finally { InstallFiles.TryDeleteFile(temporary); }
    }

    private async Task DeleteLauncherAsync(string directory, CancellationToken token)
    {
        string target = Launcher(directory);
        InstallFiles.CheckNoLinks(target);
        if (ReadOwner(directory) is not { } owner || !OwnedBy(definition, owner))
            return;
        if (File.Exists(target) && owner.Sha256 == await InstallFiles.HashAsync(target, token).ConfigureAwait(false))
            File.Delete(target);
        File.Delete(Path.Combine(directory, OwnerFile));
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private bool MatchesRequestedLauncher(LauncherOwner owner, string launcher) =>
        package?.HasLauncher == true ? package.MatchesLauncher(owner.Sha256) : Same(Environment.ProcessPath ?? "", launcher);

    private static void SetShortcut(InstallerRegistration registration, Environment.SpecialFolder location, bool enabled)
    {
        string folder = Environment.GetFolderPath(location);
        if (folder.Length == 0)
            throw new IOException("Windows did not provide the requested shortcut folder.");
        string display = string.Concat(registration.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).TrimEnd(' ', '.');
        string path = InstallFiles.Under(folder, display + ".lnk");
        InstallFiles.CheckNoLinks(path);
        if (!enabled && !File.Exists(path))
            return;
        dynamic shell = CreateCom("WScript.Shell");
        dynamic shortcut = shell.CreateShortcut(path);
        string launcher = Launcher(registration.Directory);
        string arguments = $"{OpenArgument} \"{registration.Directory}\" \"{registration.ProductId}\"";
        if (File.Exists(path) && (!Same((string)shortcut.TargetPath, launcher) || (string)shortcut.Arguments != arguments))
        {
            if (enabled)
                throw new IOException($"Another shortcut already uses {display}.");
            return;
        }
        if (!enabled)
        {
            File.Delete(path);
            return;
        }
        shortcut.TargetPath = launcher;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = registration.Directory;
        shortcut.Description = registration.Name;
        shortcut.IconLocation = $"{launcher},0";
        shortcut.Save();
    }

    private async Task RunElevatedAsync(string[] arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The installer executable is unavailable."))
        { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory };
        start.ArgumentList.Add(Worker);
        start.ArgumentList.Add(definition.Id);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the setup helper.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"The Windows setup helper stopped with code {process.ExitCode}. Retry after checking permissions and setup details.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Windows permission was declined. Setup can retry.", error, token);
        }
    }

    private void ValidateRegistration(InstallerRegistration registration)
    {
        if (registration.ProductId != definition.Id || registration.Publisher != definition.Managed?.Publisher ||
            registration.StateDirectory is null || registration.RuntimeDirectory is null || !Path.IsPathFullyQualified(registration.Directory))
            throw new InvalidDataException("This Windows operation belongs to a different installation.");
        InstallFiles.CheckNoLinks(registration.Directory);
    }

    private bool OwnsRegistry(RegistryKey key, InstallerRegistration registration) =>
        key.GetValue("GameInstallerOwner") as string == Identity(definition) && key.GetValue("InstallLocation") is string path && Same(path, registration.Directory);

    private static string Key(InstallerDefinition product) => $@"{UninstallKey}\GameInstaller-{Identity(product)}";

    private static string Identity(InstallerDefinition product) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{product.Managed?.Publisher}|{product.Id}")))[..24];

    private static bool OwnedBy(InstallerDefinition product, LauncherOwner owner) => owner.ProductId == product.Id && owner.Publisher == product.Managed?.Publisher;

    private static LauncherOwner? ReadOwner(string directory)
    {
        string marker = Path.Combine(directory, OwnerFile);
        InstallFiles.CheckNoLinks(marker);
        return File.Exists(marker) ? JsonSerializer.Deserialize<LauncherOwner>(File.ReadAllText(marker)) : null;
    }

    private static string Launcher(string directory) => Path.Combine(directory, LauncherName);

    private static bool Same(string first, string second) => Path.TrimEndingDirectorySeparator(first).Equals(Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);

    private static object CreateCom(string programId) => Activator.CreateInstance(Type.GetTypeFromProgID(programId, throwOnError: true)!)!;
}

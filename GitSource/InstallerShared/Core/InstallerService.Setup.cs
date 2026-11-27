using System.Security.Cryptography;
using System.Text;

namespace GameInstaller;

public sealed partial class InstallerService
{
    private readonly string productDirectory;
    private readonly InstallerPackage? bundle;
    private readonly string loosePackage;
    private readonly PreparedPackage? retainedPackage;
    private readonly IInstallerPlatform? platform;

    public string ProductDirectory => productDirectory;
    public string RuntimeDirectory => store.Runtime;
    public string LogDirectory => Path.Combine(store.Root, "Logs");

    public static string GetManagedProductDirectory(InstallerDefinition definition, bool programFiles = false)
    {
        ManagedInstaller managed = definition.Managed ?? throw new ArgumentException("Managed setup is disabled.", nameof(definition));
        string parent = programFiles ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        return InstallFiles.Under(InstallFiles.Under(parent, managed.Publisher), definition.Id);
    }

    public static string? FindManagedInstallation(InstallerDefinition definition, IInstallerPlatform? platform = null)
    {
        if (definition.Managed is null)
            return null;
        string? previous = (platform ?? new WindowsInstallerPlatform(definition)).FindInstallation(definition);
        if (previous is not null)
            return previous;
        string current = GetManagedProductDirectory(definition);
        var state = new InstallStore(current, definition.Id);
        if (state.ReadState().Active is not null || InstallStore.ReadRuntime(current) is not null)
            return current;
        string legacy = GetProductDirectory(definition.Id);
        return new InstallStore(legacy, definition.Id).ReadState().Active is not null ? legacy : null;
    }

    public async Task<InstallReview> PreviewReinstallAsync(InstallRequest request, CancellationToken cancellationToken = default)
    {
        InstallReceipt active = store.ReadReceipt(store.ReadState().Active) ?? throw new InvalidOperationException("There is no active install to reinstall.");
        IReadOnlyList<string> existing = active.Components.Where(id => definition.Components.Any(component =>
            component.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
        return await PreviewAsync(request with { Components = request.Components ?? existing, Intent = InstallIntent.Reinstall }, cancellationToken)
            .ConfigureAwait(false);
    }

    public IReadOnlyDictionary<string, bool> ReadSettings()
    {
        Dictionary<string, bool>? stored = store.ReadSettings();
        return definition.Settings.ToDictionary(setting => setting.Id,
            setting => stored?.TryGetValue(setting.Id, out bool value) == true ? value : setting.Recommended);
    }

    public void SaveSettings(IReadOnlyDictionary<string, bool> values)
    {
        using FileStream operation = store.OpenLock();
        if (store.ReadState().Active is null)
            throw new InvalidOperationException("Complete setup before saving your choices.");
        var settings = new Dictionary<string, bool>(ReadSettings());
        foreach (var value in values)
        {
            if (!settings.ContainsKey(value.Key))
                throw new ArgumentException($"Unknown setting: {value.Key}.", nameof(values));
            settings[value.Key] = value.Value;
        }
        store.WriteSettings(settings);
    }

    public void AcceptLimitedPlay()
    {
        using FileStream operation = store.OpenLock();
        if (definition.Managed?.AllowLimitedPlay != true || store.ReadSession() is not { Complete: false } session ||
            store.ReadReceipt(store.ReadState().Active) is not { } active || !IsUsable(active))
            throw new InvalidOperationException("A playable base installation is unavailable.");
        store.WriteSession(session with { LimitedPlayAccepted = true });
    }

    public Task<RemoveReview> PreviewRemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallReviewAction[] steps =
        [
            new("Shortcuts and firewall rules", "Remove this installation’s shortcuts and firewall rules it added for its private game copy."),
            new("Downloads and settings", "Remove setup downloads, generated caches, helper packages, and the installer’s saved options. Keep saves and setup logs."),
            new("Installed files", "Remove files recorded by this installer and its Windows Apps entry. Keep other files and the original game.")
        ];
        return Task.FromResult(new RemoveReview(this, RemovalIdentity(), steps));
    }

    public Task<RemoveResult> RemoveReviewedAsync(RemoveReview review, RemovalApproval approval,
        IInstallerInteraction? interaction = null, IProgress<InstallerProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.Service != this || approval != RemovalApproval.All)
            throw new InvalidOperationException("Confirm all three steps of the current removal review.");
        return RemoveCoreAsync(review, interaction, progress, cancellationToken);
    }

    public async Task ApplyNetworkChoiceAsync(InstallerNetworkChoice choice, IInstallerSetupInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        if (choice is not (InstallerNetworkChoice.Allow or InstallerNetworkChoice.BlockOutbound) || !HasPrivateNetwork)
            throw new ArgumentException("Choose a supported private-copy network option.", nameof(choice));
        using FileStream operation = store.OpenLock();
        EnsureNoGame();
        InstallReceipt receipt = store.ReadReceipt(store.ReadState().Active) ?? throw new InvalidOperationException("Install the private game first.");
        await UpdateNetworkAsync(receipt, choice, interaction, cancellationToken).ConfigureAwait(false);
    }

    private bool HasPrivateNetwork => definition.Mode == InstallMode.PrivateGameCopy && definition.Managed?.PrivateNetworkPrograms.Count > 0;

    private static string SafeLocalDirectory(string path, bool allowDriveRoot = false)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (path.StartsWith('\\') || Path.GetPathRoot(path) is not { Length: 3 } drive || new DriveInfo(drive).DriveType == DriveType.Network)
            throw new ArgumentException("Choose a folder on a local Windows drive.");
        if (!allowDriveRoot && SamePath(path, drive))
            throw new ArgumentException("Choose a product folder below the drive root.");
        InstallFiles.CheckNoLinks(path);
        return path;
    }

    private static string DirectoryKey(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    private string StateDirectory(string destination)
    {
        if (definition.Managed is null)
            return destination;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!InstallFiles.IsInside(destination, programFiles) && (x86.Length == 0 || !InstallFiles.IsInside(destination, x86)))
            return destination;
        if (!definition.Managed.AllowProgramFiles || !SamePath(destination, GetManagedProductDirectory(definition, programFiles: true)))
            throw new InvalidOperationException("Choose the product’s Program Files location or a writable folder on a local drive.");
        return Path.Combine(DefaultInstallParent, "SystemInstallations", definition.Id, DirectoryKey(destination));
    }

    private InstallerRegistration Registration(InstallerShortcuts shortcuts) => new(definition.Id, definition.Managed!.Publisher, definition.Name,
        definition.Version, productDirectory, shortcuts) { StateDirectory = store.Root, RuntimeDirectory = store.Runtime };

    private async Task RegisterAsync(InstallRequest request, IInstallerInteraction? interaction, CancellationToken token)
    {
        if (definition.Managed is not { } managed || platform is null)
            return;
        WindowsInstallState previous = store.ReadWindows() ?? new WindowsInstallState(null, InstallerNetworkChoice.Unchanged, []);
        InstallerRegistration registration = Registration(request.Shortcuts ?? previous.Registration?.Shortcuts ?? managed.DefaultShortcuts);
        if (!SamePath(store.Root, productDirectory) &&
            !await ApproveChangesAsync(interaction, [new("Install launcher for this PC", $"Windows permission is needed to write the launcher in {productDirectory}.")], token)
                .ConfigureAwait(false))
            throw new OperationCanceledException("Program Files setup was declined. Choose a writable location or retry.", token);
        store.WriteWindows(previous with { Registration = registration });
        await platform.RegisterAsync(registration, token).ConfigureAwait(false);
    }

    private async Task CompleteSetupAsync(InstallReceipt receipt, InstallRequest request, IInstallerInteraction? interaction, bool complete,
        CancellationToken token)
    {
        if (definition.Managed is null)
            return;
        await RegisterAsync(request, interaction, token).ConfigureAwait(false);
        if (request.Mode == SetupMode.Custom && request.Network != InstallerNetworkChoice.Unchanged)
            await UpdateNetworkAsync(receipt, request.Network, interaction, token).ConfigureAwait(false);
        store.WriteSession(new SetupSession(request, complete));
    }

    private void EnsureFreeForCopy(ResolvedGame? source, CancellationToken token)
    {
        if (definition.Mode != InstallMode.PrivateGameCopy || source is null || FreeBytes() is not long free)
            return;
        long required = InstallFiles.Tree(source.Directory, token).Where(item => !item.Directory).Sum(item => new FileInfo(item.Path).Length);
        if (required > free)
            throw new IOException($"The runtime drive needs {required:N0} free bytes for the private game copy. It has {free:N0} bytes.");
    }

    private async Task CheckRetainedPackageAsync(PreparedPackage retained, CancellationToken token)
    {
        if (retained.Files.Length == 0)
            return;
        if (!Directory.Exists(package))
            throw new IOException("The saved setup files are missing. Open the matching setup executable to continue.");
        var expected = new HashSet<string>(retained.Files.Select(file => InstallFiles.Under(package, file.Path)), StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(InstallFiles.Tree(package, token).Where(file => !file.Directory).Select(file => file.Path)))
            throw new InvalidDataException("The saved setup folder changed. Open the matching setup executable to continue.");
        foreach (OwnedFile file in retained.Files)
            if (!await FileMatchesAsync(InstallFiles.Under(package, file.Path), file, token).ConfigureAwait(false))
                throw new InvalidDataException($"The saved setup file {file.Path} changed. Open the matching setup executable to continue.");
    }

    private static void RemoveSkipped(List<InstallerComponent> remaining, InstallerComponent failed, string reason,
        List<SkippedComponent> skipped, InstallerEvents events)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { failed.Id };
        foreach (InstallerComponent item in remaining.ToArray())
        {
            if (item != failed && !item.Requires.Any(missing.Contains))
                continue;
            remaining.Remove(item);
            missing.Add(item.Id);
            string detail = item == failed ? reason : $"Requires {string.Join(", ", item.Requires.Where(missing.Contains))}.";
            skipped.Add(new SkippedComponent(item.Id, item.Name, detail));
            events.Log("Install", $"Skipped {item.Name}: {detail}", InstallerLogLevel.Warning);
        }
    }

    private static Task RunCheckpointedStepAsync(InstallerStep step, InstallerStepContext context, string route,
        Func<InstallerStepContext, CancellationToken, Task> work, CancellationToken token)
    {
        context = context.WithCheckpointRoute(route);
        return step.CheckpointFiles.Count == 0 ? work(context, token) :
            context.RunCheckpointAsync("completed-step", step.CheckpointFiles, cancellation => work(context, cancellation), token);
    }

    private static void ValidateSetupDefinition(InstallerDefinition definition, HashSet<string> componentIds)
    {
        if (definition.Managed is { } managed)
        {
            InstallFiles.CheckSegment(managed.Publisher);
            if (managed.PrivateNetworkPrograms.Count > 0 && definition.Mode != InstallMode.PrivateGameCopy)
                throw new ArgumentException("Firewall rules require a private game copy.");
            foreach (string program in managed.PrivateNetworkPrograms)
                if (string.IsNullOrWhiteSpace(program) || !Path.GetExtension(program).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Private network programs must be relative EXE paths.");
        }
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstallerComponentGroup group in definition.ComponentGroups)
            if (!groups.Add(group.Id) || string.IsNullOrWhiteSpace(group.Id) || string.IsNullOrWhiteSpace(group.Name))
                throw new ArgumentException("Component group IDs and labels must be defined and IDs must be unique.");
        foreach (InstallerComponent component in definition.Components)
            if ((component.GroupId is { } group && !groups.Contains(group)) || component.Requires.Any(id => !componentIds.Contains(id)))
                throw new ArgumentException($"{component.Name} has an unknown group or dependency.");
        var settings = new HashSet<string>(StringComparer.Ordinal);
        foreach (InstallerSetting item in definition.Settings)
            if (!settings.Add(item.Id) || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                throw new ArgumentException("Setting IDs and labels must be defined and IDs must be unique.");
    }

    private async Task UpdateNetworkAsync(InstallReceipt receipt, InstallerNetworkChoice choice, IInstallerInteraction? interaction, CancellationToken token)
    {
        if (choice == InstallerNetworkChoice.Unchanged || platform is null || !HasPrivateNetwork)
            return;
        WindowsInstallState previous = store.ReadWindows() ?? new WindowsInstallState(null, InstallerNetworkChoice.Unchanged, []);
        InstallerFirewallRule[] target = choice == InstallerNetworkChoice.BlockOutbound ? RulesFor(receipt) : [];
        InstallerFirewallRule[] retired = previous.Rules.Except(target).ToArray();
        var changes = new List<(InstallerNetworkChoice Choice, IReadOnlyList<InstallerFirewallRule> Rules)>();
        if (retired.Length > 0)
            changes.Add((InstallerNetworkChoice.Allow, platform.FindPolicyChanges(retired, InstallerNetworkChoice.Allow)));
        if (target.Length > 0)
            changes.Add((InstallerNetworkChoice.BlockOutbound, platform.FindPolicyChanges(target, InstallerNetworkChoice.BlockOutbound)));
        InstallReviewAction[] review = changes.SelectMany(change => change.Rules.Select(rule => new InstallReviewAction(
            change.Choice == InstallerNetworkChoice.Allow ? "Remove outbound block" : "Add outbound block", rule.Description))).ToArray();
        if (review.Length > 0 && !await ApproveChangesAsync(interaction, review, token).ConfigureAwait(false))
            throw new OperationCanceledException("Network changes were declined. Existing rules were kept.", token);
        InstallerRegistration registration = previous.Registration ?? Registration(new InstallerShortcuts(false, false));
        if (review.Length > 0)
        {
            store.WriteWindows(previous with { Rules = previous.Rules.Union(target).ToArray() });
            foreach (var change in changes.Where(change => change.Rules.Count > 0))
                await platform.ApplyPolicyAsync(registration, change.Rules, change.Choice, token).ConfigureAwait(false);
        }
        store.WriteWindows(previous with { Network = choice, Rules = target });
    }

    private InstallerFirewallRule[] RulesFor(InstallReceipt receipt)
    {
        string root = store.Content(receipt.Id);
        var rules = new List<InstallerFirewallRule>();
        foreach (string program in definition.Managed!.PrivateNetworkPrograms)
        {
            string fullPath = InstallFiles.Under(root, program);
            InstallFiles.CheckNoLinks(fullPath);
            if (!File.Exists(fullPath) || !receipt.Files.Any(file => SamePath(InstallFiles.Under(root, file.Path), fullPath)))
                continue;
            rules.Add(WindowsInstallerPlatform.MakeRule(definition, fullPath));
        }
        if (rules.Count == 0)
            throw new InvalidOperationException("The private copy has no configured network targets.");
        return rules.ToArray();
    }

    private static Task<bool> ApproveChangesAsync(IInstallerInteraction? interaction, IReadOnlyList<InstallReviewAction> changes, CancellationToken token) =>
        interaction is IInstallerSetupInteraction setup ? setup.ApprovePlatformChangesAsync(changes, token) : Task.FromResult(false);

    private string RemovalIdentity() => $"{store.ReadState().Active}|{string.Join(',', store.Generations().Order())}|{store.ReadWindows()?.Network}";

    private async Task RemoveSetupIntegrationAsync(IInstallerInteraction? interaction, InstallerEvents events, CancellationToken token)
    {
        if (platform is null || store.ReadWindows() is not { Registration: { } registration } owned)
            return;
        events.Report("Removing shortcuts and private-copy firewall rules");
        if (owned.Rules.Length > 0)
        {
            IReadOnlyList<InstallerFirewallRule> changes = platform.FindPolicyChanges(owned.Rules, InstallerNetworkChoice.Allow);
            if (changes.Count > 0 && !await ApproveChangesAsync(interaction,
                changes.Select(rule => new InstallReviewAction("Remove outbound block", rule.Description)).ToArray(), token).ConfigureAwait(false))
                throw new OperationCanceledException("Firewall removal was declined. Retry uninstall to finish.", token);
            if (changes.Count > 0)
                await platform.ApplyPolicyAsync(registration, changes, InstallerNetworkChoice.Allow, token).ConfigureAwait(false);
            store.WriteWindows(owned with { Rules = [], Network = InstallerNetworkChoice.Unchanged });
        }
        platform.RemoveShortcuts(registration);
    }

    private async Task FinishSetupRemovalAsync(IInstallerInteraction? interaction, CancellationToken token)
    {
        if (platform is not null && store.ReadWindows() is { Registration: { } registration })
        {
            if (!SamePath(store.Root, productDirectory) && !await ApproveChangesAsync(interaction,
                [new("Remove Program Files launcher", $"Windows permission is needed to remove the launcher in {productDirectory}.")], token).ConfigureAwait(false))
                throw new OperationCanceledException("Windows launcher removal was declined. Retry uninstall to finish.", token);
            await platform.RemoveRegistrationAsync(registration, token).ConfigureAwait(false);
        }
        store.FinishManagedRemoval();
    }
}

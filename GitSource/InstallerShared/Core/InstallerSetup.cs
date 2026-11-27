namespace GameInstaller;

public enum SetupMode { Easy, Custom }

public enum InstallIntent { Install, Reinstall, AddComponents }

public enum InstallerNetworkChoice { Unchanged, Allow, BlockOutbound }

public sealed record InstallerComponentGroup(string Id, string Name, bool Exclusive = false);

public sealed record InstallerSetting(string Id, string Group, string Name, bool Recommended = true);

public sealed record InstallerShortcuts(bool Desktop = true, bool StartMenu = true);

public sealed record ManagedInstaller(string Publisher)
{
    public string? Subtitle { get; init; }
    public bool AllowLimitedPlay { get; init; }
    public bool AllowProgramFiles { get; init; }
    public InstallerShortcuts DefaultShortcuts { get; init; } = new();
    public IReadOnlyList<string> PrivateNetworkPrograms { get; init; } = [];
}

public sealed record InstallerServiceOptions
{
    public string? ProductDirectory { get; init; }
    public string? RuntimeParent { get; init; }
    public InstallerPackage? Package { get; init; }
    public IInstallerPlatform? Platform { get; init; }
    public string? LauncherExecutable { get; init; }
    public bool UseInstalledPayload { get; init; }
}

public sealed record InstallerCapabilityQuestion(string Name, IReadOnlyList<InstallReviewAsset> Assets);

public interface IInstallerSetupInteraction : IInstallerInteraction
{
    Task<bool> ApproveCapabilityAsync(InstallerCapabilityQuestion question, CancellationToken cancellationToken);

    Task<bool> ApprovePlatformChangesAsync(IReadOnlyList<InstallReviewAction> changes, CancellationToken cancellationToken);
}

public sealed record InstallerRegistration(string ProductId, string Publisher, string Name, string Version, string Directory,
    InstallerShortcuts Shortcuts)
{
    public string? StateDirectory { get; init; }
    public string? RuntimeDirectory { get; init; }
}

public sealed record InstallerFirewallRule(string Name, string Group, string Program)
{
    public string Description => $"{Name}: block outbound traffic on all network profiles for {Program}";
}

public interface IInstallerPlatform
{
    string? FindInstallation(InstallerDefinition definition);
    Task RegisterAsync(InstallerRegistration registration, CancellationToken cancellationToken);
    void RemoveShortcuts(InstallerRegistration registration);
    Task RemoveRegistrationAsync(InstallerRegistration registration, CancellationToken cancellationToken);
    IReadOnlyList<InstallerFirewallRule> FindPolicyChanges(IReadOnlyList<InstallerFirewallRule> rules, InstallerNetworkChoice choice);
    Task ApplyPolicyAsync(InstallerRegistration registration, IReadOnlyList<InstallerFirewallRule> rules,
        InstallerNetworkChoice choice, CancellationToken cancellationToken);
}

[Flags]
public enum RemovalApproval
{
    ShortcutsAndFirewall = 1,
    DownloadsAndSettings = 2,
    InstalledFiles = 4,
    All = ShortcutsAndFirewall | DownloadsAndSettings | InstalledFiles
}

public sealed class RemoveReview
{
    internal RemoveReview(InstallerService service, string? installedIdentity, InstallReviewAction[] steps)
    {
        Service = service;
        InstalledIdentity = installedIdentity;
        Steps = Array.AsReadOnly(steps);
    }

    internal InstallerService Service { get; }
    internal string? InstalledIdentity { get; }
    public IReadOnlyList<InstallReviewAction> Steps { get; }
}

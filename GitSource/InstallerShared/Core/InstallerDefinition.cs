namespace GameInstaller;

public enum InstallMode { Standalone, PrivateGameCopy, LauncherOnly }

public enum PayloadKind { File, Directory, Zip }

public enum LaunchTarget { Installed, SourceGame }

public sealed record InstallerDefinition(string Id, string Name, string Version, InstallMode Mode)
{
    public GameSource? Source { get; init; }
    public InstallContent Required { get; init; } = new();
    public IReadOnlyList<InstallerComponent> Components { get; init; } = [];
    public LaunchDefinition? Launch { get; init; }
    public IReadOnlyList<string> BlockingProcessNames { get; init; } = [];
    public string? NetworkDisclosure { get; init; }
    public InstallerNotice? Notice { get; init; }
    public ManagedInstaller? Managed { get; init; }
    public IReadOnlyList<InstallerComponentGroup> ComponentGroups { get; init; } = [];
    public IReadOnlyList<InstallerSetting> Settings { get; init; } = [];
}

public sealed record GameSource(string Executable)
{
    public string? DisplayName { get; init; }
    public uint? SteamAppId { get; init; }
    public string? SteamDirectory { get; init; }
    public string CompatibilityNotice { get; init; } = "Testing was limited to the developer’s Windows 11 PC and the supported game release.";
    public IReadOnlyList<string> RequiredFiles { get; init; } = [];
    public IReadOnlyList<string> SupportedBuildIds { get; init; } = [];
    public IReadOnlyDictionary<string, string> FileHashes { get; init; } = new Dictionary<string, string>();
}

public sealed record InstallContent
{
    public IReadOnlyList<InstallerAsset> Assets { get; init; } = [];
    public IReadOnlyList<InstallerPayload> Payloads { get; init; } = [];
    public IReadOnlyList<InstallerStep> Steps { get; init; } = [];
    public long MinimumFreeBytes { get; init; }
}

public sealed record InstallerComponent(string Id, string Name, InstallContent Content)
{
    public string? Description { get; init; }
    public bool Recommended { get; init; } = true;
    public bool AllowSkip { get; init; }
    public string? GroupId { get; init; }
    public IReadOnlyList<string> Requires { get; init; } = [];
}

public sealed record InstallerAsset(string Id, string FileName, long Bytes, string Sha256)
{
    public Uri? Url { get; init; }
    public IReadOnlyList<string> RedirectHosts { get; init; } = [];
    public IReadOnlyList<string> LocalFiles { get; init; } = [];
}

public sealed record InstallerPayload(string Source, string Destination, PayloadKind Kind = PayloadKind.File)
{
    public string? AssetId { get; init; }
}

public sealed record InstallerStep(string Name, Func<InstallerStepContext, CancellationToken, Task> RunAsync)
{
    public string? Description { get; init; }
    public InstallerPreferredStep? Preferred { get; init; }
    public IReadOnlyList<InstallerAsset> Assets { get; init; } = [];
    public IReadOnlyList<string> CheckpointFiles { get; init; } = [];
}

public sealed record InstallerPreferredStep(string Name, string ProbeDescription, Func<CancellationToken, Task<bool>> ProbeAsync,
    Func<InstallerStepContext, CancellationToken, Task> RunAsync)
{
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public long MinimumFreeBytes { get; init; }
    public bool WorksOffline { get; init; }
    public IReadOnlyList<InstallerAsset> Assets { get; init; } = [];
}

public sealed record LaunchDefinition(string Executable)
{
    public LaunchTarget Target { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public Func<InstallerLaunchContext, CancellationToken, Task>? BeforeLaunchAsync { get; init; }
    public Func<InstallerLaunchContext, CancellationToken, Task>? AfterLaunchAsync { get; init; }
}

public sealed record InstallerLaunchContext(string InstallDirectory, string DataDirectory, string? SourceGameDirectory, int? ProcessId)
{
    public string? SettingsFile { get; init; }
}

public enum InstallDownloadPolicy { AskEachTime, ApproveReviewed, LocalOnly }

public sealed record InstallRequest(string? SourceGameDirectory = null, IReadOnlyList<string>? Components = null)
{
    public InstallDownloadPolicy DownloadPolicy { get; init; }
    public bool AllowCapabilityChecks { get; init; }
    public SetupMode Mode { get; init; }
    public InstallIntent Intent { get; init; }
    public InstallerShortcuts? Shortcuts { get; init; }
    public InstallerNetworkChoice Network { get; init; }
}

public sealed record ResolvedGame(string Directory, string? BuildId, string Executable);

public sealed record InstallerStatus(
    string ProductDirectory, ResolvedGame? SourceGame, string? SourceProblem, string? InstalledVersion,
    IReadOnlyList<string> InstalledComponents, bool CanLaunch)
{
    public string? RuntimeDirectory { get; init; }
    public bool CanRemove { get; init; }
    public bool SettingsPending { get; init; }
    public InstallerShortcuts? Shortcuts { get; init; }
    public InstallerNetworkChoice Network { get; init; }
    public InstallRequest? PendingRequest { get; init; }
    public bool LimitedPlayAccepted { get; init; }
}

public sealed record SkippedComponent(string Id, string Name, string Reason);

public sealed record InstallResult(string Directory, string Version, IReadOnlyList<string> InstalledComponents)
{
    public IReadOnlyList<SkippedComponent> SkippedComponents { get; init; } = [];
}

public sealed record RemoveResult(int RemovedFiles, int PreservedFiles);

public sealed record InstallerProgress(string Message, long? Completed = null, long? Total = null)
{
    public string? Stage { get; init; }
    public string? NextStage { get; init; }
    public int StagesCompleted { get; init; }
    public int StageCount { get; init; }
}

public sealed record DownloadQuestion(InstallerAsset Asset, int Attempt, int MaxAttempts, bool CanDownload, string? LastError)
{
    public bool CanSkip { get; init; }
    public bool CanOpenDownloadLink { get; init; } = true;
}

public enum DownloadChoice { Cancel, Download, UseLocalFile, Skip, UseLocalFolder }

public sealed record DownloadDecision(DownloadChoice Choice, string? File = null);

public interface IInstallerInteraction
{
    Task<DownloadDecision> ChooseDownloadAsync(DownloadQuestion question, CancellationToken cancellationToken);
}

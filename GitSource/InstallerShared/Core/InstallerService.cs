using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameInstaller;

public sealed partial class InstallerService
{
    private sealed class OptionalComponentFailed(InstallerComponent component, Exception error) : Exception(error.Message, error)
    {
        internal InstallerComponent Component { get; } = component;
    }

    private readonly InstallerDefinition definition;
    private readonly string package;
    private readonly InstallStore store;
    private readonly IInstallerLogger? logger;

    public InstallerService(InstallerDefinition definition, string packageDirectory, string? installParent = null, IInstallerLogger? logger = null)
        : this(definition, packageDirectory, installParent, logger, null) { }

    public InstallerService(InstallerDefinition definition, string packageDirectory, string? installParent,
        IInstallerLogger? logger, InstallerServiceOptions? options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        this.definition = definition;
        this.logger = logger;
        productDirectory = options?.ProductDirectory is { } destination ? SafeLocalDirectory(destination) :
            definition.Managed is not null && installParent is null ? GetManagedProductDirectory(definition) : GetProductDirectory(definition.Id, installParent);
        string stateDirectory = StateDirectory(productDirectory);
        RuntimeLocation? recorded = InstallStore.ReadRuntime(stateDirectory);
        string? runtime = recorded?.Directory;
        if (runtime is null && options?.RuntimeParent is { } runtimeParent)
        {
            if (definition.Managed is null || new InstallStore(stateDirectory, definition.Id).ReadState().Active is not null)
                throw new InvalidOperationException("Choose a separate runtime drive before creating a managed install.");
            runtime = InstallFiles.Under(SafeLocalDirectory(runtimeParent, allowDriveRoot: true), $"GameInstaller-{definition.Id}-{DirectoryKey(stateDirectory)}");
            InstallFiles.RequireSeparate(runtime, stateDirectory);
            InstallFiles.RequireSeparate(runtime, productDirectory);
        }
        store = new InstallStore(stateDirectory, definition.Id, runtime);
        if (recorded is not null)
            store.ValidateRuntimeOwner();
        bundle = options?.Package;
        if (bundle is not null && (bundle.ProductId != definition.Id || bundle.Version != definition.Version))
            throw new InvalidDataException("The bundled setup package belongs to a different product or version.");
        loosePackage = Path.GetFullPath(packageDirectory);
        if (bundle is null && options?.UseInstalledPayload == true && store.IsManagedOwner() &&
            store.ReadPreparedPackage() is { } cached && cached.Version == definition.Version && InstallFiles.IsHash(cached.Fingerprint))
            retainedPackage = cached;
        string? fingerprint = bundle?.Fingerprint ?? retainedPackage?.Fingerprint;
        package = fingerprint is null ? loosePackage : Path.Combine(store.Cache, "Package", fingerprint);
        platform = definition.Managed is null ? null : options?.Platform ?? new WindowsInstallerPlatform(definition, options?.LauncherExecutable, bundle);
    }

    public static string DefaultInstallParent => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameInstaller");

    public static string GetProductDirectory(string productId, string? installParent = null)
    {
        InstallFiles.CheckSegment(productId);
        return InstallFiles.Under(Path.GetFullPath(installParent ?? DefaultInstallParent), productId);
    }

    public static string GetLogDirectory(string productId, string? installParent = null)
    {
        string directory = Path.Combine(GetProductDirectory(productId, installParent), "Logs");
        InstallFiles.CheckNoLinks(directory);
        return directory;
    }

    public async Task<InstallerStatus> InspectAsync(string? sourceGameDirectory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolvedGame? source = null;
        string? problem = null;
        try { source = await FindSourceAsync(sourceGameDirectory, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        InstallReceipt? active = store.ReadReceipt(store.ReadState().Active);
        SetupSession? session = definition.Managed is null ? null : store.ReadSession();
        bool canLaunch = active?.Launch is not null && IsUsable(active) && (session is not { Complete: false, LimitedPlayAccepted: false });
        WindowsInstallState? windows = definition.Managed is null ? null : store.ReadWindows();
        return new InstallerStatus(productDirectory, source, problem, active?.Version, active?.Components ?? [], canLaunch)
        {
            RuntimeDirectory = store.Runtime, CanRemove = active is not null || windows is not null ||
                store.Generations().Any(id => store.ReadReceipt(id) is not null) || session is not null,
            SettingsPending = active is not null && definition.Settings.Count > 0 && store.ReadSettings() is null,
            Shortcuts = windows?.Registration?.Shortcuts, Network = windows?.Network ?? InstallerNetworkChoice.Unchanged,
            PendingRequest = session is { Complete: false } ? session.Request : null,
            LimitedPlayAccepted = session?.LimitedPlayAccepted == true
        };
    }

    public async Task<InstallReview> PreviewAsync(InstallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = NormalizeRequest(request);
        if (request.Intent == InstallIntent.AddComponents)
        {
            InstallReceipt active = store.ReadReceipt(store.ReadState().Active) ?? throw new InvalidOperationException("Install the base package first.");
            string[] chosenGroups = definition.Components.Where(item => request.Components?.Contains(item.Id) == true &&
                definition.ComponentGroups.Any(group => group.Exclusive && group.Id == item.GroupId)).Select(item => item.GroupId!).ToArray();
            request = request with { Components = active.Components.Where(id => definition.Components.Any(item => item.Id == id && !chosenGroups.Contains(item.GroupId)))
                .Concat(request.Components ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }
        InstallerComponent[] selected = SelectComponents(request.Components);
        ResolvedGame? source = await FindSourceAsync(request.SourceGameDirectory, cancellationToken).ConfigureAwait(false);
        request = request with { Components = Array.AsReadOnly(selected.Select(component => component.Id).ToArray()) };
        var actions = new List<InstallReviewAction>();
        if (definition.Mode == InstallMode.PrivateGameCopy)
        {
            long sourceBytes = 0;
            foreach (var item in InstallFiles.Tree(source!.Directory, cancellationToken))
                if (!item.Directory)
                    sourceBytes += new FileInfo(item.Path).Length;
            actions.Add(new("Stage the private game copy", $"Read {sourceBytes:N0} bytes from {source.Directory} if a prepared base cannot be reused. Write the staged copy under {store.Root}."));
        }
        var reviewAssets = new List<InstallReviewAsset>();
        DescribeContent("Base package", definition.Required, request, actions, reviewAssets);
        foreach (InstallerComponent component in selected)
        {
            string description = component.Description ?? "Apply the selected component.";
            if (component.AllowSkip)
                description += " Setup can skip this component and report the reason.";
            actions.Add(new(component.Name, description));
            DescribeContent(component.Name, component.Content, request, actions, reviewAssets);
        }
        if (request.Network != InstallerNetworkChoice.Unchanged)
            actions.Add(new("Private-copy network", request.Network == InstallerNetworkChoice.BlockOutbound
                ? $"Outbound blocks on all profiles for {string.Join(", ", definition.Managed!.PrivateNetworkPrograms)}. Exact paths will be shown before approval."
                : "Restore normal network access by removing this product’s recorded outbound blocks."));
        if (definition.Managed is not null)
            actions.Add(new("Windows integration", $"Register the launcher and chosen shortcuts for {productDirectory}."));
        actions.Add(new("Activate the installation", $"Record installed files and activate the completed game under {store.Runtime}. Play is a separate action."));
        return new InstallReview(definition, package, request, productDirectory, source, SourceStamp(source), actions.ToArray(),
            reviewAssets.DistinctBy(asset => asset.Id, StringComparer.OrdinalIgnoreCase).ToArray(), FreeBytes());
    }

    public Task<InstallResult> InstallReviewedAsync(InstallReview review, IInstallerInteraction? interaction = null,
        IProgress<InstallerProgress>? progress = null, CancellationToken cancellationToken = default, string? acceptedNoticeVersion = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!ReferenceEquals(review.Definition, definition) || !SamePath(review.ProductDirectory, productDirectory) || !SamePath(review.PackageDirectory, package))
            throw new ArgumentException("This review belongs to a different installer definition, package, or destination.", nameof(review));
        if (review.Notice is { } notice && acceptedNoticeVersion != notice.Version)
            throw new InvalidOperationException("Explicit acceptance of the reviewed release terms is required before installing.");
        return InstallCoreAsync(review.Request, review, interaction, progress, cancellationToken);
    }

    public Task<InstallResult> InstallAsync(InstallRequest request, IInstallerInteraction? interaction = null,
        IProgress<InstallerProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (definition.Notice is not null)
            throw new InvalidOperationException("Use the reviewed install flow to show and accept the release terms.");
        if (request.DownloadPolicy == InstallDownloadPolicy.ApproveReviewed || request.Intent != InstallIntent.Install ||
            request.Network != InstallerNetworkChoice.Unchanged)
            throw new ArgumentException("Review the installation before approving downloads or managed changes.", nameof(request));
        return InstallCoreAsync(request, null, interaction, progress, cancellationToken);
    }

    private async Task<InstallResult> InstallCoreAsync(InstallRequest request, InstallReview? review, IInstallerInteraction? interaction,
        IProgress<InstallerProgress>? progress, CancellationToken cancellationToken)
    {
        var events = new InstallerEvents(logger, progress);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = NormalizeRequest(request);
            InstallerComponent[] selected = SelectComponents(request.Components);
            int total = selected.Length + 2;
            events.Stage("Base package", NextStage(selected, 0), 0, total);
            events.Log("Install", $"Preparing {definition.Name} {definition.Version}.");
            events.Report("Checking game and package files");
            ResolvedGame? source = await FindSourceAsync(request.SourceGameDirectory, cancellationToken).ConfigureAwait(false);
            string? stamp = SourceStamp(source);
            if (review is not null && (review.SourceStamp != stamp || review.Source?.BuildId != source?.BuildId))
                throw new InvalidOperationException("The source game changed after review. Review the installation again.");
            using FileStream operation = store.OpenLock();
            EnsureNoGame();
            if (definition.Managed is not null)
                store.EnsureManagedOwner();
            store.EnsureRuntime();
            if (review?.Notice is { } notice)
            {
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notice.Text)));
                store.RecordAcceptance(new NoticeAcceptance(notice.Version, hash, definition.Version, DateTimeOffset.UtcNow));
                events.Log("Agreement", $"Accepted {notice.Title}, version {notice.Version}, SHA-256 {hash}.");
            }
            if (source is not null)
                events.Log("Install", $"Source game: {source.Directory}. Build: {source.BuildId ?? "manual"}.");

            InstallState state = store.ReadState();
            InstallReceipt? active = store.ReadReceipt(state.Active);
            string[] selection = selected.Select(component => component.Id).ToArray();
            if (definition.Managed is not null)
                store.WriteSession(new SetupSession(request, Complete: false));
            if (request.Intent != InstallIntent.Reinstall && CanReuse(active, stamp, selection, cancellationToken))
            {
                await CompleteSetupAsync(active!, request, interaction, true, cancellationToken).ConfigureAwait(false);
                events.Log("Install", "The requested install is ready.");
                events.Stage("Installation ready", null, total, total);
                return Result(active!);
            }

            if (bundle is not null)
            {
                await bundle.PreparePayloadAsync(package, cancellationToken).ConfigureAwait(false);
                if (definition.Managed is not null)
                    store.WritePreparedPackage(bundle.PreparedPayload());
            }
            else if (retainedPackage is not null)
                await CheckRetainedPackageAsync(retainedPackage, cancellationToken).ConfigureAwait(false);
            var assets = new AssetStore(Path.Combine(store.Cache, "Downloads"), package, interaction, events, request.DownloadPolicy, review?.Assets)
            { EmbeddedPackage = bundle, FallbackDirectory = bundle is null && retainedPackage is null ? null : loosePackage };
            InstallReceipt? baseline = store.ReadReceipt(state.Base);
            if (request.Intent == InstallIntent.Reinstall || !CanReuse(baseline, stamp, [], cancellationToken))
            {
                EnsureFreeForCopy(source, cancellationToken);
                baseline = await BuildAsync(source, stamp, [], assets, events, request.AllowCapabilityChecks, interaction, cancellationToken).ConfigureAwait(false);
                state = new InstallState(active is not null && IsUsable(active) ? active.Id : baseline.Id, baseline.Id);
                store.WriteState(state);
                events.Log("Install", "Base package is ready.");
            }

            if (selected.Length > 0 && (active is null || !IsUsable(active)) && state.Active != baseline!.Id)
            {
                state = state with { Active = baseline.Id };
                store.WriteState(state);
            }
            InstallReceipt next = baseline!;
            List<SkippedComponent> skipped = [];
            List<InstallerComponent> remaining = [.. selected];
            while (remaining.Count > 0)
            {
                try
                {
                    next = await BuildAsync(source, stamp, remaining.ToArray(), assets, events, request.AllowCapabilityChecks,
                        interaction, cancellationToken, baseline, selected).ConfigureAwait(false);
                    break;
                }
                catch (OptionalComponentFailed failure)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string reason = InstallerLog.Clean(failure.Message);
                    RemoveSkipped(remaining, failure.Component, reason, skipped, events);
                }
            }
            if (selected.Length > 0 && remaining.Count == 0 && active is not null && IsUsable(active))
                next = active;
            cancellationToken.ThrowIfCancellationRequested();
            events.Stage("Activate installation", "Review the result", total - 1, total);
            if (state.Active != next.Id)
                store.WriteState(state with { Active = next.Id });
            await CompleteSetupAsync(next, request, interaction, skipped.Count == 0, cancellationToken).ConfigureAwait(false);
            events.Log("Install", $"Installed {definition.Name} {next.Version}.");
            events.Stage("Installation ready", null, total, total);
            events.Report("Installation ready", 1, 1);
            return Result(next) with { SkippedComponents = skipped.AsReadOnly() };
        }
        catch (OperationCanceledException)
        {
            events.Log("Install", "Setup stopped. Completed installations and verified downloads were kept.");
            throw;
        }
        catch (Exception error)
        {
            events.Log("Install", error.Message, InstallerLogLevel.Error);
            throw;
        }
    }

    public async Task<int> LaunchAsync(CancellationToken cancellationToken = default)
    {
        var events = new InstallerEvents(logger, null);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream operation = store.OpenLock();
            EnsureNoGame();
            if (definition.Managed is not null && store.ReadSession() is { Complete: false, LimitedPlayAccepted: false })
                throw new InvalidOperationException("Resume setup or explicitly continue with the features already installed.");
            InstallReceipt active = store.ReadReceipt(store.ReadState().Active) ?? throw new InvalidOperationException("Install the game or mod before launching it.");
            InstalledLaunch launch = active.Launch ?? throw new InvalidOperationException("This installer does not have a launch action.");
            LaunchDefinition? hooks = active.Version == definition.Version ? definition.Launch : null;
            string executable = GetLaunchPath(active, launch) ?? throw new InvalidOperationException("The source game folder is unavailable.");
            InstallFiles.CheckNoLinks(executable);
            if (!File.Exists(executable))
                throw new FileNotFoundException("The launch executable is missing. Reinstall the package.", executable);
            string installDirectory = store.Content(active.Id);
            string targetDirectory = launch.Target == LaunchTarget.Installed ? installDirectory : active.SourceDirectory!;
            string workingDirectory = launch.WorkingDirectory is null ? Path.GetDirectoryName(executable)!
                : InstallFiles.Under(targetDirectory, launch.WorkingDirectory);
            InstallFiles.CheckNoLinks(workingDirectory);
            InstallFiles.CheckNoLinks(store.Data);
            Directory.CreateDirectory(store.Data);
            var context = new InstallerLaunchContext(installDirectory, store.Data, active.SourceDirectory, null)
            { SettingsFile = definition.Settings.Count == 0 ? null : store.SettingsFile };
            if (hooks?.BeforeLaunchAsync is { } beforeLaunch)
                await beforeLaunch(context, cancellationToken).ConfigureAwait(false);

            var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false };
            foreach (string argument in launch.Arguments)
                start.ArgumentList.Add(Expand(argument, context));
            foreach (var pair in launch.Environment)
                start.Environment[pair.Key] = Expand(pair.Value, context);

            cancellationToken.ThrowIfCancellationRequested();
            using Process game = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the configured executable.");
            int processId = game.Id;
            if (!game.HasExited)
            {
                try { store.WriteRunning(new RunningGame(processId, game.StartTime.ToUniversalTime())); }
                catch (InvalidOperationException) when (game.HasExited) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    events.Log("Launch", $"The game launched, but its running state could not be saved: {error.Message}", InstallerLogLevel.Warning);
                }
            }
            events.Log("Launch", $"Started {Path.GetFileName(executable)} (PID {processId}).");
            if (hooks?.AfterLaunchAsync is { } afterLaunch)
            {
                try { await afterLaunch(context with { ProcessId = processId }, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { events.Log("Launch", $"The game launched, but startup customization stopped: {error.Message}", InstallerLogLevel.Warning); }
            }
            return processId;
        }
        catch (Exception error)
        {
            events.Log("Launch", error.Message, InstallerLogLevel.Error);
            throw;
        }
    }

    public Task<RemoveResult> RemoveAsync(IProgress<InstallerProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (definition.Managed is not null)
            throw new InvalidOperationException("Review and confirm the managed removal first.");
        return RemoveCoreAsync(null, null, progress, cancellationToken);
    }

    private async Task<RemoveResult> RemoveCoreAsync(RemoveReview? review, IInstallerInteraction? interaction,
        IProgress<InstallerProgress>? progress, CancellationToken cancellationToken)
    {
        var events = new InstallerEvents(logger, progress);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream operation = store.OpenLock();
            EnsureNoGame();
            if (review is not null && review.InstalledIdentity != RemovalIdentity())
                throw new InvalidOperationException("The installation changed after review. Open Uninstall again.");
            if (definition.Managed is not null)
            {
                store.EnsureManagedOwner();
                events.Stage("Shortcuts and firewall rules", "Downloads and settings", 0, 3);
                await RemoveSetupIntegrationAsync(interaction, events, cancellationToken).ConfigureAwait(false);
                events.Stage("Downloads and settings", "Installed files", 1, 3);
                events.Report("Removing setup downloads and settings");
                store.RemoveSetupData(cancellationToken);
                events.Stage("Installed files", null, 2, 3);
            }
            events.Report("Removing installer-owned files");
            string[] generations = store.Generations().ToArray();
            store.WriteState(new InstallState());
            int removed = 0;
            int preserved = 0;
            foreach (string id in generations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InstallReceipt? receipt = store.ReadReceipt(id);
                if (receipt is null)
                    continue;
                RemoveResult result = await RemoveGenerationAsync(receipt, events, cancellationToken).ConfigureAwait(false);
                removed += result.RemovedFiles;
                preserved += result.PreservedFiles;
                store.FinishRemoval(id);
            }
            if (definition.Managed is not null)
                await FinishSetupRemovalAsync(interaction, cancellationToken).ConfigureAwait(false);
            events.Log("Remove", $"Removed {removed:N0} files. Preserved {preserved:N0} modified or untracked files in install folders.");
            events.Report("Removal complete", 1, 1);
            return new RemoveResult(removed, preserved);
        }
        catch (Exception error)
        {
            events.Log("Remove", error.Message, error is OperationCanceledException ? InstallerLogLevel.Info : InstallerLogLevel.Error);
            throw;
        }
    }

    private async Task<InstallReceipt> BuildAsync(ResolvedGame? source, string? stamp, InstallerComponent[] selected, AssetStore assets,
        InstallerEvents events, bool allowChecks, IInstallerInteraction? interaction, CancellationToken token,
        InstallReceipt? baseline = null, InstallerComponent[]? originalSelection = null)
    {
        string id = Guid.NewGuid().ToString("N");
        string stage = store.NewStage(id);
        string content = Path.Combine(stage, "Content");
        try
        {
            var readyAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (baseline is null || !await BaselineMatchesAsync(baseline, events, token).ConfigureAwait(false))
            {
                if (definition.Mode == InstallMode.PrivateGameCopy)
                {
                    events.Log("Install", "Copying the stock game into private staging.");
                    events.Report("Copying the source game");
                    await InstallFiles.CopyTreeAsync(source!.Directory, content, token).ConfigureAwait(false);
                }
                await ApplyAsync(definition.Required, "Base package", content, source, stamp, [], assets, readyAssets, events,
                    allowChecks, false, interaction, token).ConfigureAwait(false);
            }
            else
            {
                foreach (InstallerAsset asset in definition.Required.Assets)
                    readyAssets.Add(asset.Id, await assets.ResolveAsync(asset, token).ConfigureAwait(false));
                events.Report("Staging the base package for optional components");
                await CopyBaselineAsync(baseline, content, token).ConfigureAwait(false);
            }
            for (int index = 0; index < selected.Length; index++)
            {
                InstallerComponent component = selected[index];
                InstallerComponent[] plan = originalSelection ?? selected;
                events.Stage(component.Name, NextStage(selected, index + 1), Array.IndexOf(plan, component) + 1, plan.Length + 2);
                try
                {
                    await ApplyAsync(component.Content, component.Id, content, source, stamp,
                        selected.Take(index + 1).Select(item => item.Id).ToArray(), assets, readyAssets, events, allowChecks,
                        component.AllowSkip, interaction, token).ConfigureAwait(false);
                }
                catch (Exception error) when (component.AllowSkip && error is not OperationCanceledException)
                {
                    throw new OptionalComponentFailed(component, error);
                }
            }

            if (definition.Launch is { Target: LaunchTarget.Installed } launch && !File.Exists(InstallFiles.Under(content, launch.Executable)))
                throw new InvalidDataException("The package did not prepare the configured launch executable.");
            events.Report("Recording installed files");
            var files = new List<OwnedFile>();
            var directories = new List<string>();
            foreach (var item in InstallFiles.Tree(content, token))
            {
                string relative = Path.GetRelativePath(content, item.Path);
                if (item.Directory)
                    directories.Add(relative);
                else
                    files.Add(new OwnedFile(relative, new FileInfo(item.Path).Length, await InstallFiles.HashAsync(item.Path, token).ConfigureAwait(false)));
            }
            InstalledLaunch? installedLaunch = definition.Launch is { } launchSpec
                ? new InstalledLaunch(launchSpec.Executable, launchSpec.Target, launchSpec.WorkingDirectory, launchSpec.Arguments.ToArray(),
                    launchSpec.Environment.ToDictionary(pair => pair.Key, pair => pair.Value)) : null;
            var receipt = new InstallReceipt(id, definition.Id, definition.Version, source?.Directory, stamp,
                selected.Select(component => component.Id).ToArray(), files.ToArray(), directories.ToArray(), installedLaunch);
            token.ThrowIfCancellationRequested();
            store.Promote(stage, receipt);
            return receipt;
        }
        finally
        {
            if (Directory.Exists(stage))
                TryCleanStage(stage, events);
        }
    }

    private async Task<bool> BaselineMatchesAsync(InstallReceipt baseline, InstallerEvents events, CancellationToken token)
    {
        string source = store.Content(baseline.Id);
        events.Report("Checking the installed base package");
        foreach (OwnedFile file in baseline.Files)
        {
            token.ThrowIfCancellationRequested();
            string from = InstallFiles.Under(source, file.Path);
            if (!File.Exists(from) || !await FileMatchesAsync(from, file, token).ConfigureAwait(false))
            {
                events.Log("Install", $"Base file changed. Preparing a fresh candidate: {file.Path}", InstallerLogLevel.Warning);
                return false;
            }
        }
        return true;
    }

    private async Task CopyBaselineAsync(InstallReceipt baseline, string destination, CancellationToken token)
    {
        string source = store.Content(baseline.Id);
        foreach (string directory in baseline.Directories)
            Directory.CreateDirectory(InstallFiles.Under(destination, directory));
        foreach (OwnedFile file in baseline.Files)
            await InstallFiles.CopyFileAsync(InstallFiles.Under(source, file.Path), InstallFiles.Under(destination, file.Path), token).ConfigureAwait(false);
    }

    private async Task ApplyAsync(InstallContent content, string label, string destination, ResolvedGame? source, string? stamp,
        string[] selection, AssetStore assets, Dictionary<string, string> readyAssets, InstallerEvents events, bool allowChecks,
        bool canSkip, IInstallerInteraction? interaction, CancellationToken token)
    {
        events.Log("Install", $"Preparing {label}.");
        events.Report($"Preparing {label}");
        if (FreeBytes() is long free && free < content.MinimumFreeBytes)
            throw new IOException($"{label} requires {content.MinimumFreeBytes:N0} free bytes. The destination has {free:N0}.");
        foreach (InstallerAsset asset in content.Assets)
            readyAssets.Add(asset.Id, await assets.ResolveAsync(asset, token, canSkip).ConfigureAwait(false));
        foreach (InstallerPayload payload in content.Payloads)
        {
            token.ThrowIfCancellationRequested();
            string from = payload.AssetId is null ? InstallFiles.Under(package, payload.Source) : readyAssets[payload.AssetId];
            string to = InstallFiles.Under(destination, payload.Destination);
            switch (payload.Kind)
            {
                case PayloadKind.File: await InstallFiles.CopyFileAsync(from, to, token).ConfigureAwait(false); break;
                case PayloadKind.Directory: await InstallFiles.CopyTreeAsync(from, to, token).ConfigureAwait(false); break;
                case PayloadKind.Zip: await InstallFiles.ExtractAsync(from, to, token).ConfigureAwait(false); break;
            }
        }
        string cache = Path.Combine(store.Cache, "Work");
        for (int index = 0; index < content.Steps.Count; index++)
        {
            InstallerStep step = content.Steps[index];
            token.ThrowIfCancellationRequested();
            InstallFiles.CheckNoLinks(cache);
            Directory.CreateDirectory(cache);
            events.Report(step.Name);
            events.Log(step.Name, "Starting preparation.");
            string checkpoint = string.Join('|', definition.Version, bundle?.Fingerprint, stamp, label, index, string.Join(',', selection));
            var context = new InstallerStepContext(package, destination, cache, source, readyAssets, events, step.Name, checkpoint);
            await RunStepAsync(step, context, readyAssets, assets, allowChecks, canSkip, interaction, token).ConfigureAwait(false);
            events.Log(step.Name, "Preparation complete.");
        }
    }

    private async Task RunStepAsync(InstallerStep step, InstallerStepContext context, IReadOnlyDictionary<string, string> sharedAssets,
        AssetStore assetStore, bool allowChecks, bool canSkip, IInstallerInteraction? interaction, CancellationToken token)
    {
        if (allowChecks && step.Preferred is { } preferred && (bundle?.Offline != true || preferred.WorksOffline))
        {
            bool supported = false;
            context.ReportProgress($"Checking compatibility for {preferred.Name}");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(preferred.ProbeTimeout);
                CancellationToken probeToken = timeout.Token;
                try
                {
                    supported = await Task.Run(() => preferred.ProbeAsync(probeToken), probeToken).WaitAsync(probeToken).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    context.Log(supported ? $"Compatibility confirmed for {preferred.Name}." : $"{preferred.Name} is unavailable. Using the standard step.");
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    string reason = timeout.IsCancellationRequested ? "compatibility check timed out" : error.Message;
                    context.Log($"{preferred.Name}: {reason}. Using the standard step.", InstallerLogLevel.Warning);
                    supported = false;
                }
            }
            bool approved = false;
            if (supported && (FreeBytes() is not long free || free >= preferred.MinimumFreeBytes))
            {
                approved = preferred.Assets.Count == 0 || (definition.Managed is not null && interaction is IInstallerSetupInteraction setup &&
                    await setup.ApproveCapabilityAsync(new InstallerCapabilityQuestion(preferred.Name,
                        preferred.Assets.Select(asset => DescribeAsset(asset, separate: true)).ToArray()), token).ConfigureAwait(false));
                if (definition.Managed is null)
                    approved = true;
            }
            if (supported && approved)
            {
                try
                {
                    var preferredContext = await WithRouteAssetsAsync(context, sharedAssets, preferred.Assets, assetStore, canSkip, token,
                        separate: definition.Managed is not null).ConfigureAwait(false);
                    await RunCheckpointedStepAsync(step, preferredContext, "preferred", preferred.RunAsync, token).ConfigureAwait(false);
                    return;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is not OperationCanceledException &&
                    (definition.Managed is not null || error is not AssetSkippedException))
                {
                    context.Log($"{preferred.Name} failed: {error.Message}. Using the standard step.", InstallerLogLevel.Warning);
                    context.ReportProgress("Retrying with the standard step");
                }
            }
        }
        else if (step.Preferred is not null)
            context.Log("Compatibility check declined. Using the standard step.");

        token.ThrowIfCancellationRequested();
        var standardContext = await WithRouteAssetsAsync(context, sharedAssets, step.Assets, assetStore, canSkip, token).ConfigureAwait(false);
        await RunCheckpointedStepAsync(step, standardContext, "standard", step.RunAsync, token).ConfigureAwait(false);
    }

    private static async Task<InstallerStepContext> WithRouteAssetsAsync(InstallerStepContext context, IReadOnlyDictionary<string, string> sharedAssets,
        IReadOnlyList<InstallerAsset> route, AssetStore store, bool canSkip, CancellationToken token, bool separate = false)
    {
        if (route.Count == 0)
            return context;
        var available = new Dictionary<string, string>(sharedAssets, StringComparer.OrdinalIgnoreCase);
        foreach (InstallerAsset asset in route)
            available.Add(asset.Id, await (separate ? store.ResolvePreferredAsync(asset, token, canSkip) : store.ResolveAsync(asset, token, canSkip))
                .ConfigureAwait(false));
        return context.WithAssets(available);
    }

    private async Task<RemoveResult> RemoveGenerationAsync(InstallReceipt receipt, InstallerEvents events, CancellationToken token)
    {
        string content = store.Content(receipt.Id);
        int removed = 0;
        foreach (OwnedFile file in receipt.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = InstallFiles.Under(content, file.Path);
            try { InstallFiles.CheckNoLinks(path); }
            catch (InvalidDataException) { continue; }
            if (File.Exists(path) && await FileMatchesAsync(path, file, token).ConfigureAwait(false))
            {
                File.Delete(path);
                removed++;
            }
        }
        foreach (string name in receipt.Directories.OrderByDescending(path => path.Length).Append(""))
        {
            string directory = InstallFiles.Under(content, name);
            try { InstallFiles.CheckNoLinks(directory); }
            catch (InvalidDataException) { continue; }
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        int preserved = 0;
        if (Directory.Exists(content))
        {
            try { preserved = InstallFiles.Tree(content, token, preserveLinks: true).Count(item => !item.Directory); }
            catch (InvalidDataException) { preserved = 1; }
            events.Log("Remove", $"Preserved user content at {content}.");
        }
        return new RemoveResult(removed, preserved);
    }

    private static async Task<bool> FileMatchesAsync(string path, OwnedFile file, CancellationToken token) =>
        new FileInfo(path).Length == file.Bytes && string.Equals(await InstallFiles.HashAsync(path, token).ConfigureAwait(false),
            file.Sha256, StringComparison.OrdinalIgnoreCase);

    private static void TryCleanStage(string stage, InstallerEvents events)
    {
        try
        {
            InstallFiles.CheckNoLinks(stage);
            Directory.Delete(stage, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            events.Log("Install", $"Incomplete staging was left at {stage}: {error.Message}", InstallerLogLevel.Warning);
        }
    }

    private async Task<ResolvedGame?> FindSourceAsync(string? sourceDirectory, CancellationToken token)
    {
        if (definition.Source is not { } spec)
            return null;
        ResolvedGame game = await SteamDiscovery.ResolveAsync(spec, sourceDirectory, token).ConfigureAwait(false);
        InstallFiles.RequireSeparate(game.Directory, store.Root);
        InstallFiles.RequireSeparate(game.Directory, store.Runtime);
        InstallFiles.RequireSeparate(game.Directory, productDirectory);
        return game;
    }

    private bool CanReuse(InstallReceipt? receipt, string? stamp, string[] selection, CancellationToken token)
    {
        if (receipt is null || receipt.Version != definition.Version || receipt.SourceStamp != stamp ||
            !receipt.Components.SequenceEqual(selection, StringComparer.OrdinalIgnoreCase))
            return false;
        string content = store.Content(receipt.Id);
        InstallFiles.CheckNoLinks(content);
        if (!Directory.Exists(content))
            return false;
        foreach (OwnedFile file in receipt.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = InstallFiles.Under(content, file.Path);
            InstallFiles.CheckNoLinks(path);
            if (!File.Exists(path))
                return false;
        }
        return true;
    }

    private InstallerComponent[] SelectComponents(IReadOnlyList<string>? ids)
    {
        var requested = new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase);
        var components = definition.Components.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (string id in requested)
            if (!components.ContainsKey(id))
                throw new ArgumentException($"Unknown component: {id}.");
        var selected = new List<InstallerComponent>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstallerComponent component in definition.Components.Where(item => requested.Contains(item.Id)))
            Visit(component);
        foreach (InstallerComponentGroup group in definition.ComponentGroups.Where(group => group.Exclusive))
            if (selected.Count(item => item.GroupId == group.Id) > 1)
                throw new ArgumentException($"Select at most one option for {group.Name}.");
        return selected.ToArray();

        void Visit(InstallerComponent component)
        {
            if (visited.Contains(component.Id))
                return;
            if (!visiting.Add(component.Id))
                throw new ArgumentException($"Component dependencies contain a cycle at {component.Name}.");
            foreach (string dependency in component.Requires)
                Visit(components[dependency]);
            visiting.Remove(component.Id);
            visited.Add(component.Id);
            selected.Add(component);
        }
    }

    private void EnsureNoGame()
    {
        if (store.ReadRunning() is { } running && IsRunning(running))
            throw new InvalidOperationException($"Close {definition.Name} before continuing.");
        foreach (string name in definition.BlockingProcessNames)
        {
            Process[] processes = Process.GetProcessesByName(name);
            foreach (Process process in processes)
                process.Dispose();
            if (processes.Length > 0)
                throw new InvalidOperationException($"Close {name} normally before continuing.");
        }
    }

    private static bool IsRunning(RunningGame running)
    {
        try
        {
            using Process process = Process.GetProcessById(running.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == running.StartedUtc;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private bool IsUsable(InstallReceipt receipt) => receipt.Launch is { } launch
        ? GetLaunchPath(receipt, launch) is { } path && File.Exists(path) : Directory.Exists(store.Content(receipt.Id));

    private string? GetLaunchPath(InstallReceipt receipt, InstalledLaunch launch)
    {
        string? root = launch.Target == LaunchTarget.Installed ? store.Content(receipt.Id) : receipt.SourceDirectory;
        return root is null ? null : InstallFiles.Under(root, launch.Executable);
    }

    private static string? SourceStamp(ResolvedGame? source)
    {
        if (source is null)
            return null;
        var executable = new FileInfo(source.Executable);
        return string.Create(CultureInfo.InvariantCulture, $"{source.Directory.ToUpperInvariant()}|{source.BuildId}|{executable.Length}|{executable.LastWriteTimeUtc.Ticks}");
    }

    private InstallResult Result(InstallReceipt receipt) => new(store.Content(receipt.Id), receipt.Version, receipt.Components);

    private static string Expand(string value, InstallerLaunchContext context) => value.Replace("{install}", context.InstallDirectory)
        .Replace("{data}", context.DataDirectory).Replace("{source}", context.SourceGameDirectory ?? "")
        .Replace("{settings}", context.SettingsFile ?? "");

    private void DescribeContent(string name, InstallContent content, InstallRequest request, List<InstallReviewAction> actions,
        List<InstallReviewAsset> assets)
    {
        foreach (InstallerAsset asset in content.Assets)
            assets.Add(DescribeAsset(asset));

        if (content.MinimumFreeBytes > 0)
            actions.Add(new(name, $"Check for at least {content.MinimumFreeBytes:N0} free bytes on the destination volume."));
        if (content.Assets.Count > 0)
            actions.Add(new(name, $"Verify the listed files for this step and save accepted copies under {Path.Combine(store.Cache, "Downloads")}."));
        foreach (InstallerPayload payload in content.Payloads)
        {
            string input = payload.AssetId is null ? InstallFiles.Under(package, payload.Source) : $"verified asset {payload.AssetId}";
            string destination = payload.Destination.Length == 0 ? "the candidate root" : payload.Destination;
            string operation = payload.Kind == PayloadKind.Zip ? "Extract" : "Copy";
            actions.Add(new(name, $"{operation} {input} to {destination} inside the staged installation."));
        }
        foreach (InstallerStep step in content.Steps)
        {
            string action = step.Description ?? "Run project-defined preparation in the staged installation.";
            if (step.Preferred is { } preferred)
                action += request.AllowCapabilityChecks
                    ? $" If needed, {preferred.ProbeDescription} Try {preferred.Name} when compatible, with the standard step as fallback."
                    : " Use the standard step with capability detection off.";
            if (step.Assets.Count > 0)
                action += $" Standard step inputs: {string.Join(", ", step.Assets.Select(asset => asset.FileName))}.";
            foreach (InstallerAsset asset in step.Assets)
                assets.Add(DescribeAsset(asset));
            if (request.AllowCapabilityChecks && step.Preferred is { } route && route.Assets.Count > 0)
            {
                action += $" {route.Name} inputs: {string.Join(", ", route.Assets.Select(asset => asset.FileName))}.";
                foreach (InstallerAsset asset in route.Assets)
                    assets.Add(DescribeAsset(asset, separate: definition.Managed is not null));
            }
            actions.Add(new(step.Name, action));
        }
    }

    private InstallReviewAsset DescribeAsset(InstallerAsset asset, bool separate = false)
    {
        string cached = AssetStore.CachedPath(Path.Combine(store.Cache, "Downloads"), asset);
        string[] candidates = AssetStore.LocalCandidates(loosePackage, asset).Concat(AssetStore.LocalCandidates(package, asset))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new InstallReviewAsset(asset.Id, asset.FileName, asset.Bytes, asset.Sha256, asset.Url,
            Array.AsReadOnly(AssetStore.Hosts(asset)), Array.AsReadOnly(candidates), AssetStore.HasVerifiedCandidate(asset, candidates.Prepend(cached)))
        { IsEmbedded = bundle?.HasAsset(asset) == true, RequiresSeparateApproval = separate };
    }

    private long? FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(store.Runtime)!).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(left)
        .Equals(Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private static string NextStage(InstallerComponent[] selected, int index) => index < selected.Length ? selected[index].Name : "Activate installation";

    private InstallRequest NormalizeRequest(InstallRequest request)
    {
        if (!Enum.IsDefined(request.DownloadPolicy) || !Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Intent) || !Enum.IsDefined(request.Network))
            throw new ArgumentException("The installer download policy is invalid.", nameof(request));
        if (request.Mode == SetupMode.Easy || !HasPrivateNetwork)
            request = request with { Network = InstallerNetworkChoice.Unchanged };
        return bundle?.Offline == true ? request with { DownloadPolicy = InstallDownloadPolicy.LocalOnly } : request;
    }

    private static void ValidateDefinition(InstallerDefinition definition)
    {
        InstallFiles.CheckSegment(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);
        if (definition.Notice is { } notice && (string.IsNullOrWhiteSpace(notice.Version) || string.IsNullOrWhiteSpace(notice.Title) ||
            string.IsNullOrWhiteSpace(notice.Text)))
            throw new ArgumentException("A release notice needs a version, title, and terms.");
        if (!Enum.IsDefined(definition.Mode) || (definition.Mode == InstallMode.Standalone) != (definition.Source is null))
            throw new ArgumentException("The install mode and source game configuration do not match.");
        if (definition.Source is { } source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Executable);
            if (source.FileHashes.Values.Any(hash => !InstallFiles.IsHash(hash)))
                throw new ArgumentException("Source game hashes must use SHA-256.");
        }
        if (definition.Launch is { } launch)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(launch.Executable);
            if (!Enum.IsDefined(launch.Target) || (launch.Target == LaunchTarget.SourceGame && definition.Mode != InstallMode.LauncherOnly))
                throw new ArgumentException("Launching directly from a source game requires launcher-only mode.");
        }

        var componentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstallerComponent component in definition.Components)
            if (!componentIds.Add(component.Id) || string.IsNullOrWhiteSpace(component.Id))
                throw new ArgumentException("Component IDs must be unique and nonempty.");
        ValidateSetupDefinition(definition, componentIds);
        foreach (InstallContent content in definition.Components.Select(component => component.Content).Prepend(definition.Required))
        {
            if (content.MinimumFreeBytes < 0)
                throw new ArgumentException("The minimum free space must be zero or positive.");
            foreach (InstallerStep step in content.Steps)
                if (step.Preferred is { } preferred && (string.IsNullOrWhiteSpace(preferred.ProbeDescription) ||
                    preferred.ProbeTimeout <= TimeSpan.Zero || preferred.ProbeTimeout.TotalMilliseconds >= uint.MaxValue || preferred.MinimumFreeBytes < 0))
                    throw new ArgumentException("A preferred step needs a description and a positive, finite probe timeout.");
            var available = definition.Required.Assets.Select(asset => asset.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (InstallerAsset asset in content.Assets)
            {
                ValidateAsset(asset, assetIds);
                available.Add(asset.Id);
            }
            foreach (InstallerStep step in content.Steps)
                foreach (InstallerAsset asset in step.Assets.Concat(step.Preferred?.Assets ?? []))
                    ValidateAsset(asset, assetIds);
            foreach (InstallerPayload payload in content.Payloads)
            {
                if (!Enum.IsDefined(payload.Kind) || (payload.AssetId is not null && (!available.Contains(payload.AssetId) || payload.Kind == PayloadKind.Directory)) ||
                    (payload.Kind == PayloadKind.File && string.IsNullOrWhiteSpace(payload.Destination)) ||
                    (payload.AssetId is null && payload.Kind != PayloadKind.Directory && string.IsNullOrWhiteSpace(payload.Source)))
                    throw new ArgumentException("A payload has an invalid source, destination, or kind.");
            }
        }
    }

    private static void ValidateAsset(InstallerAsset asset, HashSet<string> assetIds)
    {
        if (!assetIds.Add(asset.Id) || string.IsNullOrWhiteSpace(asset.Id))
            throw new ArgumentException("Asset IDs must be unique and nonempty.");
        AssetStore.Validate(asset);
    }
}

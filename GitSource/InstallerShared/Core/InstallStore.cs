using System.Text.Json;

namespace GameInstaller;

internal sealed record InstallState(string? Active = null, string? Base = null);
internal sealed record OwnedFile(string Path, long Bytes, string Sha256);
internal sealed record InstalledLaunch(string Executable, LaunchTarget Target, string? WorkingDirectory, string[] Arguments,
    Dictionary<string, string> Environment);
internal sealed record InstallReceipt(string Id, string ProductId, string Version, string? SourceDirectory, string? SourceStamp,
    string[] Components, OwnedFile[] Files, string[] Directories, InstalledLaunch? Launch);
internal sealed record RunningGame(int ProcessId, DateTime StartedUtc);
internal sealed record NoticeAcceptance(string NoticeVersion, string NoticeSha256, string ProductVersion, DateTimeOffset AcceptedUtc);
internal sealed record RuntimeLocation(string Directory);
internal sealed record RuntimeOwner(string ProductId, string ProductDirectory);
internal sealed record SetupSession(InstallRequest Request, bool Complete, bool LimitedPlayAccepted = false);
internal sealed record PreparedPackage(string Version, string Fingerprint, OwnedFile[] Files);
internal sealed record WindowsInstallState(InstallerRegistration? Registration, InstallerNetworkChoice Network,
    InstallerFirewallRule[] Rules);

internal sealed class InstallStore(string root, string productId, string? runtimeDirectory = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string Installs => Path.Combine(Runtime, "Installs");
    private string Staging => Path.Combine(Runtime, "Staging");
    private string StateFile => Path.Combine(root, "state.json");
    private string RunningFile => Path.Combine(root, "running.json");

    internal string Root => root;
    internal string Runtime => runtimeDirectory ?? root;
    internal string Cache => Path.Combine(Runtime, "Cache");
    internal string Data => Path.Combine(root, "Data");
    private string OwnerPath => Path.Combine(root, ".gameinstaller-owner.json");
    internal string SettingsFile => Path.Combine(root, "Settings.json");
    internal SetupSession? ReadSession() => Read<SetupSession>(Path.Combine(root, "setup-session.json"));
    internal void WriteSession(SetupSession session) => Write(Path.Combine(root, "setup-session.json"), session);
    internal PreparedPackage? ReadPreparedPackage() => Read<PreparedPackage>(Path.Combine(root, "prepared-package.json"));
    internal void WritePreparedPackage(PreparedPackage package) => Write(Path.Combine(root, "prepared-package.json"), package);
    internal WindowsInstallState? ReadWindows() => Read<WindowsInstallState>(Path.Combine(root, "windows.json"));
    internal void WriteWindows(WindowsInstallState state) => Write(Path.Combine(root, "windows.json"), state);
    internal Dictionary<string, bool>? ReadSettings() => Read<Dictionary<string, bool>>(SettingsFile);
    internal void WriteSettings(Dictionary<string, bool> settings) => Write(SettingsFile, settings);
    internal static RuntimeLocation? ReadRuntime(string productRoot) => Read<RuntimeLocation>(Path.Combine(productRoot, "runtime.json"));

    internal void EnsureManagedOwner()
    {
        var expected = new RuntimeOwner(productId, root);
        RuntimeOwner? owner = Read<RuntimeOwner>(OwnerPath);
        if (owner == expected)
            return;
        if (owner is not null)
            throw new InvalidDataException("This folder is already managed by a different installation.");
        bool known = ReadReceipt(ReadState().Active) is not null || Generations().Any(id => ReadReceipt(id) is not null);
        if (!known && new[] { "Cache", "Staging", "Installs", "Data", "Settings.json", "windows.json", "setup-session.json", "runtime.json" }
            .Any(name => Path.Exists(Path.Combine(root, name))))
            throw new InvalidDataException("This folder already contains setup files from an unknown installation. Choose a different folder.");
        Write(OwnerPath, expected);
    }

    internal bool IsManagedOwner() => Read<RuntimeOwner>(OwnerPath) == new RuntimeOwner(productId, root);

    internal void EnsureRuntime()
    {
        if (runtimeDirectory is null)
            return;
        string marker = Path.Combine(runtimeDirectory, "owner.json");
        var owner = new RuntimeOwner(productId, root);
        RuntimeOwner? recorded = Read<RuntimeOwner>(marker);
        if (recorded == owner)
        {
            if (ReadRuntime(root) is null)
                Write(Path.Combine(root, "runtime.json"), new RuntimeLocation(runtimeDirectory));
            return;
        }
        if (recorded is not null || (Directory.Exists(runtimeDirectory) && Directory.EnumerateFileSystemEntries(runtimeDirectory).Any()))
            throw new InvalidDataException("This runtime location belongs to another installation. Choose an empty location.");
        Directory.CreateDirectory(runtimeDirectory);
        Write(marker, owner);
        Write(Path.Combine(root, "runtime.json"), new RuntimeLocation(runtimeDirectory));
    }

    internal void ValidateRuntimeOwner()
    {
        if (Read<RuntimeOwner>(Path.Combine(Runtime, "owner.json")) != new RuntimeOwner(productId, root))
            throw new InvalidDataException("The saved runtime folder no longer belongs to this installation.");
    }

    internal void RemoveSetupData(CancellationToken token)
    {
        RemoveDirectory(Staging, token);
        RemoveDirectory(Cache, token);
        DeleteLocal("Settings.json");
        DeleteLocal("setup-session.json");
        DeleteLocal("prepared-package.json");
    }

    internal void FinishManagedRemoval()
    {
        DeleteLocal("windows.json");
        if (runtimeDirectory is null || !Directory.Exists(runtimeDirectory))
            return;
        string marker = Path.Combine(runtimeDirectory, "owner.json");
        if (Read<RuntimeOwner>(marker) != new RuntimeOwner(productId, root))
            return;
        if (Directory.Exists(Installs) && !Directory.EnumerateFileSystemEntries(Installs).Any())
            Directory.Delete(Installs);
        if (Directory.EnumerateFileSystemEntries(runtimeDirectory).All(path => path == marker))
        {
            File.Delete(marker);
            Directory.Delete(runtimeDirectory);
            DeleteLocal("runtime.json");
        }
    }

    private void DeleteLocal(string name)
    {
        string path = Path.Combine(root, name);
        InstallFiles.CheckNoLinks(path);
        File.Delete(path);
    }

    private static void RemoveDirectory(string path, CancellationToken token)
    {
        InstallFiles.CheckNoLinks(path);
        if (!Directory.Exists(path))
            return;
        var entries = InstallFiles.Tree(path, token, preserveLinks: true).ToArray();
        foreach (var item in entries.Where(item => !item.Directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) == 0)
                File.Delete(item.Path);
        }
        foreach (string folder in entries.Where(item => item.Directory).Select(item => item.Path).OrderByDescending(path => path.Length).Append(path))
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
    }

    internal FileStream OpenLock()
    {
        string path = Path.Combine(root, "operation.lock");
        InstallFiles.CheckNoLinks(path);
        Directory.CreateDirectory(root);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        {
            throw new InvalidOperationException("An installer operation is already in progress for this folder.", error);
        }
    }

    internal InstallState ReadState() => Read<InstallState>(StateFile) ?? new InstallState();

    internal void WriteState(InstallState state) => Write(StateFile, state);

    internal RunningGame? ReadRunning() => Read<RunningGame>(RunningFile);

    internal void WriteRunning(RunningGame running) => Write(RunningFile, running);

    internal void RecordAcceptance(NoticeAcceptance acceptance)
    {
        string directory = Path.Combine(root, "Agreements");
        string path = Path.Combine(directory, acceptance.NoticeSha256 + ".json");
        InstallFiles.CheckNoLinks(path);
        Directory.CreateDirectory(directory);
        Write(path, acceptance);
    }

    internal string Content(string id) => Path.Combine(Generation(id), "Content");

    internal string NewStage(string id)
    {
        string path = InstallFiles.Under(Staging, id);
        InstallFiles.CheckNoLinks(path);
        Directory.CreateDirectory(Path.Combine(path, "Content"));
        return path;
    }

    internal void Promote(string stage, InstallReceipt receipt)
    {
        Write(Path.Combine(stage, "receipt.json"), receipt);
        string destination = Generation(receipt.Id);
        InstallFiles.CheckNoLinks(destination);
        Directory.CreateDirectory(Installs);
        Directory.Move(stage, destination);
    }

    internal InstallReceipt? ReadReceipt(string? id)
    {
        if (id is null)
            return null;
        string path = Path.Combine(Generation(id), "receipt.json");
        InstallReceipt? receipt = Read<InstallReceipt>(path);
        if (receipt is not null && (receipt.Id != id || receipt.ProductId != productId))
            throw new InvalidDataException("The install receipt belongs to a different product or directory.");
        return receipt;
    }

    internal IEnumerable<string> Generations()
    {
        InstallFiles.CheckNoLinks(Installs);
        if (Directory.Exists(Installs))
            return Directory.EnumerateDirectories(Installs).Select(path => Path.GetFileName(path)).Where(IsGenerationId);
        return [];
    }

    internal void FinishRemoval(string id)
    {
        string directory = Generation(id);
        File.Delete(Path.Combine(directory, "receipt.json"));
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static bool IsGenerationId(string? id) => Guid.TryParseExact(id, "N", out _);

    private string Generation(string id) => IsGenerationId(id) ? Path.Combine(Installs, id)
        : throw new InvalidDataException("The install generation identifier is invalid.");

    private static T? Read<T>(string path) where T : class
    {
        InstallFiles.CheckNoLinks(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(input, JsonOptions) ?? throw new InvalidDataException("Installer metadata is empty.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Write<T>(string path, T value)
    {
        InstallFiles.CheckNoLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value, JsonOptions);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { InstallFiles.TryDeleteFile(temporary); }
    }
}

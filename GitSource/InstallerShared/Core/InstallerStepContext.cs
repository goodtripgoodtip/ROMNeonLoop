using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameInstaller;

public sealed class InstallerStepContext
{
    private readonly IReadOnlyDictionary<string, string> assets;
    private readonly ResolvedGame? source;
    private readonly InstallerEvents events;
    private readonly string step;
    private readonly string checkpointScope;

    internal InstallerStepContext(string packageDirectory, string workingDirectory, string cacheDirectory, ResolvedGame? source,
        IReadOnlyDictionary<string, string> assets, InstallerEvents events, string step, string checkpointScope)
    {
        PackageDirectory = packageDirectory;
        WorkingDirectory = workingDirectory;
        CacheDirectory = cacheDirectory;
        this.source = source;
        this.assets = assets;
        this.events = events;
        this.step = step;
        this.checkpointScope = checkpointScope;
    }

    public string PackageDirectory { get; }
    public string WorkingDirectory { get; }
    public string CacheDirectory { get; }
    public string? SourceGameDirectory => source?.Directory;
    public string? SourceBuildId => source?.BuildId;

    public string GetAsset(string id) => assets.TryGetValue(id, out string? path)
        ? path : throw new ArgumentException($"Asset {id} is unavailable for this step.", nameof(id));

    public string GetWorkingPath(string relativePath) => InstallFiles.Under(WorkingDirectory, relativePath);

    internal InstallerStepContext WithAssets(IReadOnlyDictionary<string, string> routeAssets) =>
        new(PackageDirectory, WorkingDirectory, CacheDirectory, source, routeAssets, events, step, checkpointScope);

    internal InstallerStepContext WithCheckpointRoute(string route) =>
        new(PackageDirectory, WorkingDirectory, CacheDirectory, source, assets, events, step, $"{checkpointScope}|{route}");

    public async Task RunCheckpointAsync(string id, IReadOnlyList<string> outputFiles, Func<CancellationToken, Task> prepare,
        CancellationToken cancellationToken)
    {
        InstallFiles.CheckSegment(id);
        if (outputFiles.Count == 0 || outputFiles.Any(string.IsNullOrWhiteSpace) ||
            outputFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputFiles.Count)
            throw new ArgumentException("Give the checkpoint a unique list of output files.", nameof(outputFiles));
        string assetKey = string.Join('|', assets.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{checkpointScope}|{id}|{assetKey}|{string.Join('|', outputFiles)}")));
        string directory = Path.Combine(CacheDirectory, "Checkpoints", fingerprint);
        string receipt = Path.Combine(directory, "files.json");
        InstallFiles.CheckNoLinks(receipt);
        if (File.Exists(receipt))
        {
            using Stream input = File.OpenRead(receipt);
            OwnedFile[]? saved = await JsonSerializer.DeserializeAsync<OwnedFile[]>(input, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (saved is not null && saved.Select(file => file.Path).SequenceEqual(outputFiles) &&
                await CheckCheckpointAsync(directory, saved, cancellationToken).ConfigureAwait(false))
            {
                foreach (OwnedFile file in saved)
                    await InstallFiles.CopyFileAsync(InstallFiles.Under(Path.Combine(directory, "Output"), file.Path),
                        GetWorkingPath(file.Path), cancellationToken).ConfigureAwait(false);
                Log($"Reused completed checkpoint {id}.");
                return;
            }
        }

        await prepare(cancellationToken).ConfigureAwait(false);
        string output = Path.Combine(directory, "Output");
        var completed = new List<OwnedFile>();
        foreach (string relative in outputFiles)
        {
            string sourceFile = GetWorkingPath(relative);
            string cached = InstallFiles.Under(output, relative);
            await InstallFiles.CopyFileAsync(sourceFile, cached, cancellationToken).ConfigureAwait(false);
            completed.Add(new OwnedFile(relative, new FileInfo(cached).Length,
                await InstallFiles.HashAsync(cached, cancellationToken).ConfigureAwait(false)));
        }
        string temp = receipt + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(completed), cancellationToken).ConfigureAwait(false);
            File.Move(temp, receipt, overwrite: true);
            Log($"Saved checkpoint {id}.");
        }
        finally { InstallFiles.TryDeleteFile(temp); }
    }

    private static async Task<bool> CheckCheckpointAsync(string directory, OwnedFile[] saved, CancellationToken token)
    {
        foreach (OwnedFile file in saved)
        {
            string path = InstallFiles.Under(Path.Combine(directory, "Output"), file.Path);
            InstallFiles.CheckNoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes ||
                !file.Sha256.Equals(await InstallFiles.HashAsync(path, token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public void Log(string message, InstallerLogLevel level = InstallerLogLevel.Info) => events.Log(step, message, level);

    public void ReportProgress(string message, long? completed = null, long? total = null) => events.Report(message, completed, total);

    public async Task RunToolAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Provide a full path to the project-owned tool.", nameof(executable));
        InstallFiles.CheckNoLinks(executable);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;

        string name = Path.GetFileName(executable);
        Log($"Starting {name}.");
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {name}.");
        Task stdout = ReadOutputAsync(process.StandardOutput, InstallerLogLevel.Info, cancellationToken);
        Task stderr = ReadOutputAsync(process.StandardError, InstallerLogLevel.Warning, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            Log($"Stopped {name}.");
            throw;
        }
        Log($"{name} exited with code {process.ExitCode}.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{name} exited with code {process.ExitCode}. See the setup log.");
    }

    private async Task ReadOutputAsync(StreamReader reader, InstallerLogLevel level, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (level == InstallerLogLevel.Info && line.StartsWith("PROGRESS\t", StringComparison.Ordinal))
            {
                string[] fields = line.Split('\t', 4);
                if (fields.Length == 4 && long.TryParse(fields[1], out long done) && long.TryParse(fields[2], out long total))
                {
                    ReportProgress(fields[3], done, total > 0 ? total : null);
                    continue;
                }
            }
            Log(line, level);
        }
    }
}

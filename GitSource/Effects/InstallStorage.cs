using Files = GameInstaller.InstallFiles;

internal sealed record CleanupGroup(string Title, string Details, long Bytes, bool RemoveByDefault, string[] Folders);

internal static class InstallStorage
{
    internal static CleanupGroup[] Inspect(string installation, InstallSession session, CancellationToken token)
    {
        string runtime = ManagedInstall.RuntimeFolder(installation);
        string effects = session.Effects;
        if (!PathsEqual(effects, Path.Combine(runtime, "Effects")) || !ManagedInstall.IsManaged(installation))
            throw new IOException("Setup couldn’t verify this installation’s storage location.");

        string Address(string relative) => Files.Under(effects, relative);
        string bin = Address("bin");
        bool interrupted = session.HasUnfinishedCutscenes;
        string[] oldCopies = ManagedInstall.HasCentralRuntime(installation)
            ? ManagedInstall.DataFolders(installation).Where(path => !PathsEqual(path, runtime) && Directory.Exists(path)).ToArray() : [];
        return new[]
        {
            Group("Download archives", "Soundtrack ZIPs and copies of setup downloads. For new work, setup can download them again.",
                !interrupted, [Path.Combine(bin, "SetupDownloads"), Path.Combine(bin, "SoundtrackDownloads"), Path.Combine(session.Tools, "Downloads")], token),
            Group("Cutscene tools and package cache", "Private Python, FFmpeg, audio packages and other tools used to make cutscenes.",
                !interrupted, new[] { "Uv", "RuntimePython", "FFmpeg", "BanditSource", "Environment-v2-cpu", "Environment-v2-cu128",
                    "Environment-v2-cpu.installing", "Environment-v2-cu128.installing", "PackageCache", "Temp",
                    "TorchCache", "ModelCache", "CudaCache" }.Select(name => Path.Combine(session.Tools, name)).ToArray(), token),
            Group("Cutscene working files", "Separated audio and intermediate video. Finished cutscenes stay. Removing these can repeat work later.",
                !interrupted, [Path.Combine(session.Cutscenes, "Dialogue"), Path.Combine(session.Cutscenes, "Pictures")], token),
            Group("Previous split storage", "An older copy remains after NeonLoop moved to a different drive. Select to remove that copy.",
                false, oldCopies, token)
        }.Where(group => group.Folders.Length != 0).ToArray();
    }

    private static CleanupGroup Group(string title, string details, bool removeByDefault, string[] paths, CancellationToken token)
    {
        var present = paths.Where(Directory.Exists).ToArray();
        long bytes = present.Sum(folder => Files.Tree(folder, token)
            .Where(item => !item.Directory).Sum(item => new FileInfo(item.Path).Length));
        return new CleanupGroup(title, details, bytes, removeByDefault, present);
    }

    internal static void Remove(string installation, CleanupGroup[] selected, CancellationToken token)
    {
        string runtime = ManagedInstall.RuntimeFolder(installation);
        string[] oldCopies = ManagedInstall.DataFolders(installation).Where(path => !PathsEqual(path, runtime)).ToArray();
        foreach (string folder in selected.SelectMany(group => group.Folders))
        {
            token.ThrowIfCancellationRequested();
            Files.CheckNoLinks(folder);
            if (oldCopies.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                ManagedInstall.RemovePreviousData(installation, folder);
                continue;
            }
            if (!Files.IsInside(folder, Path.Combine(runtime, "Effects", "bin")))
                throw new IOException("Setup couldn’t verify this cleanup location.");
            if (!Directory.Exists(folder))
                continue;
            _ = Files.Tree(folder, token).Count();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static bool PathsEqual(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

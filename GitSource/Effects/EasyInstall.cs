using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using GameInstaller;
using Files = GameInstaller.InstallFiles;

internal sealed class SetupAssets(ZipArchive package, string effects, bool localOnly, bool askEach,
    IEnumerable<InstallAsset> reviewed, Func<DownloadQuestion, DownloadDecision> ask, InstallProgress progress)
{
    internal const string Prefix = "OfflineAssets/";
    private readonly string cache = Path.Combine(effects, "bin", "SetupDownloads");
    private readonly InstallReviewAsset[] approved = reviewed.Select(AsReview).ToArray();
    private readonly Dictionary<InstallDownloadPolicy, AssetStore> stores = [];
    private InstallProgress transfer;

    internal string Get(InstallAsset asset, CancellationToken token, InstallProgress observeTransfer = null)
    {
        transfer = observeTransfer;
        try { return Resolve(asset, token); }
        finally { transfer = null; }
    }

    private string Resolve(InstallAsset asset, CancellationToken token)
    {
        InstallerAsset item = Describe(asset);
        string cached = AssetStore.CachedPath(cache, item);
        foreach (string previous in PreviousLocations(effects, asset))
            if (Matches(previous, item, token))
                return previous;
        var entry = package?.GetEntry(Prefix + item.FileName);
        if (entry != null && !Matches(cached, item, token))
        {
            Files.CheckNoLinks(cached);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            InstallFiles.EnsureSpace(cache, item.Bytes, "stage " + item.FileName);
            string partial = cached + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using (Stream input = entry.Open())
                using (var output = new FileStream(partial, FileMode.CreateNew))
                    InstallFiles.Transfer(input, output, item.Bytes,
                        done => Report("Reading " + asset.label + " from the installer", done, item.Bytes), token);
                if (!Matches(partial, item, token))
                    throw new InvalidDataException(asset.label + " in the offline installer failed verification.");
                InstallFiles.PublishFile(partial, cached);
            }
            finally { InstallFiles.RemovePartial(partial); }
        }
        bool approvedDownload = approved.Any(review => review.Id == item.Id && review.Sha256 == item.Sha256);
        var policy = localOnly ? InstallDownloadPolicy.LocalOnly :
            !askEach && approvedDownload ? InstallDownloadPolicy.ApproveReviewed : InstallDownloadPolicy.AskEachTime;
        if (!stores.TryGetValue(policy, out var store))
            stores[policy] = store = new AssetStore(cache, AppContext.BaseDirectory, new Interaction(ask),
                new InstallerEvents(null, new InlineProgress(Report)), policy, approved);
        return store.ResolveAsync(item, token).GetAwaiter().GetResult();
    }

    private void Report(string label, long done, long total)
    {
        progress(label, done, total);
        transfer?.Invoke(label, done, total);
    }

    internal static bool Embedded(ZipArchive package, InstallAsset asset) =>
        package?.GetEntry(Prefix + asset.filename)?.Length == asset.bytes;

    internal static bool HasCandidate(string effects, InstallAsset asset)
    {
        string candidate = AssetStore.CachedPath(Path.Combine(effects, "bin", "SetupDownloads"), Describe(asset));
        return PreviousLocations(effects, asset).Append(candidate).Append(Path.Combine(AppContext.BaseDirectory, asset.filename))
            .Any(path => new FileInfo(path) is { Exists: true } info && info.Length == asset.bytes);
    }

    private static IEnumerable<string> PreviousLocations(string effects, InstallAsset asset)
    {
        if (asset.key == null)
            yield return Path.Combine(effects, "bin", "CutsceneTools", "Downloads", asset.filename);
        else
            yield return Path.Combine(effects, "bin", "SoundtrackDownloads", asset.filename);
    }

    private static bool Matches(string file, InstallerAsset asset, CancellationToken token)
    {
        var info = new FileInfo(file);
        return info.Exists && info.Length == asset.Bytes &&
            Files.HashAsync(file, token).GetAwaiter().GetResult().Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static InstallReviewAsset AsReview(InstallAsset asset)
    {
        InstallerAsset item = Describe(asset);
        return new InstallReviewAsset(item.Id, item.FileName, item.Bytes, item.Sha256, item.Url,
            AssetStore.Hosts(item), item.LocalFiles, false);
    }

    private static InstallerAsset Describe(InstallAsset asset)
    {
        var uri = new Uri(asset.url);
        string[] hosts = uri.Host switch
        {
            "github.com" => ["release-assets.githubusercontent.com", "objects.githubusercontent.com"],
            "www.gyan.dev" => ["gyan.dev"],
            "download.pytorch.org" => ["download-r2.pytorch.org"],
            "download-r2.pytorch.org" => ["download.pytorch.org"],
            _ => []
        };
        return new InstallerAsset(asset.key ?? asset.filename, asset.filename, asset.bytes, asset.sha256)
            { Url = uri, RedirectHosts = hosts };
    }

    private sealed class Interaction(Func<DownloadQuestion, DownloadDecision> ask) : IInstallerInteraction
    {
        public Task<DownloadDecision> ChooseDownloadAsync(DownloadQuestion question, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(ask(question));
        }
    }

    private sealed class InlineProgress(InstallProgress progress) : IProgress<InstallerProgress>
    {
        public void Report(InstallerProgress value) => progress(value.Message, value.Completed ?? -1, value.Total ?? -1);
    }
}

internal sealed record EasyInstallRequest(string[] Libraries, string[] Movies, bool AllowLimited = false, bool Offline = false)
{
    internal static EasyInstallRequest Read(string effects)
    {
        string path = Path.Combine(effects, "EasyInstall.json");
        if (!File.Exists(path))
            return null;
        Files.CheckNoLinks(path);
        var request = JsonSerializer.Deserialize<EasyInstallRequest>(File.ReadAllText(path));
        if (request?.Libraries == null || request.Movies == null ||
            request.Libraries.Any(key => !MusicPack.Libraries.Contains(key)) ||
            request.Movies.Any(key => !request.Libraries.Contains(key)))
            throw new InvalidDataException("The saved NeonLoop setup selection is invalid.");
        return request;
    }

    internal void Save(string effects) =>
        InstallFiles.WriteText(Path.Combine(effects, "EasyInstall.json"), JsonSerializer.Serialize(this));
}

internal static class GameplayInstallSettings
{
    internal static readonly (string Group, int Bit, string Label)[] Options =
    [
        ("Dialogue", 0, "Skip only dialogue already read"), ("Dialogue", 1, "Reveal full lines instantly"),
        ("Dialogue", 2, "Conversation history"), ("Accessibility", 22, "Use readable in-game font"),
        ("Accessibility", 11, "Larger dialogue choices"), ("Controls", 8, "Keep the exploration pointer visible"),
        ("Controls", 10, "One-click selection in supported menus"),
        ("Saves", 6, "Keep three recovery copies per save slot"), ("Saves", 12, "Preview the Continue destination"),
        ("Saves", 16, "Show when a save finishes or fails"),
        ("Exploration", 5, "Reveal interactable hotspots with F8"), ("Exploration", 9, "Show the current map objective"),
        ("Exploration", 14, "Preview hotspot actions as icons"), ("Exploration", 15, "Mark visited locations"),
        ("Exploration", 17, "Track completed actions and choices"), ("Exploration", 19, "Remember the last focused item"),
        ("Puzzle assists", 4, "Retry the Chapter 2 taxi"), ("Puzzle assists", 7, "Label maintenance tunnel exits"),
        ("Puzzle assists", 13, "Label glyph symbols and positions"), ("Game startup", 18, "Reopen on the last-used monitor"),
        ("Game startup", 20, "Choose the starting window state")
    ];

    internal static string PathFor(string effects) => Path.Combine(effects, "GameplaySettings.txt");
    private static string ReviewPath(string effects) => Path.Combine(effects, "GameplayOptionsReviewed.txt");

    internal static bool WasReviewed(string effects)
    {
        string path = ReviewPath(effects);
        Files.CheckNoLinks(path);
        return File.Exists(path) && File.ReadAllText(path).Trim() == "1";
    }

    internal static uint Read(string effects)
    {
        string path = PathFor(effects);
        Files.CheckNoLinks(path);
        return GameplayDefaults.Read(path);
    }

    internal static void Ensure(string effects)
    {
        string path = PathFor(effects);
        Files.CheckNoLinks(path);
        if (!GameplayDefaults.TryRead(path, out uint flags))
            InstallFiles.WriteText(path, flags.ToString(CultureInfo.InvariantCulture));
    }

    internal static bool On(uint flags, int bit) => ((flags & (1u << bit)) != 0) != (bit == 22);
    internal static uint Set(uint flags, int bit, bool enabled) =>
        (enabled != (bit == 22)) ? flags | (1u << bit) : flags & ~(1u << bit);
    internal static void SaveReviewed(string effects, uint flags)
    {
        Files.CheckNoLinks(PathFor(effects));
        Files.CheckNoLinks(ReviewPath(effects));
        InstallFiles.WriteText(PathFor(effects), flags.ToString(CultureInfo.InvariantCulture));
        InstallFiles.WriteText(ReviewPath(effects), "1");
    }
}

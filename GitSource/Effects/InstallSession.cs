using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

internal sealed class InstallSession
{
    private const long CutsceneReserve = 2L * 1024 * 1024 * 1024;
    private readonly List<FileInfo> gameFiles;
    private readonly InstallAsset[] toolsManifest;
    private readonly List<string> results = new List<string>();
    private readonly long gameBytes;
    private readonly string[] videoNames;
    private readonly string[] sourceVideos;
    private static readonly string[] VideoVariants = { "Videos", "DynamicVideos/effects", "DynamicVideos/pitch-high",
        "DynamicVideos/pitch-low", "DynamicVideos/both-high", "DynamicVideos/both-low" };

    internal string Effects { get; private set; }
    internal SteamGame Steam { get; private set; }
    internal MusicPack Pack { get; private set; }
    internal string GameFolder { get; private set; }
    internal string Cutscenes { get; private set; }
    internal string Tools { get; private set; }
    internal string Network { get; set; }
    internal string Backend { get; set; }
    internal bool Easy { get; set; }
    internal bool LocalOnly { get; set; }
    internal bool AllowHardwareCheck { get; set; }
    internal LocalInstallerFiles LocalFiles { get; } = new LocalInstallerFiles();
    internal bool Ready { get; private set; }
    internal string[] Movies { get; set; }
    internal EasyInstallRequest Request { get; set; }
    internal ZipArchive Package { get; set; }
    internal InstallAsset[] ApprovedDownloads { get; set; } = [];
    internal bool IsOfflinePackage => Package?.Entries.Any(entry => entry.FullName.StartsWith(SetupAssets.Prefix,
        StringComparison.Ordinal)) == true;
    internal bool HasIncompleteSelection => Request != null &&
        (Request.Libraries.Any(key => !Pack.HasLibrary(key)) || Request.Movies.Any(key => !MovieReady(key)));
    internal bool CanPlay => Ready && (!HasIncompleteSelection || Request.AllowLimited);
    internal string Results { get { return string.Join(Environment.NewLine, results); } }
    internal InstallAsset[] MusicAssets { get; }
    internal string[] InstalledLibraries => MusicPack.Libraries.Where(Pack.HasLibrary).ToArray();

    internal InstallSession(string effects, string selectedFolder, InstallProgress progress, CancellationToken token)
    {
        Effects = effects;
        progress("Checking for an open game, then reading the Steam registry and library manifests.", 0, 0);
        EffectsLauncher.CheckNoGame();
        Steam = SteamGame.Find(effects, selectedFolder, token);
        GameInstaller.InstallFiles.RequireSeparate(Steam.Installed, Path.GetDirectoryName(effects)!);
        token.ThrowIfCancellationRequested();
        progress("Validating the installed Steam build and reading NeonLoop's local manifests.", 0, 0);
        Pack = new MusicPack(effects, Steam);
        GameFolder = Path.Combine(effects, "bin", "Game", Steam.BuildId);
        Cutscenes = Path.Combine(effects, "bin", "Cutscenes", Steam.BuildId);
        Tools = Path.Combine(effects, "bin", "CutsceneTools");
        MusicAssets = InstallAsset.Read(Path.Combine(effects, "CutsceneSetup", "ReleaseAssets.json"));
        if (MusicAssets.Select(asset => asset.key).Distinct().Count() != MusicAssets.Length ||
            MusicAssets.Select(asset => asset.filename).Distinct(StringComparer.OrdinalIgnoreCase).Count() != MusicAssets.Length ||
            MusicAssets.Any(asset => !MusicPack.Libraries.Contains(asset.key) || asset.expandedBytes <= 0 ||
                !InstallAssets.IsMusicDownload(asset)))
            throw new InvalidDataException("The soundtrack release manifest does not match the supported libraries.");
        toolsManifest = InstallAsset.Read(Path.Combine(effects, "CutsceneSetup", "DownloadAssets.json"));
        var videoRows = File.ReadLines(Path.Combine(effects, "CutsceneSetup", "Videos.tsv")).Skip(1)
            .Select(line => line.Split('\t')).Where(fields => fields.Length > 1).ToArray();
        sourceVideos = videoRows.Select(fields => fields[0]).ToArray();
        videoNames = videoRows.Where(fields => fields[1] == "rescored").Select(fields => fields[0]).ToArray();
        if (videoNames.Length == 0 || sourceVideos.Any(name => name != Path.GetFileName(name) ||
            !name.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The cutscene file list is missing or invalid.");
        Network = NetworkPrivacy.ReadChoice(effects) ?? "allow";
        Backend = InstallHardware.Cpu;
        Movies = [];
        Request = EasyInstallRequest.Read(effects);
        progress("Reading file sizes within the Steam game folder and checking free space on the destination drive.", 0, 0);
        gameFiles = InstallFiles.Tree(Steam.Installed, token).ToList();
        gameBytes = gameFiles.Sum(file => file.Length);
        if (gameBytes == 0 || !gameFiles.Any(file => string.Equals(file.FullName, Path.Combine(Steam.Installed, "ROM.exe"),
            StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The Steam game folder could not be read as a normal installation directory.");
        Ready = CurrentCopy && NetworkPrivacy.ReadChoice(effects) != null &&
            File.Exists(Path.Combine(GameFolder, "dxgi.dll")) &&
            Pack.IsStaged(GameFolder);
    }

    internal IEnumerable<InstallAsset> ToolsFor(string backend)
    {
        return toolsManifest.Where(asset => asset.group == "base" || asset.group == "wheel" || asset.group == backend);
    }

    private bool CurrentCopy
    {
        get
        {
            string marker = Path.Combine(GameFolder, ".copy-ready");
            return File.Exists(marker) && File.Exists(Path.Combine(GameFolder, "ROM.exe")) &&
                string.Equals(File.ReadAllText(marker).Trim(), Steam.BuildId, StringComparison.Ordinal);
        }
    }

    internal bool MovieReady(string library)
    {
        string folder = Path.Combine(Cutscenes, library);
        if (!File.Exists(Path.Combine(folder, ".ready")))
            return false;
        foreach (string video in videoNames)
            foreach (string variant in VideoVariants)
            {
                var file = new FileInfo(Path.Combine(folder, variant, video));
                if (!file.Exists || file.Length <= 1024)
                    return false;
            }
        return true;
    }

    internal void RemoveMoviePartials(string library, string backend)
    {
        foreach (InstallAsset asset in ToolsFor(backend))
            InstallFiles.RemovePartial(Path.Combine(Tools, "Downloads", asset.filename + ".part"));
        string output = Path.Combine(Cutscenes, library);
        InstallFiles.RemovePartial(Path.Combine(output, ".ready.part"));
        foreach (string name in sourceVideos)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            InstallFiles.RemovePartial(Path.Combine(Cutscenes, "Dialogue", stem + ".part"));
            InstallFiles.RemovePartial(Path.Combine(Cutscenes, "Dialogue", stem + "-dialogue-effects.partial.flac"));
            InstallFiles.RemovePartial(Path.Combine(Cutscenes, "Pictures", stem + ".partial.webm"));
            foreach (string variant in VideoVariants)
                InstallFiles.RemovePartial(Path.Combine(output, variant, stem + ".partial.webm"));
        }
    }

    internal bool NeedsMovieWork
    {
        get
        {
            return Movies.Any(key => !MovieReady(key));
        }
    }

    internal string Review(CancellationToken token)
    {
        var text = new StringBuilder();
        text.AppendLine("1. Prepare the private game copy");
        text.AppendLine("  Steam source (read only): " + Steam.Installed);
        text.AppendLine("  Target: " + GameFolder);
        text.AppendLine(CurrentCopy ? "  Reuse the private game copy and stage the current mod files." :
            "  Stage " + InstallFiles.Size(gameBytes) + " from the installed game before activating the copy.");
        text.AppendLine("  Copy the overlay, settings, shaders, textures and audio helper. Patch the private game assembly locally.");
        text.AppendLine("  Available on the destination drive: " + InstallFiles.Size(InstallFiles.Free(Effects)));
        text.AppendLine();
        text.AppendLine("2. Game network privacy");
        if (Easy)
            text.AppendLine("  Skip the firewall check and keep any existing firewall rules.");
        else
        {
            text.AppendLine(Network == "block" ? "  " + NetworkPrivacy.BlockSummary :
                "  Keep normal network access. Remove NeonLoop's private-copy firewall rules if present, with administrator approval if needed.");
            text.AppendLine("  Save your choice in " + Path.Combine(Effects, "NetworkChoice.txt"));
        }
        text.AppendLine();
        text.AppendLine("3. Install selected soundtracks and prepare replacement cutscenes");
        foreach (var asset in MusicAssets.Where(asset => Request?.Libraries.Contains(asset.key) == true))
            text.AppendLine("  " + asset.label + (Pack.HasLibrary(asset.key) ? " (already installed)" : " · " + InstallFiles.Size(asset.bytes)));
        if (Movies.Length == 0)
            text.AppendLine("  Skipped by your selection. The game can use original cutscenes.");
        else
        {
            foreach (string library in Movies)
                text.AppendLine("  " + MusicPack.Name(library) + (MovieReady(library) ? " (already prepared)" : ""));
            if (NeedsMovieWork)
            {
                text.AppendLine("  Read original Steam videos, extract verified tools and install Python packages offline in " + Tools);
                text.AppendLine("  Generate dialogue, picture and soundtrack mixes locally in " + Cutscenes + ".");
                text.AppendLine("  Use " + (AllowHardwareCheck ? "NVIDIA CUDA if the local compatibility check passes, with CPU fallback." : "CPU.") +
                    " Keep at least 2 GiB free for cutscene generation after tools are available. Actual output size varies.");
                var requested = ToolsFor(InstallHardware.Cpu).ToArray();
                text.AppendLine("  Maximum planned cutscene downloads before reuse or retries: " + InstallFiles.Size(requested.Sum(asset => asset.bytes)));
                text.AppendLine("  Tool and model download cache: " + Path.Combine(Tools, "Downloads"));
                foreach (InstallAsset asset in requested)
                {
                    token.ThrowIfCancellationRequested();
                    string save = Path.Combine(Tools, "Downloads", asset.filename);
                    text.AppendLine(asset.Describe());
                    if (File.Exists(save))
                        text.AppendLine("  Local cache candidate found. Setup verifies it before reuse.");
                }
            }
        }
        text.AppendLine();
        text.AppendLine(IsOfflinePackage || LocalOnly ? "Setup uses embedded and verified local files." :
            Easy ? "Install approves the listed downloads. NVIDIA downloads require a separate choice." :
            "Setup asks for missing downloads in Custom mode.");
        text.AppendLine("Setup records local progress in " + Path.Combine(Effects, "Install.log") + ".");
        text.AppendLine("At the end, choose Play Now to start Steam if needed and open the private game, or close the installer.");
        return text.ToString();
    }

    internal InstallAsset[] ReviewedAssets() => MusicAssets.Where(asset => Request?.Libraries.Contains(asset.key) == true &&
        !Pack.HasLibrary(asset.key)).Concat(NeedsMovieWork ? ToolsFor(InstallHardware.Cpu) : []).ToArray();

    internal InstallAsset[] DownloadsNeeded(IEnumerable<InstallAsset> assets) => assets.Where(asset =>
        !SetupAssets.Embedded(Package, asset) && !SetupAssets.HasCandidate(Effects, asset)).ToArray();

    internal void Run(Action<string, string, int, int> step, InstallProgress progress,
        Func<GameInstaller.DownloadQuestion, GameInstaller.DownloadDecision> ask,
        Func<string, string, bool> approve, CancellationToken token)
    {
        results.Clear();
        EffectsLauncher.CheckNoGame();
        var reviewed = ReviewedAssets();
        var approved = ApprovedDownloads.ToList();
        if (Easy && Request != null)
            Request.Save(Effects);
        long selectedArchives = reviewed.Where(asset => !SetupAssets.HasCandidate(Effects, asset)).Sum(asset => asset.bytes);
        long space = (Ready ? 0 : gameBytes) + selectedArchives +
            reviewed.Sum(asset => asset.expandedBytes) + (NeedsMovieWork ? CutsceneReserve * (1 + Movies.Length) : 0);
        InstallFiles.EnsureSpace(Effects, space, "prepare the selected installation");
        if (Backend == InstallHardware.Gpu && NeedsMovieWork)
        {
            var gpu = ToolsFor(InstallHardware.Gpu).Where(asset => asset.group == InstallHardware.Gpu).ToArray();
            InstallAsset[] needed = DownloadsNeeded(gpu);
            long download = needed.Sum(asset => asset.bytes);
            bool enoughSpace = InstallFiles.Free(Effects) >= space + gpu.Sum(asset => asset.bytes) * 2;
            string prompt = download == 0 ? "Use the verified NVIDIA tools already available on this PC?" :
                $"Download {InstallFiles.Size(download)} for NVIDIA processing? CPU processing is available without this download.";
            if (enoughSpace && approve("Use NVIDIA acceleration?", prompt))
                approved.AddRange(needed);
            else
            {
                if (!enoughSpace)
                    progress("Not enough space for the NVIDIA tools. Using the reviewed CPU path.", -1, -1);
                Backend = InstallHardware.Cpu;
            }
        }
        bool offline = Request?.Offline == true || IsOfflinePackage;
        var assets = new SetupAssets(Package, Effects, LocalOnly || offline, !Easy, approved, ask, progress);
        var gpuAssets = offline && Backend == InstallHardware.Gpu
            ? new SetupAssets(Package, Effects, false, false, approved, ask, progress) : assets;
        InstallFiles.WriteText(Path.Combine(Effects, "GameSource.txt"), Steam.Installed);
        var stages = new List<KeyValuePair<string, Action>>();
        stages.Add(new KeyValuePair<string, Action>("Getting the game ready…", () => PrepareCopy(progress, token)));
        stages.Add(new KeyValuePair<string, Action>("Finishing setup…", () =>
        {
            if (Easy)
            {
                if (NetworkPrivacy.ReadChoice(Effects) == null)
                    InstallFiles.WriteText(Path.Combine(Effects, "NetworkChoice.txt"), "allow");
                ResultsLine("Network: firewall check skipped.", progress);
            }
            else
            {
                progress("Applying your private-copy network choice. Windows may ask for administrator approval.", 0, 0);
                NetworkPrivacy.Ensure(Effects, Steam.BuildId, Network);
                InstallFiles.WriteText(Path.Combine(Effects, "NetworkChoice.txt"), Network);
                ResultsLine(Network == "block" ? "Network: private-copy outbound block applied." : "Network: normal access selected.", progress);
            }
            Ready = true;
        }));
        foreach (InstallAsset soundtrack in MusicAssets.Where(asset => Request?.Libraries.Contains(asset.key) == true &&
            !Pack.HasLibrary(asset.key)))
        {
            var asset = soundtrack;
            stages.Add(new KeyValuePair<string, Action>(asset.label, () => InstallPack(assets.Get(asset, token), progress, token)));
        }
        foreach (string library in Movies)
        {
            string key = library;
            stages.Add(new KeyValuePair<string, Action>(MusicPack.Name(key) + " cutscenes",
                () => PrepareMovies(key, assets, gpuAssets, progress, token)));
        }
        for (int i = 0; i < stages.Count; ++i)
        {
            token.ThrowIfCancellationRequested();
            step(stages[i].Key, i + 1 < stages.Count ? stages[i + 1].Key : "Review the result", i, stages.Count);
            stages[i].Value();
        }
        foreach (string key in MusicPack.Libraries)
            if (Pack.HasLibrary(key))
                ResultsLine("Available soundtrack: " + MusicPack.Name(key), progress);
        if (Easy && HasIncompleteSelection)
            throw new IOException("Some selected soundtracks or cutscenes still need setup.");
        if (Easy && Request != null)
            SelectSoundtrack(preserve: true);
        step("Installation complete", "Play Now or Close", stages.Count, stages.Count);
    }

    internal void SelectSoundtrack(bool preserve)
    {
        string choice = Path.Combine(Effects, "MusicLibrary.txt");
        var installed = Request.Libraries.Where(Pack.HasLibrary).ToArray();
        string current = File.Exists(choice) ? File.ReadLines(choice).FirstOrDefault()?.Trim() : null;
        if (preserve && (current == "original" || installed.Contains(current)))
            return;
        InstallFiles.WriteText(choice, installed.FirstOrDefault() ?? "original");
    }

    internal void InstallPack(string path, InstallProgress progress, CancellationToken token)
    {
        InstallAsset asset = InstallAssets.Import(Pack, MusicAssets, path, progress, token);
        Pack = new MusicPack(Effects, Steam);
        progress("Installed " + asset.label + ". Choose it in F10 > Music when you play.", -1, -1);
    }

    private void PrepareMovies(string library, SetupAssets assets, SetupAssets gpuAssets, InstallProgress progress, CancellationToken token)
    {
        string name = MusicPack.Name(library);
        if (!Pack.HasLibrary(library))
        {
            throw new IOException("Install " + name + " before preparing its cutscenes.");
        }
        if (MovieReady(library))
        {
            ResultsLine("Reused prepared " + name + " cutscenes.", progress);
            return;
        }
        if (Backend == InstallHardware.Gpu)
        {
            try { CutsceneSetup.Run(this, library, Backend, gpuAssets, progress, token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                progress("NVIDIA processing couldn’t finish. Using the reviewed CPU tools. " + error.Message, -1, -1);
                Backend = InstallHardware.Cpu;
            }
        }
        if (Backend == InstallHardware.Cpu && !MovieReady(library))
            CutsceneSetup.Run(this, library, Backend, assets, progress, token);
        if (!MovieReady(library))
            throw new IOException(name + " cutscenes could not be verified.");
        ResultsLine("Prepared " + name + " cutscenes.", progress);
    }

    private void PrepareCopy(InstallProgress progress, CancellationToken token)
    {
        bool reuse = CurrentCopy && Pack.IsStaged(GameFolder);
        if (reuse)
        {
            ResultsLine("Reused the verified private game copy.", progress);
            return;
        }
        string staging = GameFolder + ".installing";
        string work = reuse ? GameFolder : staging;
        if (!reuse)
        {
            progress("Preparing staging folder " + staging, 0, 0);
            InstallFiles.ClearOwnedFolder(staging);
            InstallFiles.EnsureSpace(Effects, gameBytes, "copy the Steam game");
        }
        try
        {
            if (!reuse)
            {
                long copied = 0;
                foreach (FileInfo file in gameFiles)
                {
                    token.ThrowIfCancellationRequested();
                    string relative = file.FullName.Substring(Steam.Installed.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                    string label = "Copying " + relative + " to " + staging;
                    progress(label, copied, gameBytes);
                    InstallFiles.Copy(file, Path.Combine(work, relative), part => progress(label, copied + part, gameBytes), token);
                    copied += file.Length;
                }
            }
            progress("Staging ReShade, NeonLoop, shaders, and textures in " + work, 0, 0);
            string runtime = Path.Combine(Effects, "bin", "Runtime");
            StageFile(Path.Combine(runtime, "dxgi.dll"), work, progress, token);
            StageFile(Path.Combine(runtime, "ROMEffects.addon64"), work, progress, token);
            StageFile(Path.Combine(Effects, "ReShade.ini"), work, progress, token);
            token.ThrowIfCancellationRequested();
            InstallFiles.WriteText(Path.Combine(work, "steam_appid.txt"), SteamGame.AppId + Environment.NewLine);
            StageFolder(Path.Combine(Effects, "Shaders"), Path.Combine(work, "ROMShaders"), progress, token);
            StageFolder(Path.Combine(runtime, "Shaders"), Path.Combine(work, "ROMShaders"), progress, token);
            StageFolder(Path.Combine(runtime, "Textures"), Path.Combine(work, "ROMTextures"), progress, token);
            progress("Preparing the audio helper and patching only the private managed assembly in " + work, 0, 0);
            Pack.Stage(Steam.Installed, work);
            token.ThrowIfCancellationRequested();
            if (!reuse)
            {
                InstallFiles.WriteText(Path.Combine(work, ".copy-ready"), Steam.BuildId);
                progress("Activating the completed private game copy in " + GameFolder, 0, 0);
                InstallFiles.PublishFolder(staging, GameFolder);
            }
            ResultsLine(reuse ? "Reused the private game copy and staged the current mod." : "Installed the private game copy and mod.", progress);
        }
        finally { if (!reuse) InstallFiles.ClearOwnedFolder(staging); }
    }

    private static void StageFolder(string from, string to, InstallProgress progress, CancellationToken token)
    {
        foreach (FileInfo file in InstallFiles.Tree(from, token))
        {
            string relative = file.FullName.Substring(from.Length + 1);
            string target = Path.Combine(to, relative);
            progress("Staging " + target, 0, 0);
            InstallFiles.Copy(file, target, null, token);
        }
    }

    private static void StageFile(string from, string to, InstallProgress progress, CancellationToken token)
    {
        string target = Path.Combine(to, Path.GetFileName(from));
        progress("Staging " + target, 0, 0);
        InstallFiles.Copy(new FileInfo(from), target, null, token);
    }

    private void ResultsLine(string line, InstallProgress progress)
    {
        results.Add(line);
        progress(line, -1, -1);
    }
}

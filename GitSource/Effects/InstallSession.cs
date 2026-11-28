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
    private readonly string destinationEffects;
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
    internal bool CanPlayDuringConversion => Ready &&
        (Request == null || Request.Libraries.All(Pack.HasLibrary));
    internal string Results { get { return string.Join(Environment.NewLine, results); } }
    internal InstallAsset[] MusicAssets { get; }
    internal string[] InstalledLibraries => MusicPack.Libraries.Where(Pack.HasLibrary).ToArray();

    internal InstallSession(string effects, string selectedFolder, InstallProgress progress, CancellationToken token,
        ZipArchive preview = null, SteamGame verifiedGame = null, string destinationEffects = null)
    {
        Effects = effects;
        this.destinationEffects = destinationEffects ?? effects;
        progress("Checking for an open game, then reading the Steam registry and library manifests.", 0, 0);
        EffectsLauncher.CheckNoGame();
        Steam = verifiedGame ?? SteamGame.Find(effects, selectedFolder, token);
        GameInstaller.InstallFiles.RequireSeparate(Steam.Installed, Path.GetDirectoryName(effects)!);
        token.ThrowIfCancellationRequested();
        progress("Validating the installed Steam build and reading NeonLoop's local manifests.", 0, 0);
        string Read(string name) => preview == null ? File.ReadAllText(Path.Combine(effects, name)) : SetupPackage.ReadEntry(preview, name);
        string[] Lines(string name) => Read(name).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Pack = preview == null ? new MusicPack(effects, Steam) :
            new MusicPack(effects, Steam, Lines("bin/MusicPack/Ready.txt"), Lines("bin/MusicPack/Cues.tsv"));
        GameFolder = Path.Combine(effects, "bin", "Game", Steam.BuildId);
        Cutscenes = Path.Combine(effects, "bin", "Cutscenes", Steam.BuildId);
        Tools = Path.Combine(effects, "bin", "CutsceneTools");
        MusicAssets = InstallAsset.Parse(Read("CutsceneSetup/ReleaseAssets.json"));
        if (MusicAssets.Select(asset => asset.key).Distinct().Count() != MusicAssets.Length ||
            MusicAssets.Select(asset => asset.filename).Distinct(StringComparer.OrdinalIgnoreCase).Count() != MusicAssets.Length ||
            MusicAssets.Any(asset => !MusicPack.Libraries.Contains(asset.key) || asset.expandedBytes <= 0 ||
                !InstallAssets.IsMusicDownload(asset)))
            throw new InvalidDataException("The soundtrack release manifest does not match the supported libraries.");
        toolsManifest = InstallAsset.Parse(Read("CutsceneSetup/DownloadAssets.json"));
        var videoRows = Lines("CutsceneSetup/Videos.tsv").Skip(1)
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
        Ready = preview == null && CurrentCopy && NetworkPrivacy.ReadChoice(effects) != null &&
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

    private bool conversionInterrupted;

    internal bool HasUnfinishedCutscenes => conversionInterrupted ||
        MusicPack.Libraries.Any(key => Directory.Exists(Path.Combine(Cutscenes, key)) && !MovieReady(key));

    internal string Review(CancellationToken token)
    {
        var text = new StringBuilder();
        text.AppendLine("1. Prepare the private game copy");
        text.AppendLine("  Steam source (read only): " + Steam.Installed);
        text.AppendLine("  Target: " + Path.Combine(destinationEffects, "bin", "Game", Steam.BuildId));
        text.AppendLine(CurrentCopy ? "  Reuse the private game copy and stage the current mod files." :
            "  Stage " + InstallFiles.Size(gameBytes) + " from the installed game before activating the copy.");
        text.AppendLine("  Copy the overlay, settings, shaders, textures and audio helper. Patch the private game assembly locally.");
        text.AppendLine("  Available on the destination drive: " + InstallFiles.Size(InstallFiles.Free(destinationEffects)));
        text.AppendLine("  Downloads: " + Path.Combine(destinationEffects, "bin", "SetupDownloads"));
        text.AppendLine("  Optional conversion tools and model cache: " + Path.Combine(destinationEffects, "bin", "CutsceneTools"));
        if (!Path.GetDirectoryName(Effects)!.Equals(Path.GetDirectoryName(destinationEffects), StringComparison.OrdinalIgnoreCase))
            text.AppendLine("  Bring existing NeonLoop data into the selected install folder from " + Path.GetDirectoryName(Effects));
        text.AppendLine();
        text.AppendLine("2. Game network privacy");
        if (Easy)
            text.AppendLine("  Skip the firewall check and keep any existing firewall rules.");
        else
        {
            text.AppendLine(Network == "block" ? "  " + NetworkPrivacy.BlockSummary :
                "  Keep normal network access. Remove NeonLoop's private-copy firewall rules if present, with administrator approval if needed.");
            text.AppendLine("  Save your choice in " + Path.Combine(destinationEffects, "NetworkChoice.txt"));
        }
        text.AppendLine();
        text.AppendLine("3. Install selected soundtrack packs");
        foreach (var asset in MusicAssets.Where(asset => Request?.Libraries.Contains(asset.key) == true))
            text.AppendLine("  " + asset.label + (Pack.HasLibrary(asset.key) ? " (already installed)" : " · " + InstallFiles.Size(asset.bytes)));
        text.AppendLine();
        text.AppendLine("4. Optional cutscene conversion");
        if (Movies.Length == 0)
            text.AppendLine("  Leave cutscene audio as it is. Selected soundtrack packs still replace music during gameplay.");
        else
        {
            foreach (string library in Movies)
                text.AppendLine("  " + MusicPack.Name(library) + (MovieReady(library) ? " (already prepared)" : ""));
            if (NeedsMovieWork)
            {
                text.AppendLine("  Python and uv set up local tools. PyTorch and the Bandit model separate dialogue and effects from the original music.");
                text.AppendLine("  FFmpeg and NeonLoop render cutscene variants with the chosen soundtrack, pitch and audio effects.");
                text.AppendLine("  Processing happens on this PC. Tools: " + Tools + ". Output: " + Cutscenes + ".");
                text.AppendLine("  " + ProcessorSummary + " Keep at least 2 GiB free after tools are available. Actual output size varies.");
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
        text.AppendLine("Setup records local progress in " + Path.Combine(destinationEffects, "Install.log") + ".");
        text.AppendLine("At the end, choose Play Now to start Steam if needed and open the private game, or close the installer.");
        return text.ToString();
    }

    internal InstallAsset[] ReviewedAssets() => MusicAssets.Where(asset => Request?.Libraries.Contains(asset.key) == true &&
        !Pack.HasLibrary(asset.key)).Concat(NeedsMovieWork ? ToolsFor(InstallHardware.Cpu) : []).ToArray();

    internal InstallAsset[] DownloadsNeeded(IEnumerable<InstallAsset> assets) => assets.Where(asset =>
        !SetupAssets.Embedded(Package, asset) && !SetupAssets.HasCandidate(Effects, asset)).ToArray();

    internal const string CutscenePurpose = "Soundtrack packs replace music during gameplay. Optional conversion also replaces music in cutscenes while keeping dialogue and sound effects.";
    internal const string ProcessingChoices = "A compatible NVIDIA GPU can separate audio much faster using CUDA. Without CUDA, setup can use the CPU, which may take several hours. Both paths render the finished clips separately.";
    internal const string CpuDecision = "Use CPU processing instead?";
    internal const string CpuWarning = "CUDA acceleration is off, unavailable, or could not finish. If you continue, your CPU separates the cutscene audio. That can take several hours and may slow the game. You can play while setup works or skip conversion. Your chosen soundtrack works during gameplay, and unfinished cutscenes keep their original audio.";
    internal string ProcessorSummary => AllowHardwareCheck
        ? "Setup checks for a compatible NVIDIA GPU and asks before downloading CUDA tools. CUDA can speed up audio separation. If CUDA is unavailable, declined, or fails, setup asks whether to use the slower CPU path."
        : "The NVIDIA check is off. If you approve conversion, your CPU will separate the audio. This can take several hours, especially for multiple soundtracks.";

    internal string DownloadPurpose(string filename)
    {
        if (MusicAssets.FirstOrDefault(asset => asset.filename == filename) is { } soundtrack)
            return soundtrack.label + " adds a selectable soundtrack for gameplay.";
        var tool = toolsManifest.FirstOrDefault(asset => asset.filename == filename);
        return tool?.label switch
        {
            "Bandit cutscene model" => "The Bandit model separates dialogue and sound effects from the original cutscene music on this PC.",
            "FFmpeg video tools" => "FFmpeg reads the Steam cutscene videos and writes the replacement clips.",
            "uv setup tool" or "Python 3.12.13" => tool.label + " sets up the private environment for cutscene conversion.",
            "PyTorch (CPU)" => "This PyTorch package runs the cutscene audio separator on the CPU.",
            "PyTorch (NVIDIA GPU)" => "This PyTorch package runs the cutscene audio separator on a compatible NVIDIA GPU.",
            _ => tool == null ? "" : tool.label + " supports the local cutscene audio tools."
        };
    }

    internal void Run(Action<string, string, int, int> step, InstallProgress progress,
        Func<GameInstaller.DownloadQuestion, CancellationToken, GameInstaller.DownloadDecision> ask,
        Func<string, string, CancellationToken, bool> approve, Action<CutsceneUpdate> timing,
        CancellationToken token, CancellationToken cutsceneToken)
    {
        results.Clear();
        EffectsLauncher.CheckNoGame();
        GameplayInstallSettings.Ensure(Effects);
        var reviewed = ReviewedAssets();
        var approved = ApprovedDownloads.ToList();
        if (Request != null)
            Request.Save(Effects);
        long baseSpace = (Ready ? 0 : gameBytes) + RequiredAssetSpace(reviewed.Where(asset => asset.key != null));
        InstallFiles.EnsureSpace(Effects, baseSpace, "prepare the game and selected soundtracks");
        bool offline = Request?.Offline == true || IsOfflinePackage;
        var assets = new SetupAssets(Package, Effects, LocalOnly || offline, !Easy, approved, question => ask(question, token), progress);
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
        if (Request != null)
            stages.Add(new("Activating your soundtrack…", () => SelectSoundtrack(preserve: true)));
        bool cutscenesSkipped = false;
        bool cpuApproved = false;
        SetupAssets movieAssets = null;
        SetupAssets gpuAssets = null;
        var selectedMovies = Movies.Where(key => !MovieReady(key)).ToArray();
        bool ApproveCpu()
        {
            if (cpuApproved)
                return true;
            long bytes = DownloadsNeeded(ToolsFor(InstallHardware.Cpu)).Sum(asset => asset.bytes);
            string download = bytes > 0 && !LocalOnly && !offline
                ? " Setup may still need up to " + InstallFiles.Size(bytes) + " for the CPU tools."
                : "";
            cpuApproved = approve(CpuDecision, CpuWarning + download, cutsceneToken);
            if (cpuApproved)
                InstallFiles.EnsureSpace(Effects, MovieSpace(InstallHardware.Cpu), "prepare CPU cutscenes");
            return cpuApproved;
        }
        if (selectedMovies.Length > 0)
            stages.Add(new("Choose cutscene processing", () =>
            {
                if (cutsceneToken.IsCancellationRequested)
                    return;
                try
                {
                    Backend = ChooseMovieBackend(approve, approved, progress, cutsceneToken);
                    cutscenesSkipped = Backend == InstallHardware.Cpu && !ApproveCpu();
                    if (cutscenesSkipped)
                        return;
                    movieAssets = new SetupAssets(Package, Effects, LocalOnly || offline, !Easy, approved,
                        question => ask(question, cutsceneToken), progress);
                    gpuAssets = offline && Backend == InstallHardware.Gpu
                        ? new SetupAssets(Package, Effects, false, false, approved, question => ask(question, cutsceneToken), progress) : movieAssets;
                }
                catch (OperationCanceledException) when (cutsceneToken.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    cutscenesSkipped = true;
                }
            }));
        foreach (string library in Movies)
        {
            string key = library;
            stages.Add(new KeyValuePair<string, Action>(MusicPack.Name(key) + " cutscenes",
                () =>
                {
                    if (cutscenesSkipped || cutsceneToken.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        cutscenesSkipped = true;
                        return;
                    }
                    try { cutscenesSkipped = !PrepareMovies(key, movieAssets, gpuAssets, progress, timing, ApproveCpu, cutsceneToken); }
                    catch (OperationCanceledException) when (cutsceneToken.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        cutscenesSkipped = true;
                    }
                }));
        }
        for (int i = 0; i < stages.Count; ++i)
        {
            token.ThrowIfCancellationRequested();
            step(stages[i].Key, i + 1 < stages.Count ? stages[i + 1].Key : "Review the result", i, stages.Count);
            stages[i].Value();
        }
        if (cutscenesSkipped)
        {
            conversionInterrupted = true;
            if (Request != null)
            {
                Request = Request with { Movies = Request.Movies.Where(key => !Movies.Contains(key) || MovieReady(key)).ToArray() };
                Request.Save(Effects);
            }
            ResultsLine("Skipped unfinished cutscene conversion. Installed soundtracks and completed cutscenes are kept.", progress);
        }
        foreach (string key in MusicPack.Libraries)
            if (Pack.HasLibrary(key))
                ResultsLine("Available soundtrack: " + MusicPack.Name(key), progress);
        if (Easy && HasIncompleteSelection)
            throw new IOException("Some selected soundtracks or cutscenes still need setup.");
        step("Installation complete", "Review setup storage", stages.Count, stages.Count);
    }

    private long RequiredAssetSpace(IEnumerable<InstallAsset> assets) => assets.Sum(asset =>
        asset.expandedBytes + (SetupAssets.HasCandidate(Effects, asset) ? 0 : asset.bytes));

    private long MovieSpace(string backend) => RequiredAssetSpace(ToolsFor(backend)) + CutsceneReserve * (1 + Movies.Length);

    private string ChooseMovieBackend(Func<string, string, CancellationToken, bool> approve,
        List<InstallAsset> approved, InstallProgress progress, CancellationToken token)
    {
        string backend = InstallHardware.Choose(AllowHardwareCheck, progress, token);
        if (backend != InstallHardware.Gpu)
            return InstallHardware.Cpu;
        var gpu = ToolsFor(backend).Where(asset => asset.group == backend).ToArray();
        if (InstallFiles.Free(Effects) < MovieSpace(backend) + gpu.Sum(asset => asset.bytes))
        {
            progress("There isn’t enough room for the NVIDIA tools. Setup can offer CPU conversion.", 0, 0);
            return InstallHardware.Cpu;
        }
        InstallAsset[] needed = DownloadsNeeded(gpu);
        long download = needed.Sum(asset => asset.bytes);
        string prompt = download == 0 ? "Setup can reuse the local NVIDIA CUDA tools." :
            $"Download {InstallFiles.Size(download)} for the NVIDIA CUDA edition of PyTorch.";
        if (!approve("Use NVIDIA CUDA acceleration?", prompt + " CUDA can separate audio much faster on your GPU. Rendering the finished clips is a separate stage. You can also use the CPU or skip conversion.", token))
            return InstallHardware.Cpu;
        approved.AddRange(needed);
        return backend;
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

    private bool PrepareMovies(string library, SetupAssets assets, SetupAssets gpuAssets, InstallProgress progress,
        Action<CutsceneUpdate> timing, Func<bool> approveCpu, CancellationToken token)
    {
        string name = MusicPack.Name(library);
        if (!Pack.HasLibrary(library))
        {
            throw new IOException("Install " + name + " before preparing its cutscenes.");
        }
        if (MovieReady(library))
        {
            ResultsLine("Reused prepared " + name + " cutscenes.", progress);
            return true;
        }
        if (Backend == InstallHardware.Gpu)
        {
            try { CutsceneSetup.Run(this, library, Backend, gpuAssets, progress, timing, token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                progress("NVIDIA couldn’t finish. Setup will ask before using the CPU. " + error.Message, 0, 0);
                if (!approveCpu())
                    return false;
                Backend = InstallHardware.Cpu;
            }
        }
        if (Backend == InstallHardware.Cpu && !MovieReady(library))
            CutsceneSetup.Run(this, library, Backend, assets, progress, timing, token);
        if (!MovieReady(library))
            throw new IOException(name + " cutscenes could not be verified.");
        ResultsLine("Prepared " + name + " cutscenes.", progress);
        return true;
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

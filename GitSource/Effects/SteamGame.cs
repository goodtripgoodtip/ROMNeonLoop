using GameInstaller;

internal sealed class SteamGame
{
    internal const string AppId = "330820";
    internal string Steam { get; private set; }
    internal string Installed { get; private set; }
    internal string BuildId { get; private set; }

    internal static SteamGame Find(string effects, string selectedFolder = null, CancellationToken token = default)
    {
        string[] seal = File.ReadAllLines(Path.Combine(effects, "bin", "MusicPack", "Ready.txt"));
        return FindSupported(seal, selectedFolder, token);
    }

    internal static SteamGame FindSupported(string[] seal, string selectedFolder = null, CancellationToken token = default)
    {
        if (seal.Length != 3 || !GameInstaller.InstallFiles.IsHash(seal[1]) || !GameInstaller.InstallFiles.IsHash(seal[2]))
            throw new InvalidDataException("NeonLoop’s supported Steam build information is missing or invalid.");
        var source = new GameSource("ROM.exe")
        {
            DisplayName = "2064: Read Only Memories",
            SteamAppId = 330820,
            SteamInstallDirectoryName = "Read Only Memories",
            SupportedBuildIds = [seal[0]],
            FileHashes = new Dictionary<string, string>
            {
                ["ROM_Data/Managed/Assembly-CSharp.dll"] = seal[1],
                ["ROM_Data/globalgamemanagers"] = seal[2]
            }
        };
        ResolvedGame game = SteamDiscovery.ResolveAsync(source, selectedFolder, token).GetAwaiter().GetResult();
        string library = Directory.GetParent(game.Directory)?.Parent?.Parent?.FullName;
        return new SteamGame { Steam = SteamDiscovery.FindClientDirectory(library), Installed = game.Directory, BuildId = game.BuildId ?? seal[0] };
    }

    internal static string SelectedFolder(string effects)
    {
        string path = Path.Combine(effects, "GameSource.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}

using System.Text;

internal sealed class LocalInstallerFiles
{
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> checkedFiles = new(StringComparer.OrdinalIgnoreCase);

    internal string LastFolder { get; private set; }

    internal LocalInstallerFiles()
    {
        string launcher = AppContext.BaseDirectory;
        folders.Add(launcher);
        string parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(launcher))?.FullName;
        if (parent != null)
            folders.Add(parent);
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        LastFolder = Directory.Exists(downloads) ? downloads : launcher;
    }

    internal string InFolder(string folder, string filename)
    {
        string directory = Path.GetFullPath(folder);
        GameInstaller.InstallFiles.CheckNoLinks(directory);
        folders.Add(directory);
        LastFolder = directory;
        string path = GameInstaller.InstallFiles.Under(directory, filename);
        return File.Exists(path) ? path : null;
    }

    internal string UseFile(string path, string hash)
    {
        string file = Path.GetFullPath(path);
        GameInstaller.InstallFiles.CheckNoLinks(file);
        string folder = Path.GetDirectoryName(file)!;
        folders.Add(folder);
        LastFolder = folder;
        checkedFiles.Add(hash + "|" + file);
        return Encode(file);
    }

    internal string Find(string filename, string hash)
    {
        foreach (string folder in folders.OrderByDescending(folder => folder.Equals(LastFolder, StringComparison.OrdinalIgnoreCase)))
        {
            string file = GameInstaller.InstallFiles.Under(folder, filename);
            if (!File.Exists(file) || !checkedFiles.Add(hash + "|" + file))
                continue;
            try { GameInstaller.InstallFiles.CheckNoLinks(file); }
            catch (InvalidDataException) { continue; }
            return Encode(file);
        }
        return null;
    }

    private static string Encode(string file) => "file\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(file));
}

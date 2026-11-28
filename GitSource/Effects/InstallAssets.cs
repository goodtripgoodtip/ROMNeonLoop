using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Text.Json;

internal sealed class InstallAsset
{
    public string key { get; set; }
    public string label { get; set; }
    public string filename { get; set; }
    public string url { get; set; }
    public string sha256 { get; set; }
    public string group { get; set; }
    public long bytes { get; set; }
    public long expandedBytes { get; set; }

    internal string Describe()
    {
        return label + "  |  " + InstallFiles.Size(bytes) + Environment.NewLine +
            "  " + filename + Environment.NewLine + "  " + url + Environment.NewLine + "  SHA-256 " + sha256;
    }

    internal static InstallAsset[] Parse(string text)
    {
        var assets = JsonSerializer.Deserialize<InstallAsset[]>(text);
        if (assets == null)
            throw new InvalidDataException("The installer download list is missing.");
        foreach (InstallAsset asset in assets)
        {
            Uri source;
            if (asset == null || string.IsNullOrWhiteSpace(asset.filename) ||
                asset.filename != Path.GetFileName(asset.filename) || asset.bytes <= 0 ||
                string.IsNullOrEmpty(asset.sha256) || asset.sha256.Length != 64 ||
                !Uri.TryCreate(asset.url, UriKind.Absolute, out source) || source.Scheme != Uri.UriSchemeHttps ||
                !GameInstaller.InstallFiles.IsHash(asset.sha256))
                throw new InvalidDataException("The installer has an invalid download entry.");
        }
        return assets;
    }
}

internal static class InstallAssets
{
    internal static bool IsMusicDownload(InstallAsset asset)
    {
        var uri = new Uri(asset.url);
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.EndsWith("/" + asset.filename, StringComparison.Ordinal) &&
            uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal) &&
            asset.filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    internal static InstallAsset Import(MusicPack pack, InstallAsset[] assets, string path,
        InstallProgress progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GameInstaller.InstallFiles.CheckNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var candidates = assets.Where(asset => asset.bytes == stream.Length).ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("Choose a complete music pack ZIP from this NeonLoop release.");

        progress("Checking the music pack checksum…", 0, 0);
        string checksum = Convert.ToHexString(SHA256.HashData(stream));
        InstallAsset match = candidates.FirstOrDefault(asset => checksum.Equals(asset.sha256, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new InvalidDataException("The ZIP does not match a music pack from this NeonLoop release.");
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        Install(pack, match, stream, progress, token);
        return match;
    }

    private static void Install(MusicPack pack, InstallAsset asset, Stream verified, InstallProgress progress, CancellationToken token)
    {
        string destination = Path.Combine(pack.Root, asset.key);
        string staging = destination + ".installing";
        string prefix = "Effects/bin/MusicPack/" + asset.key + "/";
        string[] cues = File.ReadAllLines(Path.Combine(pack.Root, "Cues.tsv"));
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CueCredits.tsv" };
        for (int i = 1; i < cues.Length; ++i)
            entries.Add("Cues/" + cues[i].Split('\t')[0] + ".mp3");
        InstallFiles.EnsureSpace(pack.Root, asset.expandedBytes, "extract " + asset.label);
        InstallFiles.ClearOwnedFolder(staging);
        Directory.CreateDirectory(Path.Combine(staging, "Cues"));
        try
        {
            long extracted = 0;
            using (var zip = new ZipArchive(verified, ZipArchiveMode.Read, true))
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.FullName;
                    if (name == prefix || name == prefix + "Cues/") continue;
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) || !entries.Remove(name.Substring(prefix.Length)) ||
                        entry.Length <= 0 || entry.Length > asset.expandedBytes - extracted)
                        throw new InvalidDataException("Unexpected soundtrack ZIP entry: " + name);
                    string relative = name.Substring(prefix.Length);
                    progress("Extracting " + relative + " to " + staging, extracted, asset.expandedBytes);
                    using (Stream source = entry.Open())
                    using (var output = new FileStream(Path.Combine(staging, relative), FileMode.CreateNew))
                        InstallFiles.Transfer(source, output, entry.Length, null, token);
                    extracted += entry.Length;
                }
            if (entries.Count != 0 || extracted != asset.expandedBytes || !MusicPack.HasCuesIn(staging, cues))
                throw new InvalidDataException("The archive does not contain the complete soundtrack.");
            progress("Activating " + asset.label + " in " + destination, extracted, extracted);
            InstallFiles.PublishFolder(staging, destination);
        }
        finally { InstallFiles.ClearOwnedFolder(staging); }
    }
}

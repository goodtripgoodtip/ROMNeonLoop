using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameInstaller;

public sealed record OfflinePackageAsset(InstallerAsset Asset, string File);

public sealed record InstallerPackageOptions(string Bootstrap, string PayloadDirectory, string Output, string ProductId, string Version)
{
    public IReadOnlyList<OfflinePackageAsset> OfflineAssets { get; init; } = [];
    public bool Offline { get; init; }
    public string? InstalledLauncher { get; init; }
}

public sealed class InstallerPackage
{
    private sealed record Entry(string Name, long Bytes, string Sha256);
    private sealed record Manifest(string ProductId, string Version, bool Offline, Entry[] Files);
    private const string ManifestName = "InstallerPackage.json";
    private const string LauncherName = "Launcher.exe";
    private const int FooterSize = 48;
    private static ReadOnlySpan<byte> Magic => "GINSTPK1"u8;
    private readonly long offset;
    private readonly long length;
    private readonly Dictionary<string, Entry> files;

    private InstallerPackage(string executable, long offset, long length, string fingerprint, Manifest manifest)
    {
        Executable = executable;
        this.offset = offset;
        this.length = length;
        Fingerprint = fingerprint;
        ProductId = manifest.ProductId;
        Version = manifest.Version;
        Offline = manifest.Offline;
        files = manifest.Files.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Executable { get; }
    public string ProductId { get; }
    public string Version { get; }
    public bool Offline { get; }
    public string Fingerprint { get; }

    public Task CopyLauncherAsync(string target, CancellationToken cancellationToken = default) => HasLauncher
        ? ExtractLauncherAsync(Path.GetFullPath(target), cancellationToken)
        : throw new InvalidOperationException("The package does not contain a separate installed launcher.");

    public static InstallerPackage? Open(string executable)
    {
        executable = Path.GetFullPath(executable);
        using FileStream input = File.OpenRead(executable);
        Span<byte> footer = stackalloc byte[FooterSize];
        long end = input.Length;
        if (!ReadFooter(input, end, footer))
        {
            input.Position = 0;
            using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
            var certificate = pe.PEHeaders.PEHeader?.CertificateTableDirectory;
            if (certificate is not { RelativeVirtualAddress: > 0, Size: >= 8 } table ||
                (long)table.RelativeVirtualAddress + table.Size != input.Length)
                return null;
            end = table.RelativeVirtualAddress;
            bool found = false;
            for (int pad = 0; pad <= 7 && end > FooterSize; pad++, end--)
            {
                if (ReadFooter(input, end, footer))
                {
                    found = true;
                    break;
                }
                input.Position = end - 1;
                if (input.ReadByte() != 0)
                    break;
            }
            if (!found)
                return null;
        }

        long length = BinaryPrimitives.ReadInt64LittleEndian(footer[8..]);
        long dataEnd = end - FooterSize;
        if (length <= 0 || length >= dataEnd)
            throw new InvalidDataException("The setup package length is invalid.");
        long offset = dataEnd - length;
        string hash = Convert.ToHexString(footer[16..]);
        using var section = new PackageSection(input, offset, length);
        if (!Convert.ToHexString(SHA256.HashData(section)).Equals(hash, StringComparison.Ordinal))
            throw new InvalidDataException("The setup package checksum does not match.");
        section.Position = 0;
        using var archive = new ZipArchive(section, ZipArchiveMode.Read);
        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("The setup manifest is missing.");
        if (manifestEntry.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The setup manifest is too large.");
        using Stream manifestStream = manifestEntry.Open();
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(manifestStream) ?? throw new InvalidDataException("The setup manifest is empty.");
        ValidateManifest(archive, manifest);
        return new InstallerPackage(executable, offset, length, hash, manifest);
    }

    public static async Task BuildAsync(InstallerPackageOptions options, CancellationToken cancellationToken = default)
    {
        InstallFiles.CheckSegment(options.ProductId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);
        string bootstrap = Path.GetFullPath(options.Bootstrap);
        string payload = Path.GetFullPath(options.PayloadDirectory);
        string output = Path.GetFullPath(options.Output);
        InstallFiles.RequireSeparate(output, payload);
        InstallFiles.RequireSeparate(bootstrap, output);
        InstallFiles.CheckNoLinks(bootstrap);
        InstallFiles.CheckNoLinks(output);
        if (!Directory.Exists(payload) || (!options.Offline && options.OfflineAssets.Count != 0))
            throw new ArgumentException("Provide a payload folder and use embedded optional assets only for an offline package.");
        if (Open(bootstrap) is not null)
            throw new ArgumentException("A setup package must be built from a clean, unpackaged bootstrap.");
        string directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        string scratch = Path.Combine(directory, $".package-{Guid.NewGuid():N}");
        string zip = scratch + ".zip";
        try
        {
            await WriteArchiveAsync(zip, payload, options, cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await using var app = File.OpenRead(bootstrap);
                await app.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await using var package = File.OpenRead(zip);
                byte[] hash = await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false);
                package.Position = 0;
                await package.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                byte[] footer = new byte[FooterSize];
                Magic.CopyTo(footer);
                BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(8), package.Length);
                hash.CopyTo(footer, 16);
                await target.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
            }
            File.Move(scratch, output, overwrite: true);
        }
        finally
        {
            InstallFiles.TryDeleteFile(scratch);
            InstallFiles.TryDeleteFile(zip);
        }
    }

    internal bool HasAsset(InstallerAsset asset) => files.TryGetValue(AssetName(asset), out Entry? entry) &&
        entry.Bytes == asset.Bytes && entry.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);

    internal bool HasLauncher => files.ContainsKey(LauncherName);

    internal PreparedPackage PreparedPayload() => new(Version, Fingerprint, files.Values
        .Where(file => file.Name.StartsWith("Payload/", StringComparison.Ordinal)).Select(file => new OwnedFile(file.Name[8..], file.Bytes, file.Sha256)).ToArray());

    internal bool MatchesLauncher(string hash) => files.TryGetValue(LauncherName, out Entry? entry) &&
        entry.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase);

    internal Task ExtractLauncherAsync(string target, CancellationToken token) => ExtractOneAsync(files[LauncherName], target, token);

    internal Task ExtractAssetAsync(InstallerAsset asset, string target, CancellationToken token) =>
        ExtractOneAsync(files[AssetName(asset)], target, token);

    internal async Task PreparePayloadAsync(string destination, CancellationToken token)
    {
        InstallFiles.CheckNoLinks(destination);
        using FileStream input = File.OpenRead(Executable);
        using var archive = new ZipArchive(new PackageSection(input, offset, length), ZipArchiveMode.Read);
        foreach (Entry entry in files.Values.Where(file => file.Name.StartsWith("Payload/", StringComparison.Ordinal)))
        {
            string target = InstallFiles.Under(destination, entry.Name[8..]);
            InstallFiles.CheckNoLinks(target);
            if (File.Exists(target) && new FileInfo(target).Length == entry.Bytes &&
                entry.Sha256.Equals(await InstallFiles.HashAsync(target, token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                continue;
            await ExtractAsync(archive, entry, target, token).ConfigureAwait(false);
        }
    }

    private static async Task WriteArchiveAsync(string zip, string payload, InstallerPackageOptions options, CancellationToken token)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        var files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in InstallFiles.Tree(payload, token).Where(item => !item.Directory).OrderBy(item => item.Path, StringComparer.Ordinal))
            await AddFileAsync("Payload/" + Path.GetRelativePath(payload, item.Path).Replace('\\', '/'), item.Path).ConfigureAwait(false);
        if (options.InstalledLauncher is { } launcher)
            await AddFileAsync(LauncherName, launcher).ConfigureAwait(false);
        foreach (OfflinePackageAsset offline in options.OfflineAssets)
        {
            AssetStore.Validate(offline.Asset);
            Entry entry = await AddFileAsync(AssetName(offline.Asset), offline.File).ConfigureAwait(false);
            if (entry.Bytes != offline.Asset.Bytes || !entry.Sha256.Equals(offline.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The offline file did not match its pin: {offline.Asset.FileName}.");
        }
        await using Stream manifest = archive.CreateEntry(ManifestName).Open();
        await JsonSerializer.SerializeAsync(manifest, new Manifest(options.ProductId, options.Version, options.Offline, files.Values.ToArray()),
            cancellationToken: token).ConfigureAwait(false);

        async Task<Entry> AddFileAsync(string name, string file)
        {
            InstallFiles.CheckNoLinks(Path.GetFullPath(file));
            await using FileStream input = File.OpenRead(file);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            input.Position = 0;
            var entry = new Entry(name, input.Length, hash);
            files.Add(name, entry);
            await using Stream output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
            await input.CopyToAsync(output, token).ConfigureAwait(false);
            return entry;
        }
    }

    private async Task ExtractOneAsync(Entry entry, string target, CancellationToken token)
    {
        using FileStream input = File.OpenRead(Executable);
        using var archive = new ZipArchive(new PackageSection(input, offset, length), ZipArchiveMode.Read);
        await ExtractAsync(archive, entry, target, token).ConfigureAwait(false);
    }

    private static async Task ExtractAsync(ZipArchive archive, Entry entry, string target, CancellationToken token)
    {
        InstallFiles.CheckNoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string staged = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            ZipArchiveEntry source = archive.GetEntry(entry.Name) ?? throw new InvalidDataException("A setup file is missing.");
            await using (Stream input = source.Open())
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > entry.Bytes)
                        throw new InvalidDataException($"The setup file exceeds its declared size: {entry.Name}.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
            }
            if (new FileInfo(staged).Length != entry.Bytes || !entry.Sha256.Equals(await InstallFiles.HashAsync(staged, token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The setup file did not match its manifest: {entry.Name}.");
            File.Move(staged, target, overwrite: true);
        }
        finally { InstallFiles.TryDeleteFile(staged); }
    }

    private static void ValidateManifest(ZipArchive archive, Manifest manifest)
    {
        InstallFiles.CheckSegment(manifest.ProductId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Version);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        foreach (Entry file in manifest.Files)
        {
            if (!(file.Name == LauncherName || file.Name.StartsWith("Payload/", StringComparison.Ordinal) ||
                  (manifest.Offline && file.Name.StartsWith("Assets/", StringComparison.Ordinal))) ||
                file.Name.Contains('\\') || file.Name.Split('/').Any(segment => segment is "" or "." or "..") ||
                file.Bytes < 0 || !InstallFiles.IsHash(file.Sha256) || !entries.Add(file.Name))
                throw new InvalidDataException("The setup manifest contains an invalid file.");
            foreach (string part in file.Name.Split('/'))
                InstallFiles.CheckSegment(part);
            ZipArchiveEntry item = archive.GetEntry(file.Name) ?? throw new InvalidDataException("The setup manifest refers to a missing file.");
            if (item.Length != file.Bytes)
                throw new InvalidDataException("The setup archive and manifest disagree about file size.");
        }
        if (archive.Entries.Count != entries.Count || !entries.SetEquals(archive.Entries.Select(entry => entry.FullName)))
            throw new InvalidDataException("The setup archive contains duplicate or unlisted files.");
    }

    private static string AssetName(InstallerAsset asset) => $"Assets/{asset.Sha256.ToUpperInvariant()}/{asset.FileName}";

    private static bool ReadFooter(Stream input, long end, Span<byte> footer)
    {
        if (end < FooterSize)
            return false;
        input.Position = end - FooterSize;
        input.ReadExactly(footer);
        return footer[..8].SequenceEqual(Magic);
    }

    private sealed class PackageSection(Stream input, long start, long length) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            input.Position = start + position;
            int count = input.Read(buffer[..(int)Math.Min(length - position, buffer.Length)]);
            position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long basis = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentException(nameof(origin)) };
            long target = checked(basis + offset);
            if (target < 0 || target > length)
                throw new IOException("The setup archive read was outside its bounds.");
            return position = target;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

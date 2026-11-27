using System.IO.Compression;
using System.Security.Cryptography;

namespace GameInstaller;

internal static class InstallFiles
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    internal static void CheckSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment.EndsWith('.') || segment.EndsWith(' ') || DeviceNames.Contains(segment.Split('.')[0].TrimEnd()))
            throw new InvalidDataException($"Invalid path segment: {segment}");
    }

    internal static string Under(string root, string relative)
    {
        if (relative.Length == 0)
            return root;
        foreach (string segment in relative.Replace('\\', '/').Split('/'))
            CheckSegment(segment);
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(path, root))
            throw new InvalidDataException($"Path is outside the expected folder: {relative}");
        return path;
    }

    internal static bool IsInside(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        (Path.EndsInDirectorySeparator(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    internal static void RequireSeparate(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(first);
        second = Path.TrimEndingDirectorySeparator(second);
        if (first.Equals(second, StringComparison.OrdinalIgnoreCase) || IsInside(first, second) || IsInside(second, first))
            throw new InvalidDataException("The source and installer folders must be separate.");
    }

    internal static void CheckNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Linked paths are unsupported: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static IEnumerable<(string Path, bool Directory)> Tree(string root, CancellationToken token, bool preserveLinks = false)
    {
        CheckNoLinks(root);
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.TryPop(out string? folder))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (!preserveLinks)
                        throw new InvalidDataException($"Linked paths are unsupported: {entry}");
                    yield return (entry, false);
                    continue;
                }
                if (!preserveLinks)
                    CheckSegment(Path.GetFileName(entry));
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (directory)
                    folders.Push(entry);
                yield return (entry, directory);
            }
        }
    }

    internal static async Task CopyTreeAsync(string source, string destination, CancellationToken token)
    {
        RequireSeparate(source, destination);
        CheckNoLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (var item in Tree(source, token))
        {
            string target = Under(destination, Path.GetRelativePath(source, item.Path));
            if (item.Directory)
                Directory.CreateDirectory(target);
            else
                await CopyFileAsync(item.Path, target, token).ConfigureAwait(false);
        }
    }

    internal static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        CheckNoLinks(source);
        CheckNoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        await WriteAsync(input, destination, token).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Stream input, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { TryDeleteFile(temporary); }
    }

    internal static async Task ExtractAsync(string archive, string destination, CancellationToken token)
    {
        CheckNoLinks(archive);
        CheckNoLinks(destination);
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry item in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = item.FullName.Replace('\\', '/');
            bool directory = name.EndsWith('/');
            if (name.Length == 0 || name.StartsWith('/') || (item.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
                (item.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Unsupported ZIP entry: {name}");
            string target = Under(destination, directory ? name[..^1] : name);
            if (target == destination || (!directory && !files.Add(target)))
                throw new InvalidDataException($"Invalid or duplicate ZIP entry: {name}");
            CheckNoLinks(target);
            if (directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            await using Stream input = item.Open();
            await WriteAsync(input, target, token).ConfigureAwait(false);
        }
    }

    internal static async Task<string> HashAsync(string file, CancellationToken token)
    {
        CheckNoLinks(file);
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }

    internal static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static void TryDeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

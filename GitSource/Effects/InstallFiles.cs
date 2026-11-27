using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal delegate void InstallProgress(string action, long done, long total);

internal static class InstallFiles
{
    internal static string Size(long bytes)
    {
        return bytes >= 1073741824 ? (bytes / 1073741824.0).ToString("N2") + " GiB" :
            (bytes / 1048576.0).ToString("N1") + " MiB";
    }

    internal static long Free(string path)
    {
        return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;
    }

    internal static void EnsureSpace(string path, long bytes, string action)
    {
        if (Free(path) < bytes)
            throw new IOException("Free at least " + Size(bytes) + " on " + Path.GetPathRoot(path) + " to " + action + ".");
    }

    internal static IEnumerable<FileInfo> Tree(string root, CancellationToken token)
    {
        var directory = new DirectoryInfo(root);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            yield break;
        foreach (FileInfo file in directory.EnumerateFiles())
        {
            token.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                yield return file;
        }
        foreach (DirectoryInfo child in directory.EnumerateDirectories())
            foreach (FileInfo file in Tree(child.FullName, token))
                yield return file;
    }

    internal static void Transfer(Stream input, Stream output, long length, Action<long> report, CancellationToken token)
    {
        var buffer = new byte[256 * 1024];
        var update = Stopwatch.StartNew();
        long done = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            done += read;
            if (done > length)
                throw new InvalidDataException("More data was received than expected.");
            output.Write(buffer, 0, read);
            if (report != null && update.ElapsedMilliseconds >= 100)
            {
                report(done);
                update.Restart();
            }
        }
        if (done != length)
            throw new InvalidDataException("The operation finished with fewer bytes than expected.");
        if (report != null) report(done);
    }

    internal static void Copy(FileInfo file, string destination, Action<long> progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        if (File.Exists(destination))
        {
            var existing = new FileInfo(destination);
            if ((existing.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A linked file blocks installation: " + destination);
            if (existing.Length == file.Length && existing.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                if (progress != null) progress(file.Length);
                return;
            }
        }
        string partial = destination + ".install-part";
        try
        {
            using (var input = file.OpenRead())
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Transfer(input, output, file.Length, progress, token);
            File.SetLastWriteTimeUtc(partial, file.LastWriteTimeUtc);
            PublishFile(partial, destination);
        }
        finally { RemovePartial(partial); }
    }

    internal static void WriteText(string path, string value)
    {
        string partial = path + ".install-part";
        try
        {
            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
                writer.Write(value);
            PublishFile(partial, path);
        }
        finally { RemovePartial(partial); }
    }

    internal static void PublishFile(string source, string destination)
    {
        if (File.Exists(destination))
            File.Replace(source, destination, null);
        else
            File.Move(source, destination);
    }

    internal static void PublishFolder(string source, string destination)
    {
        string backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        bool replace = Directory.Exists(destination);
        if (replace) Directory.Move(destination, backup);
        try { Directory.Move(source, destination); }
        catch
        {
            if (replace) Directory.Move(backup, destination);
            throw;
        }
        if (replace)
            ClearRetiredFolder(backup);
    }

    internal static void RemovePartial(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void ClearOwnedFolder(string path)
    {
        if (!path.EndsWith(".installing", StringComparison.Ordinal))
            throw new ArgumentException("The staging folder must be installer-owned.");
        if (Directory.Exists(path))
            DeleteTree(new DirectoryInfo(path));
    }

    internal static void ClearRetiredFolder(string path)
    {
        const string suffix = ".previous-";
        string name = Path.GetFileName(path);
        int at = name.LastIndexOf(suffix, StringComparison.Ordinal);
        if (at < 0 || !Guid.TryParseExact(name.AsSpan(at + suffix.Length), "N", out _))
            throw new ArgumentException("The backup folder must be installer-owned.");
        try
        {
            if (Directory.Exists(path))
                DeleteTree(new DirectoryInfo(path));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A linked directory blocks cleanup: " + directory.FullName);
        foreach (var file in directory.EnumerateFiles())
            file.Delete();
        foreach (var child in directory.EnumerateDirectories())
            DeleteTree(child);
        directory.Delete();
    }
}

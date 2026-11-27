using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameInstaller;

internal static class SteamDiscovery
{
    private static readonly Regex Pair = new("\"(?<key>[^\"]+)\"\\s+\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant);

    internal static async Task<ResolvedGame> ResolveAsync(GameSource spec, string? manualDirectory, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(manualDirectory))
        {
            string manual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manualDirectory));
            token.ThrowIfCancellationRequested();
            if (spec.SteamAppId is not uint manualAppId)
                return await ValidateAsync(spec, manual, null, token).ConfigureAwait(false);
            return await ResolveManualSteamAsync(spec, manual, manualAppId, token).ConfigureAwait(false);
        }

        string? problem = null;
        foreach (var candidate in Candidates(spec))
        {
            token.ThrowIfCancellationRequested();
            try { return await ValidateAsync(spec, candidate.Directory, candidate.Build, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problem ??= error.Message;
            }
        }
        throw new InvalidDataException(problem ?? "The game was not found. Select its installation folder.");
    }

    private static async Task<ResolvedGame> ResolveManualSteamAsync(GameSource spec, string manual, uint appId, CancellationToken token)
    {
        string? parent = Path.GetDirectoryName(manual);
        string? apps = parent is not null && Named(parent, "common") ? Path.GetDirectoryName(parent) : null;
        if (apps is not null && Named(apps, "steamapps") && ReadManifest(apps, appId) is { } game && SameFolder(game.Directory, manual))
            return await ValidateAsync(spec, manual, game.Build, token).ConfigureAwait(false);

        string library = Named(manual, "steamapps") ? manual : Named(manual, "common") && parent is not null && Named(parent, "steamapps")
            ? parent : Path.Combine(manual, "steamapps");
        if (ReadManifest(library, appId) is { } installed)
            return await ValidateAsync(spec, installed.Directory, installed.Build, token).ConfigureAwait(false);

        return await ValidateAsync(spec, manual, null, token).ConfigureAwait(false);
    }

    private static bool Named(string path, string name) => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);

    private static bool SameFolder(string left, string right) => Path.TrimEndingDirectorySeparator(left).Equals(right, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Directory, string? Build)> Candidates(GameSource spec)
    {
        if (spec.SteamAppId is not uint appId)
            yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in SteamRoots(spec.SteamDirectory))
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                continue;
            foreach (string rawLibrary in Libraries(root))
            {
                if (!Path.IsPathFullyQualified(rawLibrary))
                    continue;
                string library = Path.GetFullPath(rawLibrary);
                if (seen.Add(library) && ReadManifest(Path.Combine(library, "steamapps"), appId) is { } match)
                    yield return match;
            }
        }
    }

    private static IEnumerable<string?> SteamRoots(string? configured)
    {
        yield return configured;
        yield return ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        yield return ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    }

    internal static string? FindClientDirectory(string? configured = null) => SteamRoots(configured)
        .FirstOrDefault(root => !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && File.Exists(Path.Combine(root, "steam.exe")));

    private static string? ReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static IEnumerable<string> Libraries(string steam)
    {
        yield return steam;
        string? text = ReadText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
        if (text is null)
            yield break;
        foreach (Match match in Pair.Matches(text))
        {
            string key = match.Groups["key"].Value;
            if (key.Equals("path", StringComparison.OrdinalIgnoreCase) || key.All(char.IsAsciiDigit))
                yield return Decode(match.Groups["value"].Value);
        }
    }

    private static (string Directory, string? Build)? ReadManifest(string apps, uint appId)
    {
        string? text = ReadText(Path.Combine(apps, $"appmanifest_{appId.ToString(CultureInfo.InvariantCulture)}.acf"));
        if (text is null)
            return null;
        string? directory = Value(text, "installdir");
        if (string.IsNullOrWhiteSpace(directory) || Value(text, "appid") != appId.ToString(CultureInfo.InvariantCulture))
            return null;
        try
        {
            InstallFiles.CheckSegment(directory);
            return (Path.Combine(apps, "common", directory), Value(text, "buildid"));
        }
        catch (InvalidDataException) { return null; }
    }

    private static string? Value(string text, string key) => Pair.Matches(text).Cast<Match>()
        .Where(match => match.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
        .Select(match => Decode(match.Groups["value"].Value)).FirstOrDefault();

    private static string Decode(string value) => value.Replace(@"\\", @"\").Replace("\\\"", "\"");

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<ResolvedGame> ValidateAsync(GameSource spec, string directory, string? build, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstallFiles.CheckNoLinks(directory);
        if (spec.SupportedBuildIds.Count > 0 && (build is null ? spec.FileHashes.Count == 0 : !spec.SupportedBuildIds.Contains(build)))
            throw new InvalidDataException(build is null ? "The supported build could not be confirmed for that folder." : $"Unsupported game build: {build}.");

        string executable = InstallFiles.Under(directory, spec.Executable);
        foreach (string name in spec.RequiredFiles.Prepend(spec.Executable))
        {
            string file = InstallFiles.Under(directory, name);
            InstallFiles.CheckNoLinks(file);
            if (!File.Exists(file))
                throw new FileNotFoundException($"Required game file is missing: {name}.", file);
        }
        foreach (var pair in spec.FileHashes)
        {
            string file = InstallFiles.Under(directory, pair.Key);
            if (!string.Equals(await InstallFiles.HashAsync(file, token).ConfigureAwait(false), pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The installed file does not match the supported build: {pair.Key}.");
        }
        return new ResolvedGame(directory, build, executable);
    }
}

using System.Net;

namespace GameInstaller;

internal sealed class AssetSkippedException(string message) : Exception(message) { }

internal sealed class AssetStore(string directory, string package, IInstallerInteraction? interaction, InstallerEvents events,
    InstallDownloadPolicy policy, IReadOnlyList<InstallReviewAsset>? approved, HttpClient? transport = null)
{
    internal const int MaxAttempts = 5;
    private readonly HashSet<string> selectedFolders = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxRedirects = 5;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static void Validate(InstallerAsset asset)
    {
        InstallFiles.CheckSegment(asset.FileName);
        if (asset.Bytes < 0 || !InstallFiles.IsHash(asset.Sha256))
            throw new InvalidDataException($"A size and SHA-256 are required for {asset.FileName}.");
        if (asset.Url is not null)
            CheckOrigin(asset.Url, asset);
    }

    internal static string CachedPath(string directory, InstallerAsset asset) =>
        InstallFiles.Under(InstallFiles.Under(directory, asset.Sha256.ToUpperInvariant()), asset.FileName);

    internal static string[] LocalCandidates(string package, InstallerAsset asset) => asset.LocalFiles.Prepend(asset.FileName)
        .Select(relative => InstallFiles.Under(package, relative)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal InstallerPackage? EmbeddedPackage { get; init; }
    internal string? FallbackDirectory { get; init; }

    internal Task<string> ResolveAsync(InstallerAsset asset, CancellationToken token, bool canSkip = false) =>
        ResolveCoreAsync(asset, token, canSkip, preferredApproved: false);

    internal Task<string> ResolvePreferredAsync(InstallerAsset asset, CancellationToken token, bool canSkip) =>
        ResolveCoreAsync(asset, token, canSkip, preferredApproved: true);

    private async Task<string> ResolveCoreAsync(InstallerAsset asset, CancellationToken token, bool canSkip, bool preferredApproved)
    {
        string cached = CachedPath(directory, asset);
        InstallFiles.CheckNoLinks(cached);
        if (File.Exists(cached))
        {
            events.Report($"Checking saved {asset.FileName}");
            if (await MatchesAsync(asset, cached, token).ConfigureAwait(false))
            {
                events.Log("Download", $"Verified from cache: {asset.FileName}");
                return cached;
            }
            events.Log("Download", $"Cached file did not match: {asset.FileName}", InstallerLogLevel.Warning);
        }

        string partial = cached + ".part";
        InstallFiles.CheckNoLinks(partial);
        if (EmbeddedPackage?.HasAsset(asset) == true)
        {
            events.Report($"Reading bundled {asset.FileName}");
            await EmbeddedPackage.ExtractAssetAsync(asset, cached, token).ConfigureAwait(false);
            events.Log("Download", $"Verified embedded asset: {asset.FileName}");
            return cached;
        }
        int attempted = 0;
        string? previousError = null;
        IEnumerable<string> adjacent = FallbackDirectory is { } loose ? LocalCandidates(loose, asset) : [];
        foreach (string candidate in LocalCandidates(package, asset).Concat(adjacent).Concat(selectedFolders.Select(folder => InstallFiles.Under(folder, asset.FileName)))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
                continue;
            try
            {
                events.Log("Download", $"Checking bundled candidate: {candidate}.");
                await ReadLocalAsync(candidate, partial, asset, token).ConfigureAwait(false);
                return await CommitAsync(asset, partial, cached, token).ConfigureAwait(false);
            }
            catch (Exception error) when (CanRetry(error))
            {
                previousError = InstallerLog.Clean(error.Message);
                events.Log("Download", $"Local candidate for {asset.FileName}: {previousError}", InstallerLogLevel.Warning);
            }
            finally { InstallFiles.TryDeleteFile(partial); }
        }

        bool automatic = policy != InstallDownloadPolicy.LocalOnly &&
            (preferredApproved ? IsApproved(asset, preferred: true) : policy == InstallDownloadPolicy.ApproveReviewed && IsApproved(asset));
        bool online = policy != InstallDownloadPolicy.LocalOnly && asset.Url is not null;
        if (policy == InstallDownloadPolicy.ApproveReviewed && !automatic)
            online = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            bool canDownload = online && attempted < MaxAttempts;
            bool approvedAttempt = automatic && canDownload;
            DownloadDecision choice;
            if (approvedAttempt)
                choice = new DownloadDecision(DownloadChoice.Download);
            else if (interaction is not null)
            {
                events.Report($"Waiting for your choice for {asset.FileName}");
                events.Log("Download", $"Waiting for a verified copy of {asset.FileName}.");
                var question = new DownloadQuestion(asset, attempted + 1, MaxAttempts, canDownload, previousError)
                {
                    CanSkip = canSkip,
                    CanOpenDownloadLink = online
                };
                choice = await interaction.ChooseDownloadAsync(question, token).ConfigureAwait(false);
            }
            else if (canSkip)
                throw new AssetSkippedException(previousError ?? $"No verified local copy of {asset.FileName} is available.");
            else
                throw new InvalidOperationException(previousError ?? $"{asset.FileName} requires a verified local copy, review approval, or a download interaction handler.");

            if (choice.Choice == DownloadChoice.Cancel)
                throw new OperationCanceledException($"Setup stopped before acquiring {asset.FileName}.", token);
            if (choice.Choice == DownloadChoice.Skip && canSkip)
                throw new AssetSkippedException($"No verified copy of {asset.FileName} was selected.");

            try
            {
                token.ThrowIfCancellationRequested();
                if (choice.Choice == DownloadChoice.Download && canDownload)
                {
                    attempted++;
                    events.Log("Download", $"Approved attempt {attempted} of {MaxAttempts} for {asset.FileName}{(approvedAttempt ? " (install review)" : "")}.");
                    await DownloadAsync(asset, partial, token).ConfigureAwait(false);
                }
                else if (choice.Choice is DownloadChoice.UseLocalFile or DownloadChoice.UseLocalFolder && !string.IsNullOrWhiteSpace(choice.File))
                {
                    string selected = Path.GetFullPath(choice.File);
                    if (choice.Choice == DownloadChoice.UseLocalFolder)
                    {
                        InstallFiles.CheckNoLinks(selected);
                        selectedFolders.Add(selected);
                        selected = InstallFiles.Under(selected, asset.FileName);
                        if (!File.Exists(selected))
                            throw new FileNotFoundException($"That folder does not contain {asset.FileName}.");
                    }
                    events.Log("Download", $"Checking a manually selected file for {asset.FileName}.");
                    await ReadLocalAsync(selected, partial, asset, token).ConfigureAwait(false);
                    string verified = await CommitAsync(asset, partial, cached, token).ConfigureAwait(false);
                    selectedFolders.Add(Path.GetDirectoryName(selected)!);
                    return verified;
                }
                else
                {
                    throw new InvalidOperationException($"The download handler returned an unavailable choice for {asset.FileName}.");
                }

                return await CommitAsync(asset, partial, cached, token).ConfigureAwait(false);
            }
            catch (Exception error) when (CanRetry(error))
            {
                previousError = InstallerLog.Clean(error.Message);
                events.Log("Download", $"{asset.FileName}: {previousError}", InstallerLogLevel.Warning);
                if (approvedAttempt && interaction is IInstallerSetupInteraction)
                    automatic = false;
            }
            finally { InstallFiles.TryDeleteFile(partial); }
        }
    }

    private bool IsApproved(InstallerAsset asset, bool preferred = false) => approved?.Any(item => item.RequiresSeparateApproval == preferred &&
        item.Id == asset.Id && item.FileName == asset.FileName &&
        item.Bytes == asset.Bytes && item.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase) &&
        item.Url?.AbsoluteUri == asset.Url?.AbsoluteUri && item.Hosts.SequenceEqual(Hosts(asset), StringComparer.OrdinalIgnoreCase)) == true;

    internal static bool HasVerifiedCandidate(InstallerAsset asset, IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            try
            {
                InstallFiles.CheckNoLinks(candidate);
                using FileStream input = File.OpenRead(candidate);
                if (input.Length == asset.Bytes && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input))
                    .Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception error) when (CanRetry(error)) { }
        }
        return false;
    }

    internal static string[] Hosts(InstallerAsset asset) => asset.Url is null ? [] :
        asset.RedirectHosts.Prepend(asset.Url.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool CanRetry(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or TimeoutException;

    private async Task ReadLocalAsync(string source, string partial, InstallerAsset asset, CancellationToken token)
    {
        InstallFiles.CheckNoLinks(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        if (input.Length != asset.Bytes)
            throw new InvalidDataException("The selected file has a different size.");
        await CopyLimitedAsync(input, partial, asset, remote: false, token).ConfigureAwait(false);
    }

    private async Task<string> CommitAsync(InstallerAsset asset, string partial, string cached, CancellationToken token)
    {
        events.Report($"Verifying {asset.FileName}");
        if (!await MatchesAsync(asset, partial, token).ConfigureAwait(false))
            throw new InvalidDataException("The file does not match the expected size and SHA-256.");
        token.ThrowIfCancellationRequested();
        File.Move(partial, cached, overwrite: true);
        events.Log("Download", $"Verified {asset.FileName} ({asset.Bytes:N0} bytes).");
        return cached;
    }

    private async Task DownloadAsync(InstallerAsset asset, string target, CancellationToken token)
    {
        Uri address = asset.Url!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                CheckOrigin(address, asset);
                events.Log("Download", $"Connecting to {address.IdnHost} for {asset.FileName}.");
                timeout.CancelAfter(IdleTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                request.Headers.UserAgent.ParseAdd("GameInstaller/1.0");
                using HttpResponseMessage response = await (transport ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop == MaxRedirects || response.Headers.Location is not { } redirect || !Uri.TryCreate(address, redirect, out Uri? next))
                        throw new InvalidDataException("The download sent an invalid redirect or redirected too many times.");
                    address = next;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long advertised && advertised != asset.Bytes)
                    throw new InvalidDataException("The server reported a different file size.");
                await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                await CopyLimitedAsync(input, target, asset, remote: true, token, timeout).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The download did not respond for 45 seconds.");
        }
    }

    private async Task CopyLimitedAsync(Stream input, string target, InstallerAsset asset, bool remote, CancellationToken token,
        CancellationTokenSource? timeout = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        InstallFiles.TryDeleteFile(target);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        byte[] buffer = new byte[131072];
        long total = 0;
        string label = remote ? $"Downloading {asset.FileName}" : $"Reading local {asset.FileName}";
        events.Report(label, 0, asset.Bytes);
        long previous = 0;
        while (true)
        {
            timeout?.CancelAfter(IdleTimeout);
            int read = await input.ReadAsync(buffer.AsMemory(), timeout?.Token ?? token).ConfigureAwait(false);
            timeout?.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0)
                break;
            if (read > asset.Bytes - total)
                throw new InvalidDataException("The file exceeds the expected size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            total += read;
            if (total - previous >= 1024 * 1024 || total == asset.Bytes)
            {
                events.Report(label, total, asset.Bytes);
                previous = total;
            }
        }
    }

    private static async Task<bool> MatchesAsync(InstallerAsset asset, string file, CancellationToken token) =>
        new FileInfo(file).Length == asset.Bytes && string.Equals(await InstallFiles.HashAsync(file, token).ConfigureAwait(false),
            asset.Sha256, StringComparison.OrdinalIgnoreCase);

    private static void CheckOrigin(Uri address, InstallerAsset asset)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort || address.UserInfo.Length != 0 ||
            address.Fragment.Length != 0 || address.IdnHost.Length == 0 ||
            (!address.IdnHost.Equals(asset.Url!.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                !asset.RedirectHosts.Contains(address.IdnHost, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("The download or redirect did not use an approved HTTPS origin. Use the manual option.");
    }
}

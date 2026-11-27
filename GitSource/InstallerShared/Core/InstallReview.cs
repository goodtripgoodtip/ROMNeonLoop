namespace GameInstaller;

public sealed record InstallReviewAction(string Name, string Description);

public sealed record InstallReviewAsset(string Id, string FileName, long Bytes, string Sha256, Uri? Url, IReadOnlyList<string> Hosts,
    IReadOnlyList<string> LocalFiles, bool HasCachedCandidate)
{
    public bool IsEmbedded { get; init; }
    public bool RequiresSeparateApproval { get; init; }
}

public sealed class InstallReview
{
    internal InstallReview(InstallerDefinition definition, string packageDirectory, InstallRequest request, string productDirectory,
        ResolvedGame? source, string? sourceStamp, InstallReviewAction[] actions, InstallReviewAsset[] assets, long? freeBytes)
    {
        Definition = definition;
        PackageDirectory = packageDirectory;
        Request = request;
        ProductDirectory = productDirectory;
        Source = source;
        SourceStamp = sourceStamp;
        Actions = Array.AsReadOnly(actions);
        Assets = Array.AsReadOnly(assets);
        FreeBytes = freeBytes;
    }

    internal InstallerDefinition Definition { get; }
    internal string PackageDirectory { get; }
    internal string? SourceStamp { get; }
    internal InstallRequest Request { get; }

    public string ProductDirectory { get; }
    public ResolvedGame? Source { get; }
    public IReadOnlyList<InstallReviewAction> Actions { get; }
    public IReadOnlyList<InstallReviewAsset> Assets { get; }
    public long? FreeBytes { get; }
    public InstallDownloadPolicy DownloadPolicy => Request.DownloadPolicy;
    public string? NetworkDisclosure => Definition.NetworkDisclosure;
    public InstallerNotice? Notice => Definition.Notice;
    public IReadOnlyList<string> Components => Request.Components ?? [];
}

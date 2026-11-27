namespace GameInstaller;

public sealed record InstallerNotice(string Version, string Title, string Text)
{
    public static InstallerNotice HobbyRelease { get; } = ReadHobbyTerms();

    private static InstallerNotice ReadHobbyTerms()
    {
        using Stream input = typeof(InstallerNotice).Assembly.GetManifestResourceStream("GameInstaller.HobbyTerms.txt")
            ?? throw new InvalidOperationException("The hobby release terms resource is missing.");
        using var reader = new StreamReader(input);
        return new InstallerNotice("hobby-1", "Hobby Release Terms", reader.ReadToEnd());
    }
}

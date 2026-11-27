using System.Text;
using System.Text.RegularExpressions;

namespace GameInstaller;

public enum InstallerLogLevel { Info, Warning, Error }

public sealed record InstallerLogEntry(DateTimeOffset Timestamp, InstallerLogLevel Level, string Area, string Message);

public interface IInstallerLogger
{
    void Write(InstallerLogEntry entry);
}

public sealed class InstallerLog : IInstallerLogger, IDisposable
{
    private static readonly Regex UrlQuery = new(@"(https?://[^\s?#]+)[?#][^\s]+", RegexOptions.IgnoreCase);
    private readonly StreamWriter writer;
    private readonly IProgress<InstallerLogEntry>? observer;
    private readonly IInstallerLogger? additionalSink;

    public InstallerLog(string directory, IProgress<InstallerLogEntry>? observer = null, IInstallerLogger? additionalSink = null)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        this.observer = observer;
        this.additionalSink = additionalSink;
    }

    public string Path { get; }

    public void Write(InstallerLogEntry entry)
    {
        var safeEntry = entry with { Area = Clean(entry.Area), Message = Clean(entry.Message) };
        lock (writer)
            writer.WriteLine($"{safeEntry.Timestamp:O} [{safeEntry.Level}] [{safeEntry.Area}] {safeEntry.Message}");
        try { observer?.Report(safeEntry); }
        catch (Exception) { }
        try { additionalSink?.Write(safeEntry); }
        catch (Exception) { }
    }

    internal static string Clean(string value) => UrlQuery.Replace(value.Replace('\r', ' ').Replace('\n', ' '), "$1?[redacted]");

    public void Dispose()
    {
        lock (writer)
            writer.Dispose();
    }
}

internal sealed class InstallerEvents(IInstallerLogger? logger, IProgress<InstallerProgress>? progress)
{
    private string? stage;
    private string? nextStage;
    private int stagesCompleted;
    private int stageCount;

    internal void Log(string area, string message, InstallerLogLevel level = InstallerLogLevel.Info)
    {
        try { logger?.Write(new InstallerLogEntry(DateTimeOffset.Now, level, InstallerLog.Clean(area), InstallerLog.Clean(message))); }
        catch (Exception) { }
    }

    internal void Stage(string label, string? next, int done, int count)
    {
        stage = label;
        nextStage = next;
        stagesCompleted = done;
        stageCount = count;
        Log("Setup", next is null ? label : $"{label}. Next: {next}.");
        Report(label);
    }

    internal void Report(string message, long? completed = null, long? total = null) =>
        progress?.Report(new InstallerProgress(message, completed, total)
        {
            Stage = stage,
            NextStage = nextStage,
            StagesCompleted = stagesCompleted,
            StageCount = stageCount
        });
}

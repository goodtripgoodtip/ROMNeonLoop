using System;
using System.Diagnostics;
using System.IO;

internal static class ScoreControls
{
    internal const int Pitch = 1;
    internal const int Effects = 2;
    private static readonly string Path = Environment.GetEnvironmentVariable("ROM_MUSIC_SELECTION");
    private static readonly string[] Installed = (Environment.GetEnvironmentVariable("ROM_MUSIC_INSTALLED") ?? "").Split(',');
    private static long nextRead;
    private static string library = "";
    private static volatile int flags = Pitch | Effects;

    internal static string Library { get { return library; } }
    internal static int Flags { get { return library.Length == 0 ? 0 : flags; } }

    internal static void Refresh(bool force)
    {
        if (string.IsNullOrEmpty(Path))
            return;
        long now = Stopwatch.GetTimestamp();
        if (!force && now < nextRead)
            return;
        nextRead = now + Stopwatch.Frequency / 5;
        try
        {
            if (!File.Exists(Path))
            {
                library = "";
                return;
            }
            using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                string selection = (reader.ReadLine() ?? "").Trim();
                library = Array.IndexOf(Installed, selection) >= 0 ? selection : "";
                int selected = Pitch | Effects;
                for (int i = 0; i < 2; ++i)
                {
                    string setting = (reader.ReadLine() ?? "").Trim();
                    if (setting == "pitch=0")
                        selected &= ~Pitch;
                    else if (setting == "effects=0")
                        selected &= ~Effects;
                }
                flags = selected;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

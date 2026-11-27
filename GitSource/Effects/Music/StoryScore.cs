using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public static class StoryScore
{
    private const string Prefix = StoryCueMatcher.Prefix;
    private static readonly string Pack = Environment.GetEnvironmentVariable("ROM_REPLACEMENT_SCORE");
    private static readonly Lazy<Dictionary<string, Beat>> Beats = new Lazy<Dictionary<string, Beat>>(ReadBeats);
    private static readonly Dictionary<string, bool> Moves = new Dictionary<string, bool>();
    private static readonly Dictionary<string, string> StockNames = new Dictionary<string, string>();
    private static string storyPath;

    private sealed class Beat
    {
        internal string Scene;
        internal StoryCueMatcher.Cue Cue;
        internal float Pitch;
        internal float Space;
        internal float Seconds;
    }

    public static string Transform(string text, string path)
    {
        if (string.IsNullOrEmpty(Pack) || text == null || path == null)
            return text;
        if (storyPath != path)
        {
            storyPath = path;
            ScoreDirector.Scene(path);
        }
        Moves.Clear();
        ScoreControls.Refresh(true);
        if (ScoreControls.Library.Length == 0)
            return text;
        var cues = new List<StoryCueMatcher.Cue>();
        foreach (Beat beat in Beats.Value.Values)
            if (string.Equals(path, "dialogs/en/" + beat.Scene, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "dialogs/jp/" + beat.Scene, StringComparison.OrdinalIgnoreCase))
                cues.Add(beat.Cue);
        return cues.Count == 0 ? text : StoryCueMatcher.Rewrite(text, cues, StockNames);
    }

    public static bool Route(ref string name, out bool temporaryEntrance)
    {
        temporaryEntrance = false;
        if (name == null || !name.StartsWith(Prefix, StringComparison.Ordinal))
            return true;

        int split = name.IndexOf('_', Prefix.Length);
        Beat beat;
        if (split < 0 || !Beats.Value.TryGetValue(name.Substring(split + 1), out beat))
            return false;

        string id = name.Substring(split + 1);
        string action = name.Substring(Prefix.Length, split - Prefix.Length);
        ScoreControls.Refresh(true);
        int flags = ScoreControls.Flags;
        bool effects = (flags & ScoreControls.Effects) != 0;
        if (action == "beat")
        {
            if (flags != 0)
                ScoreDirector.Pulse(beat.Cue.Track, beat.Pitch, beat.Space, beat.Seconds, false);
            return false;
        }

        if (action == "enter")
        {
            temporaryEntrance = effects;
            return effects && ApplyBeat(ref name, beat);
        }

        if (action == "move")
        {
            if (beat.Cue.Action == "move-earlier")
            {
                Moves[id] = effects;
                return effects && ApplyBeat(ref name, beat);
            }

            bool shifted;
            bool play = Moves.TryGetValue(id, out shifted) && shifted;
            Moves.Remove(id);
            return play && ApplyBeat(ref name, beat);
        }

        if (action == "original")
        {
            if (beat.Cue.Action != "move-earlier")
            {
                Moves[id] = effects;
                if (effects)
                    return false;
            }
            else
            {
                bool shifted;
                bool skip = Moves.TryGetValue(id, out shifted) && shifted;
                Moves.Remove(id);
                if (skip)
                    return false;
            }
            name = ClipName(beat.Cue);
            return true;
        }
        return false;
    }

    private static bool ApplyBeat(ref string name, Beat beat)
    {
        name = ClipName(beat.Cue);
        ScoreDirector.Pulse(beat.Cue.Track, beat.Pitch, beat.Space, beat.Seconds, true);
        return true;
    }

    private static string ClipName(StoryCueMatcher.Cue cue)
    {
        string name;
        return StockNames.TryGetValue(cue.Id, out name) ? name : cue.Track;
    }

    private static Dictionary<string, Beat> ReadBeats()
    {
        var beats = new Dictionary<string, Beat>(StringComparer.OrdinalIgnoreCase);
        string[] rows = File.ReadAllLines(Path.Combine(Pack, "StoryBeats.tsv"));
        for (int i = 1; i < rows.Length; ++i)
        {
            string[] item = rows[i].Split('\t');
            beats.Add(item[0], new Beat
            {
                Scene = item[1],
                Cue = new StoryCueMatcher.Cue
                {
                    Id = item[0], Anchor = item[2], Occurrence = int.Parse(item[3], CultureInfo.InvariantCulture),
                    Action = item[4], Track = item[5], Volume = item[9],
                    AnchorMatches = int.Parse(item[11], CultureInfo.InvariantCulture)
                },
                Pitch = float.Parse(item[6], CultureInfo.InvariantCulture),
                Space = float.Parse(item[7], CultureInfo.InvariantCulture),
                Seconds = float.Parse(item[8], CultureInfo.InvariantCulture)
            });
        }
        return beats;
    }
}

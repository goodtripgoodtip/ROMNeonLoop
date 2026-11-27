using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

internal sealed class CueDynamics
{
    internal readonly string Id;
    internal readonly string Family;
    internal readonly float Range;
    internal readonly float Motion;
    internal readonly float Period;
    internal readonly float Wet;
    internal readonly float Room;
    internal readonly float Echo;
    internal readonly float Cutoff;
    internal readonly float Width;

    private CueDynamics(string[] row)
    {
        Id = row[0];
        Family = row[2];
        Range = Number(row[3]);
        Motion = Number(row[4]);
        Period = Number(row[5]);
        Wet = Number(row[6]);
        Room = Number(row[7]);
        Echo = Number(row[8]);
        Cutoff = Number(row[9]);
        Width = Number(row[10]);
    }

    internal float PitchAt(int frame, float take, float accent)
    {
        double position = frame / (44100.0 * Period);
        double phase = (position - Math.Floor(position)) * (2 * Math.PI);
        double motion = Math.Sin(phase) * Motion * Math.Sign(take);
        return Math.Max(-65f, Math.Min(65f, take * Range + (float)motion + accent));
    }

    internal static Dictionary<string, CueDynamics> Read(string file)
    {
        var cues = new Dictionary<string, CueDynamics>(StringComparer.OrdinalIgnoreCase);
        foreach (string row in File.ReadAllLines(file))
        {
            if (row.StartsWith("cue\t", StringComparison.Ordinal) || row.Length == 0)
                continue;
            var cue = new CueDynamics(row.Split('\t'));
            cues.Add(cue.Id, cue);
        }
        return cues;
    }

    private static float Number(string value)
    {
        return float.Parse(value, CultureInfo.InvariantCulture);
    }
}

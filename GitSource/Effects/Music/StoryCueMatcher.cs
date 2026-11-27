using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

internal static class StoryCueMatcher
{
    internal const string Prefix = "_romscore_";
    private static readonly Regex Tag = new Regex(@"<[^<>\r\n]+>");
    private static readonly Regex Node = new Regex(@"^::[ \t]+.*$", RegexOptions.Multiline);
    private static readonly Regex Music = new Regex(@"^<playMusic\b", RegexOptions.IgnoreCase);
    private static readonly Regex Boundary = new Regex(@"<(?:stopMusic|break|loadDialog|loadScene)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Name = new Regex(@"(\bname\s*=\s*)(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);

    internal sealed class Cue
    {
        internal string Id;
        internal string Anchor;
        internal int Occurrence;
        internal int AnchorMatches;
        internal string Action;
        internal string Track;
        internal string Volume;
    }

    private sealed class Edit
    {
        internal int Offset;
        internal int Length;
        internal string Markup;
    }

    internal static string Rewrite(string text, List<Cue> cues, Dictionary<string, string> stockNames)
    {
        if (text.IndexOf(Prefix, StringComparison.Ordinal) >= 0)
            return text;
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var tags = Tag.Matches(normalized);
        var nodes = Node.Matches(normalized);
        var edits = new List<Edit>();
        var names = new Dictionary<string, string>();
        foreach (Cue cue in cues)
            if (!AddEdits(normalized, tags, nodes, cue, edits, names))
                return text;

        edits.Sort((left, right) => right.Offset.CompareTo(left.Offset));
        int limit = normalized.Length + 1;
        foreach (Edit edit in edits)
        {
            if (edit.Offset >= limit || edit.Offset + edit.Length > limit)
                return text;
            limit = edit.Offset;
        }

        var result = new StringBuilder(normalized);
        foreach (Edit edit in edits)
            result.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Markup);
        foreach (var name in names)
            stockNames[name.Key] = name.Value;
        return result.ToString();
    }

    private static bool AddEdits(string text, MatchCollection tags, MatchCollection nodes, Cue cue,
        List<Edit> edits, Dictionary<string, string> names)
    {
        Match anchor = null;
        int matches = 0;
        foreach (Match tag in tags)
            if (tag.Value.IndexOf(cue.Anchor, StringComparison.Ordinal) >= 0 && ++matches == cue.Occurrence)
                anchor = tag;
        if (anchor == null || matches != cue.AnchorMatches)
            return false;

        if (cue.Action == "pulse" || cue.Action == "enter")
        {
            string suffix = cue.Action == "enter" ? " volume=" + cue.Volume + " crossFade>" : ">";
            string action = cue.Action == "enter" ? "enter" : "beat";
            edits.Add(new Edit { Offset = anchor.Index, Markup = "<playMusic name=\"" + Key(cue, action) + "\"" + suffix });
            return true;
        }

        bool earlier = cue.Action == "move-earlier";
        if (!earlier && cue.Action != "move-later")
            return false;
        Match source = FindMoveSource(text.Length, tags, nodes, anchor, cue.Track, earlier);
        if (source == null)
            return false;
        int destination = earlier ? anchor.Index : anchor.Index + anchor.Length;
        int start = Math.Min(destination, source.Index);
        int end = Math.Max(destination, source.Index);
        if (Boundary.IsMatch(text.Substring(start, end - start)))
            return false;
        names.Add(cue.Id, TrackName(Name.Match(source.Value)));
        edits.Add(new Edit { Offset = destination, Markup = Rename(source.Value, Key(cue, "move")) });
        edits.Add(new Edit { Offset = source.Index, Length = source.Length, Markup = Rename(source.Value, Key(cue, "original")) });
        return true;
    }

    private static Match FindMoveSource(int end, MatchCollection tags, MatchCollection nodes, Match anchor, string cue, bool earlier)
    {
        int start = 0;
        foreach (Match node in nodes)
        {
            if (node.Index > anchor.Index)
            {
                end = node.Index;
                break;
            }
            start = node.Index;
        }
        Match nearest = null;
        foreach (Match tag in tags)
        {
            if (tag.Index < start || tag.Index >= end || (tag.Index > anchor.Index + anchor.Length) != earlier ||
                !Music.IsMatch(tag.Value) || !string.Equals(TrackName(Name.Match(tag.Value)), cue, StringComparison.OrdinalIgnoreCase))
                continue;
            if (nearest == null || Math.Abs(tag.Index - anchor.Index) < Math.Abs(nearest.Index - anchor.Index))
                nearest = tag;
        }
        return nearest;
    }

    private static string TrackName(Match name)
    {
        if (!name.Success)
            return null;
        for (int i = 2; i <= 4; ++i)
            if (name.Groups[i].Success)
                return name.Groups[i].Value;
        return null;
    }

    private static string Key(Cue cue, string action)
    {
        return Prefix + action + "_" + cue.Id;
    }

    private static string Rename(string tag, string key)
    {
        return Name.Replace(tag, name => name.Groups[1].Value + "\"" + key + "\"", 1);
    }
}

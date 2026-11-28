using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static class GameplayProgress
{
    internal enum ActionStatus { Untried, Tried, Covered }

    private static readonly HashSet<string> Records = new HashSet<string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> Offered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    private static readonly byte[] Separator = { 0 };
    private static readonly Regex Formatting = new Regex(
        @"</?(?:b|i|u|color(?:=#[0-9a-f]{6,8})?)>|<(?:profileBox|setColor|playVO)(?:[ =,][^<>$\[\]{}]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Menus = new Regex(@"<menu>[^<]*</menu>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly char[] ScriptCharacters = { '<', '>', '$', '[', ']', '{', '}' };
    private static string conversation;
    private static bool dialogueStarted;
    private static readonly ConditionalWeakTable<object, Tuple<bool>> TaleChecks = new ConditionalWeakTable<object, Tuple<bool>>();

    private static bool Enabled
    {
        get { return GameplayHooks.Enabled(GameplayHooks.Option.Examined); }
    }

    public static string BeginAction(object source, string action)
    {
        try
        {
            var tag = source as Component;
            if (!Enabled || tag == null || !GameAccess.Playing(GameAccess.Scene))
                return null;
            string key = ActionKey(tag, action);
            if (key != null)
            {
                Records.Add("tried:" + key);
                conversation = key;
                dialogueStarted = false;
            }
            return key;
        }
        catch (Exception error) { GameplayHud.Report(error); return null; }
    }

    public static void EndAction(string key, string action)
    {
        if (key != null && action != "talk" && !dialogueStarted)
        {
            Records.Add("done:" + key);
            if (conversation == key)
                conversation = null;
        }
    }

    internal static void DialogueStarted(object manager)
    {
        if (conversation == null)
            return;
        dialogueStarted = true;
        object tale = GameAccess.Get<object>(manager, "_currentTale");
        if (tale != null && !TaleChecks.GetValue(tale, value => Tuple.Create(KnownTale(value))).Item1)
            MarkUncertain();
    }

    private static bool KnownTale(object tale)
    {
        var passages = GameAccess.Get<IDictionary>(tale, "PassageDict");
        if (passages == null)
            return false;
        foreach (object passage in passages.Values)
        {
            var nodes = GameAccess.Get<IList>(passage, "_nodeChain");
            if (nodes == null)
                return false;
            foreach (object node in nodes)
                if (node == null || node.GetType().Name != "TextScriptNode")
                    return false;
        }
        return true;
    }

    public static void DialogueEnded(object manager)
    {
        try
        {
            if (!dialogueStarted || conversation == null || GameAccess.Get<bool>(manager, "_showingDialog"))
                return;
            Records.Add("done:" + conversation);
            conversation = null;
            dialogueStarted = false;
        }
        catch (Exception error) { GameplayHud.Report(error); }
    }

    private static string ActionKey(Component tag, string action)
    {
        if (string.IsNullOrEmpty(action) || GameAccess.Get<object>(tag, "overrideTrigger") != null)
            return null;
        var actions = GameAccess.Get<IDictionary>(tag, "ActionList");
        string effective = GameAccess.Get<bool>(tag, "forceTag") ?
            GameAccess.Get<string>(tag, "forceTagString") : action;
        var script = actions == null || string.IsNullOrEmpty(effective) ? null : actions[effective] as string[];
        if (script == null || script.Length == 0)
            return null;
        object memory = GameAccess.Type("Memory");
        string chapter = Convert.ToString(GameAccess.Call(memory, "Get", "chapter", ""));
        string location = Convert.ToString(GameAccess.Call(memory, "Get", "currentSet", ""));
        return Digest(location, GameplayExplore.Identity(tag), chapter, action, effective, Digest(script));
    }

    internal static ActionStatus Status(Component tag, string action)
    {
        try
        {
            if (action != "item")
                return Status(ActionKey(tag, action));
            var scripts = GameAccess.Get<IDictionary>(tag, "ActionList");
            if (scripts == null)
                return ActionStatus.Untried;
            bool found = false;
            bool tried = false;
            bool covered = true;
            foreach (string key in scripts.Keys)
                if (GameplayExplore.ItemActionAvailable(key))
                {
                    found = true;
                    ActionStatus status = Status(ActionKey(tag, key));
                    tried |= status != ActionStatus.Untried;
                    covered &= status == ActionStatus.Covered;
                }
            return found && covered ? ActionStatus.Covered : tried ? ActionStatus.Tried : ActionStatus.Untried;
        }
        catch (Exception error) { GameplayHud.Report(error); return ActionStatus.Untried; }
    }

    private static ActionStatus Status(string key)
    {
        if (ActionComplete(key))
            return ActionStatus.Covered;
        return key != null && (Records.Contains("tried:" + key) || Records.Contains("done:" + key))
            ? ActionStatus.Tried : ActionStatus.Untried;
    }

    private static bool ActionComplete(string key)
    {
        if (key == null || !Records.Contains("done:" + key) || Records.Contains("unknown:" + key))
            return false;
        HashSet<string> options;
        if (Offered.TryGetValue(key, out options))
            foreach (string option in options)
                if (!Records.Contains("picked:" + key + ":" + option))
                    return false;
        return true;
    }

    private static void AddRecord(string record)
    {
        if (!Records.Add(record) || !record.StartsWith("offered:", StringComparison.Ordinal))
            return;
        string[] fields = record.Split(':');
        if (fields.Length != 3)
            return;
        HashSet<string> options;
        if (!Offered.TryGetValue(fields[1], out options))
            Offered[fields[1]] = options = new HashSet<string>(StringComparer.Ordinal);
        options.Add(fields[2]);
    }

    internal static bool[] ObserveChoices(object manager, IList items)
    {
        if (!Enabled || items == null)
            return null;
        var options = GameAccess.Get<IList<string>>(manager, "_menuOptions");
        var marks = new bool[items.Count];
        if (options == null || options.Count != items.Count)
        {
            MarkUncertain();
            return marks;
        }
        for (int i = 0; i < options.Count; ++i)
        {
            if (GameAccess.Get<bool>(items[i], "label") || GameAccess.Get<bool>(items[i], "noClick"))
                continue;
            string option = options[i];
            string display = GameAccess.Get<string>(items[i], "plaintext");
            if (!ChoiceMatches(display, option))
            {
                MarkUncertain();
                continue;
            }
            string sameResult;
            string key = ChoiceKey(manager, option, out sameResult);
            if (conversation != null)
            {
                if (key == null)
                    MarkUncertain();
                else
                    AddRecord("offered:" + conversation + ":" + key);
            }
            marks[i] = sameResult != null && Records.Contains("repeat:" + sameResult);
        }
        return marks;
    }

    private static bool ChoiceMatches(string display, string option)
    {
        if (string.IsNullOrEmpty(option))
            return false;
        int pipe = option.IndexOf('|');
        string label = pipe < 0 ? option : option.Substring(0, pipe);
        return GameplayHooks.Plain(display) == GameplayHooks.Plain(label);
    }

    public static string BeginChoice(object manager, string option)
    {
        if (!Enabled)
            return null;
        try
        {
            string sameResult;
            string key = ChoiceKey(manager, option, out sameResult);
            if (key == null)
            {
                MarkUncertain();
                return null;
            }
            return (conversation ?? "") + "|" + key + "|" + (sameResult ?? "");
        }
        catch (Exception error) { GameplayHud.Report(error); return null; }
    }

    public static void EndChoice(string key)
    {
        if (key == null)
            return;
        string[] fields = key.Split('|');
        if (fields[0].Length > 0)
            Records.Add("picked:" + fields[0] + ":" + fields[1]);
        if (fields[2].Length > 0)
            Records.Add("repeat:" + fields[2]);
    }

    private static void MarkUncertain()
    {
        if (conversation != null)
            Records.Add("unknown:" + conversation);
    }

    private static string ChoiceKey(object manager, string option, out string sameResult)
    {
        sameResult = null;
        string story = GameAccess.Get<string>(manager, "_currDialog");
        string source = GameAccess.Get<string>(manager, "_currNode");
        object tale = GameAccess.Get<object>(manager, "_currentTale");
        if (string.IsNullOrEmpty(story) || string.IsNullOrEmpty(source) || string.IsNullOrEmpty(option) || tale == null)
            return null;
        string[] parts = option.Split('|');
        if (parts.Length > 2 || string.IsNullOrWhiteSpace(parts[parts.Length - 1]))
            return null;
        string target = parts[parts.Length - 1];
        var passages = GameAccess.Get<IDictionary>(tale, "PassageDict");
        if (passages == null || !passages.Contains(target))
            return null;
        var nodes = GameAccess.Get<IList>(passages[target], "_nodeChain");
        if (nodes == null || nodes.Count == 0)
            return null;
        var transcript = new List<string>(nodes.Count);
        bool plain = true;
        foreach (object node in nodes)
        {
            if (node == null || node.GetType().Name != "TextScriptNode")
                return null;
            string text = GameAccess.Get<string>(node, "NodeTextWithComments");
            if (string.IsNullOrWhiteSpace(text))
                return null;
            string stripped = Formatting.Replace(text, "");
            string withoutMenu = Menus.Replace(stripped, "");
            if (withoutMenu.IndexOfAny(ScriptCharacters) >= 0)
                return null;
            plain &= stripped == withoutMenu;
            transcript.Add(text);
        }
        var options = GameAccess.Get<IList<string>>(manager, "_menuOptions");
        if (options == null || options.Count == 0)
            return null;
        var available = new string[options.Count];
        options.CopyTo(available, 0);
        string choice = Digest(story, source, option, target, Digest(available), Digest(transcript.ToArray()));
        if (plain)
            sameResult = choice;
        return choice;
    }

    public static void Reset()
    {
        Records.Clear();
        Offered.Clear();
        conversation = null;
        dialogueStarted = false;
    }

    public static void Load(string save, bool configOnly)
    {
        if (configOnly)
            return;
        Reset();
        try
        {
            string path = SnapshotPath(save);
            if (path == null || !File.Exists(path))
                return;
            using (var reader = File.OpenText(path))
            {
                if (reader.ReadLine() != "NeonLoop actions 1")
                    return;
                string line;
                while ((line = reader.ReadLine()) != null && Records.Count < 100000)
                    if (line.Length < 150)
                        AddRecord(line);
            }
        }
        catch (Exception error) { Reset(); GameplayHud.Report(error); }
    }

    public static void Save(string metadata)
    {
        if (Records.Count == 0 || string.IsNullOrEmpty(metadata) ||
            !metadata.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            return;
        string temporary = null;
        try
        {
            string path = SnapshotPath(metadata.Substring(0, metadata.Length - 5));
            if (path == null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            using (var writer = File.CreateText(temporary))
            {
                writer.WriteLine("NeonLoop actions 1");
                foreach (string record in Records)
                    writer.WriteLine(record);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        catch (Exception error) { GameplayHud.Report(error); }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static string SnapshotPath(string save)
    {
        string data = Environment.GetEnvironmentVariable("ROM_GAMEPLAY_DATA");
        string metadata = save + ".meta";
        if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(save) || !File.Exists(save) || !File.Exists(metadata))
            return null;
        return Path.Combine(data, "ActionHistory",
            Digest(Path.GetFileName(save).ToUpperInvariant(), FileDigest(save), FileDigest(metadata)) + ".txt");
    }

    private static string FileDigest(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static string Digest(params string[] parts)
    {
        using (var sha = SHA256.Create())
        {
            foreach (string part in parts)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(part ?? "");
                byte[] length = BitConverter.GetBytes(bytes.Length);
                sha.TransformBlock(length, 0, length.Length, length, 0);
                if (bytes.Length != 0)
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            }
            sha.TransformFinalBlock(Separator, 0, Separator.Length);
            return BitConverter.ToString(sha.Hash).Replace("-", "");
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static class GameplayHooks
{
    internal enum Option
    {
        SeenSkip, InstantText, History, TaxiRetry = 4, Hotspots, Backups, TunnelExits,
        Pointer, Objective, SingleClick, LargeChoices, ContinuePreview, GlyphLabels,
        Verbs, Visited, SaveFeedback, Examined, Monitor, Inventory, Startup, OriginalPixelFont = 22
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FlagReader();
    private static FlagReader readFlags;
    private static uint flags = InitialFlags();
    private static float retryFlagsAt;

    private static uint InitialFlags()
    {
        try { return GameplayDefaults.Read(Environment.GetEnvironmentVariable("ROM_GAMEPLAY_SETTINGS")); }
        catch (IOException) { return GameplayDefaults.Enabled; }
        catch (UnauthorizedAccessException) { return GameplayDefaults.Enabled; }
    }
    private static GameplayHud hud;
    private static object dialogue;
    private static object writer;
    private static string lineKey;
    private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
    private static readonly Queue<string> History = new Queue<string>();
    private static readonly Regex Tags = new Regex("<[^>]*>", RegexOptions.Compiled);
    private static readonly string Data = Environment.GetEnvironmentVariable("ROM_GAMEPLAY_DATA");
    private static readonly string HistoryPath = Environment.GetEnvironmentVariable("ROM_GAMEPLAY_HISTORY");
    private static bool seenLoaded;
    private static bool skipNeedsRelease;
    private static Action retryTaxi;
    private static object retryWriter;
    private static bool retryKey;
    private static bool hotspotKey;
    internal static bool ShowHotspots { get; private set; }
    internal static object Dialog { get { return dialogue; } }
    internal static bool TaxiReady
    {
        get
        {
            return Enabled(Option.TaxiRetry) && retryTaxi != null && GameAccess.Playing(GameAccess.Scene) &&
                Equals(GameAccess.Get<Action>(retryWriter, "onFinishCallback"), retryTaxi) &&
                !GameAccess.Get<bool>(retryWriter, "typing");
        }
    }

    internal static bool Enabled(Option option)
    {
        return (flags & (1u << (int)option)) != 0;
    }

    internal static bool HasHud
    {
        get
        {
            const uint visible = (1u << 4) | (1u << 5) | (1u << 7) | (1u << 9) |
                (1u << 11) | (1u << 12) | (1u << 13) | (1u << 14) | (1u << 15) |
                (1u << 16) | (1u << 17);
            return (flags & visible) != 0;
        }
    }

    public static void Tick()
    {
        try
        {
            OverlayInput.NotifyStartupPrompt();
            RefreshFlags();
            GameplayFont.Tick(!Enabled(Option.OriginalPixelFont));
            if ((flags & 0x1fffff) == 0 && hud == null)
                return;
            if (HasHud && hud == null)
            {
                var root = new GameObject("NeonLoop Gameplay");
                UnityEngine.Object.DontDestroyOnLoad(root);
                hud = root.AddComponent<GameplayHud>();
            }
            ReadShortcuts();
            GameplaySaves.Tick();
            GameplayExplore.Tick();
        }
        catch (Exception ex) { GameplayHud.Report(ex); }
    }

    private static void RefreshFlags()
    {
        if (readFlags == null && Time.unscaledTime >= retryFlagsAt)
        {
            retryFlagsAt = Time.unscaledTime + 1;
            IntPtr module = GetModuleHandle("ROMEffects.addon64");
            if (module != IntPtr.Zero)
            {
                IntPtr export = GetProcAddress(module, "ROMGameplayFlags");
                if (export != IntPtr.Zero)
                    readFlags = (FlagReader)Marshal.GetDelegateForFunctionPointer(export, typeof(FlagReader));
            }
        }
        if (readFlags != null)
            flags = readFlags();
    }

    private static void ReadShortcuts()
    {
        bool focused = Application.isFocused && !OverlayInput.IsMenuOpen();
        int hotspotSignal = OverlayInput.ConsumeHotspotToggle();
        bool f8 = focused && hotspotSignal < 0 && (GetAsyncKeyState(0x77) & 0x8000) != 0;
        if (focused && (hotspotSignal > 0 || f8 && !hotspotKey) &&
            (Enabled(Option.Hotspots) || Enabled(Option.Examined)))
            ShowHotspots = !ShowHotspots;
        if (!Enabled(Option.Hotspots) && !Enabled(Option.Examined))
            ShowHotspots = false;
        hotspotKey = f8;
        bool f7 = focused && (GetAsyncKeyState(0x76) & 0x8000) != 0;
        if (f7 && !retryKey && TaxiReady)
            Retry();
        retryKey = f7;
    }

    public static void BeginLine(object manager, string raw)
    {
        if (!Enabled(Option.SeenSkip) && !Enabled(Option.History) &&
            !Enabled(Option.LargeChoices) && !Enabled(Option.Examined))
            return;
        try
        {
            dialogue = manager;
            writer = GameAccess.Get<object>(manager, "dialogTypewriter");
            GameplayProgress.DialogueStarted(manager);
            string resource = GameAccess.Get<string>(manager, "_currDialog");
            object pointer = GameAccess.Get<object>(manager, "_currentTalePointer");
            object passage = GameAccess.Call(pointer, "GetCurrentPassage");
            string passageKey = GameAccess.Get<string>(passage, "Name") ??
                GameAccess.Get<string>(passage, "name") ?? GameAccess.Get<string>(manager, "_currNode");
            lineKey = !Enabled(Option.SeenSkip) || string.IsNullOrWhiteSpace(raw) ||
                string.IsNullOrEmpty(resource) ? null : Hash(resource + "\n" + passageKey + "\n" + raw);
        }
        catch (Exception ex) { lineKey = null; GameplayHud.Report(ex); }
    }

    public static bool AllowSkip(bool pressed, object currentWriter)
    {
        if (!Enabled(Option.SeenSkip))
        {
            skipNeedsRelease = false;
            return pressed;
        }
        if (!ReferenceEquals(writer, currentWriter))
            return false;
        if (!pressed)
        {
            skipNeedsRelease = false;
            return false;
        }
        if (skipNeedsRelease)
            return false;
        try
        {
            LoadSeen();
            bool safe = lineKey != null && Seen.Contains(lineKey) && !GameAccess.Get<bool>(dialogue, "_hasMenu") &&
                !GameAccess.Get<bool>(currentWriter, "NoSkip");
            skipNeedsRelease = !safe;
            return safe;
        }
        catch (Exception ex) { GameplayHud.Report(ex); return false; }
    }

    public static void CompleteLine(object manager)
    {
        if (!Enabled(Option.SeenSkip) && !Enabled(Option.History))
            return;
        try
        {
            if (!ReferenceEquals(dialogue, manager))
                return;
            string completed = DialogueText(writer);
            if (string.IsNullOrWhiteSpace(completed))
                return;
            if (Enabled(Option.SeenSkip) && lineKey != null)
            {
                LoadSeen();
                if (Seen.Add(lineKey) && !string.IsNullOrEmpty(Data))
                {
                    Directory.CreateDirectory(Data);
                    File.AppendAllText(Path.Combine(Data, "ReadDialogue.txt"), lineKey + "\n");
                }
            }
            if (Enabled(Option.History))
                RecordLine(completed);
        }
        catch (Exception ex) { GameplayHud.Report(ex); }
    }

    internal static string DialogueText(object source)
    {
        var text = DialogueRenderer(source);
        return GameAccess.IsActive(text) ? Plain(GameAccess.Get<string>(text, "Text")) : "";
    }

    internal static Behaviour DialogueRenderer(object source)
    {
        return GameAccess.Get<Behaviour>(source, "portraitTextObject");
    }

    internal static string Plain(string value)
    {
        return value == null ? "" : Tags.Replace(value, "").Trim();
    }

    private static void LoadSeen()
    {
        if (seenLoaded)
            return;
        seenLoaded = true;
        string path = string.IsNullOrEmpty(Data) ? null : Path.Combine(Data, "ReadDialogue.txt");
        if (path != null && File.Exists(path))
            foreach (string key in File.ReadLines(path))
                if (key.Length == 64)
                    Seen.Add(key);
    }

    internal static string Hash(string value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
    }

    private static void RecordLine(string text)
    {
        if (string.IsNullOrEmpty(HistoryPath))
            return;
        var profile = GameAccess.Get<object>(GameAccess.Scene, "profileBox");
        string target = GameAccess.Get<bool>(profile, "ProfileVisible") ? GameAccess.Get<string>(profile, "target") : null;
        var portraits = target == null ? null : GameAccess.Call(GameAccess.Scene, "GetObject", target) as IList;
        var portrait = portraits != null && portraits.Count > 0 ? portraits[0] as GameObject : null;
        string speaker = portrait == null ? null :
            GameAccess.Get<string>(portrait.GetComponent("SetTag"), "descriptiveName");
        History.Enqueue((string.IsNullOrEmpty(speaker) ? "" : speaker + ": ") +
            text.Replace('\r', ' ').Replace('\n', ' '));
        while (History.Count > 240)
            History.Dequeue();
        string temporary = HistoryPath + ".pending";
        File.WriteAllLines(temporary, History.ToArray(), new UTF8Encoding(false));
        if (File.Exists(HistoryPath))
            File.Replace(temporary, HistoryPath, null);
        else
            File.Move(temporary, HistoryPath);
    }

    public static float CharacterTime(float original, object routine)
    {
        if (!Enabled(Option.InstantText) || original <= 0)
            return original;
        try
        {
            object call = GameAccess.Get<object>(routine, "call");
            object typewriter = GameAccess.Get<object>(routine, "<>4__this");
            return GameAccess.Get<bool>(call, "Portrait") && !GameAccess.Get<bool>(call, "Rollover") &&
                !GameAccess.Get<bool>(call, "NoSkip") && !GameAccess.Get<bool>(typewriter, "NoSkip") &&
                GameAccess.Playing(GameAccess.Scene) ? 0 : original;
        }
        catch (Exception ex) { GameplayHud.Report(ex); return original; }
    }

    public static void TaxiFailed()
    {
        if (!Enabled(Option.TaxiRetry))
            return;
        try
        {
            var manager = GameAccess.Get<object>(GameAccess.Type("DialogManager"), "_instance");
            retryWriter = GameAccess.Get<object>(manager, "dialogTypewriter");
            var callback = GameAccess.Get<Action>(retryWriter, "onFinishCallback");
            retryTaxi = callback != null && callback.Method.DeclaringType.DeclaringType == GameAccess.Type("ChaseWidget")
                ? callback : null;
        }
        catch (Exception ex) { GameplayHud.Report(ex); }
    }

    internal static void Retry()
    {
        if (!TaxiReady)
            return;
        Action action = retryTaxi;
        retryTaxi = null;
        GameAccess.Set(retryWriter, "onFinishCallback", null);
        GameAccess.Call(retryWriter, "ClearText", false);
        action();
    }

    public static void KeepPointer(object pointer)
    {
        if (!Enabled(Option.Pointer))
            return;
        try
        {
            if (GameAccess.Playing(GameAccess.Scene) && !GameAccess.Get<bool>(pointer, "hidden"))
                GameAccess.Set(pointer, "timeStill", 0f);
        }
        catch (Exception ex) { GameplayHud.Report(ex); }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}

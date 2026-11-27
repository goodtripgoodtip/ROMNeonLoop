using System;
using System.IO;
using System.Linq;
using UnityEngine;

public static class GameplaySaves
{
    private static string pending;
    private static float timeout;
    private static float noticeUntil;
    private static string notice;
    private static float nextPreview;
    internal static string Preview { get; private set; }
    internal static string Notice { get { return Time.unscaledTime < noticeUntil ? notice : null; } }

    public static void StartSlot(string filename)
    {
        if (!GameplayHooks.Enabled(GameplayHooks.Option.SaveFeedback))
            return;
        pending = filename;
        timeout = Time.unscaledTime + 8;
    }

    public static void CopySlot(string source, string destination)
    {
        try { File.Copy(source, destination); }
        catch
        {
            if (GameplayHooks.Enabled(GameplayHooks.Option.SaveFeedback))
                SetNotice("Save failed");
            pending = null;
            throw;
        }
    }

    public static void FinishSlot(string metadata)
    {
        if (!GameplayHooks.Enabled(GameplayHooks.Option.Backups) &&
            !GameplayHooks.Enabled(GameplayHooks.Option.SaveFeedback))
            return;
        try
        {
            if (!metadata.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return;
            string saved = metadata.Substring(0, metadata.Length - ".meta".Length);
            bool complete = File.Exists(saved) && File.Exists(metadata);
            pending = null;
            if (GameplayHooks.Enabled(GameplayHooks.Option.SaveFeedback))
                SetNotice(complete ? "Saved" : "Save failed");
            if (complete && GameplayHooks.Enabled(GameplayHooks.Option.Backups))
                Backup(saved, metadata);
        }
        catch (Exception ex)
        {
            if (GameplayHooks.Enabled(GameplayHooks.Option.SaveFeedback))
                SetNotice("Saved. Recovery copy could not be created.");
            GameplayHud.Report(ex);
        }
    }

    private static void Backup(string saved, string metadata)
    {
        string data = Environment.GetEnvironmentVariable("ROM_GAMEPLAY_DATA");
        if (string.IsNullOrEmpty(data))
            return;
        string folder = Path.Combine(data, "Recovery", Path.GetFileName(saved));
        Directory.CreateDirectory(folder);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N");
        string temporary = Path.Combine(folder, id + ".pending");
        Directory.CreateDirectory(temporary);
        try
        {
            File.Copy(saved, Path.Combine(temporary, Path.GetFileName(saved)));
            File.Copy(metadata, Path.Combine(temporary, Path.GetFileName(metadata)));
            Directory.Move(temporary, Path.Combine(folder, id));
        }
        finally
        {
            if (Directory.Exists(temporary))
                DeleteCopy(temporary, saved, metadata);
        }
        foreach (string path in Directory.GetDirectories(folder).Where(IsRecoveryCopy)
            .OrderByDescending(path => path, StringComparer.Ordinal).Skip(3))
            DeleteCopy(path, saved, metadata);
    }

    private static bool IsRecoveryCopy(string path)
    {
        string name = Path.GetFileName(path);
        Guid id;
        return name.Length == 56 && Guid.TryParseExact(name.Substring(24), "N", out id);
    }

    private static void DeleteCopy(string folder, string saved, string metadata)
    {
        File.Delete(Path.Combine(folder, Path.GetFileName(saved)));
        File.Delete(Path.Combine(folder, Path.GetFileName(metadata)));
        if (Directory.GetFileSystemEntries(folder).Length == 0)
            Directory.Delete(folder);
    }

    private static void SetNotice(string message)
    {
        notice = message;
        noticeUntil = Time.unscaledTime + 3.5f;
    }

    internal static void Tick()
    {
        if (pending != null && Time.unscaledTime >= timeout)
        {
            pending = null;
            SetNotice("Save did not finish");
        }
        if (!GameplayHooks.Enabled(GameplayHooks.Option.ContinuePreview) ||
            !GameAccess.Get<bool>(GameAccess.Scene, "inMainMenu") ||
            GameAccess.Get<bool>(GameAccess.Scene, "inOptions"))
        {
            Preview = null;
            nextPreview = 0;
            return;
        }
        if (Time.unscaledTime < nextPreview)
            return;
        nextPreview = Time.unscaledTime + 1;
        var memory = GameAccess.Type("Memory");
        var files = GameAccess.Call(memory, "SaveFiles") as string[];
        if (files == null || files.Length == 0)
        {
            Preview = null;
            return;
        }
        Preview = null;
        var display = GameAccess.Type("SaveDisplayInfo");
        for (int i = 0; i < files.Length; ++i)
            if (!GameAccess.Alive(GameAccess.Call(display, "GetDisplayInfoForSlot", i)))
                return;
        object index = GameAccess.Call(memory, "GetMostRecentSaveSlot");
        var save = GameAccess.Call(display, "GetDisplayInfoForSlot", index);
        if (!GameAccess.Alive(save))
            return;
        string location = GameAccess.Get<string>(save, "DescriptiveName");
        if (string.IsNullOrWhiteSpace(location))
            location = GameAccess.Get<string>(save, "Set");
        string chapter = GameAccess.Get<string>(save, "Chapter");
        DateTime date = GameAccess.Get<DateTime>(save, "Date");
        Preview = "Continue  ·  " + chapter + "  ·  " + location +
            (date == default(DateTime) ? "" : "  ·  " + date.ToString("g"));
    }
}

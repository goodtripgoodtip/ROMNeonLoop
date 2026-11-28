using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

internal sealed class MusicPack
{
    internal static readonly string[] Libraries = { "dark-synthwave", "cinematic-sci-fi", "noir-jazz" };
    private readonly string[] installed;
    private readonly string version;

    internal string Root { get; private set; }
    internal bool HasSoundtrack { get { return installed.Length != 0; } }
    internal string InstalledKeys { get { return string.Join(",", installed); } }

    internal MusicPack(string effects, SteamGame game, string[] verifiedSeal = null, string[] verifiedCues = null)
    {
        Root = Path.Combine(effects, "bin", "MusicPack");
        string data = Path.Combine(game.Installed, "ROM_Data");
        string[] ready = verifiedSeal ?? File.ReadAllLines(Path.Combine(Root, "Ready.txt"));
        if (ready.Length != 3 || ready[0] != game.BuildId ||
            verifiedSeal == null && (ready[1] != Digest(Path.Combine(data, "Managed", "Assembly-CSharp.dll")) ||
            ready[2] != Digest(Path.Combine(data, "globalgamemanagers"))))
            throw new InvalidDataException("This release supports a different Steam build of 2064: Read Only Memories.");
        version = ready[0] + "-" + ready[1].Substring(0, 12);

        var keys = new List<string>();
        string[] cues = verifiedCues ?? File.ReadAllLines(Path.Combine(Root, "Cues.tsv"));
        foreach (string library in Libraries)
            if (HasCues(library, cues))
                keys.Add(library);
        installed = keys.ToArray();
    }

    internal static string Name(string library)
    {
        switch (library)
        {
            case "dark-synthwave": return "Dark Synthwave";
            case "cinematic-sci-fi": return "Cinematic Sci-Fi";
            case "noir-jazz": return "Noir Jazz";
            default: return "Original music";
        }
    }

    internal bool HasLibrary(string library)
    {
        return Array.IndexOf(installed, library) >= 0;
    }

    internal static bool HasCuesIn(string folder, string[] cues)
    {
        if (cues.Length < 2)
            return false;
        for (int i = 1; i < cues.Length; ++i)
        {
            string cue = cues[i].Split('\t')[0];
            var file = new FileInfo(Path.Combine(folder, "Cues", cue + ".mp3"));
            if (!file.Exists || file.Length == 0)
                return false;
        }
        return true;
    }

    private bool HasCues(string library, string[] cues)
    {
        return HasCuesIn(Path.Combine(Root, library), cues);
    }

    internal void Stage(string stockGame, string gameFolder)
    {
        string stock = Path.Combine(stockGame, "ROM_Data", "Managed", "Assembly-CSharp.dll");
        string managed = Path.Combine(gameFolder, "ROM_Data", "Managed");
        string target = Path.Combine(managed, "Assembly-CSharp.dll");
        string helper = Path.Combine(Root, "ReplacementScore.dll");
        string marker = Path.Combine(managed, ".score-patch");
        string signature = PatchSignature();

        StageFile(helper, Path.Combine(managed, "ReplacementScore.dll"));
        StageFile(Path.Combine(Root, "RomScoreDSP.dll"), Path.Combine(gameFolder, "RomScoreDSP.dll"));
        if (File.Exists(target) && File.Exists(marker) && File.ReadAllText(marker) == signature)
            return;

        string temporary = target + ".score-tmp";
        try
        {
            PatchScore.Patch(stock, helper, temporary);
            if (File.Exists(target))
                File.Replace(temporary, target, null);
            else
                File.Move(temporary, target);
            File.WriteAllText(marker, signature);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal bool IsStaged(string gameFolder)
    {
        string managed = Path.Combine(gameFolder, "ROM_Data", "Managed");
        string marker = Path.Combine(managed, ".score-patch");
        return File.Exists(marker) && File.Exists(Path.Combine(gameFolder, "ROMEffects.addon64")) &&
            File.Exists(Path.Combine(gameFolder, "dxgi.dll")) && File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")) &&
            File.Exists(Path.Combine(managed, "ReplacementScore.dll")) && File.ReadAllText(marker) == PatchSignature();
    }

    private string PatchSignature() => version + "-" + Digest(Path.Combine(Root, "ReplacementScore.dll")) +
        "-" + typeof(PatchScore).Module.ModuleVersionId;

    private static void StageFile(string source, string target)
    {
        var original = new FileInfo(source);
        var staged = new FileInfo(target);
        if (!staged.Exists || staged.Length != original.Length || staged.LastWriteTimeUtc != original.LastWriteTimeUtc)
            File.Copy(source, target, true);
    }

    private static string Digest(string file)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(file))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }

}

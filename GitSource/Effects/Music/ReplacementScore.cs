using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static class ReplacementScore
{
    private const string Prefix = "Audio/Music/";
    private const int Channels = 2;
    private static readonly Dictionary<string, AudioClip> Clips =
        new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
    private static readonly string PackDirectory = Environment.GetEnvironmentVariable("ROM_REPLACEMENT_SCORE");
    private static readonly string CutsceneDirectory = Environment.GetEnvironmentVariable("ROM_CUTSCENES");
    private static readonly Lazy<Dictionary<string, int>> ClipFrames = new Lazy<Dictionary<string, int>>(ReadFrames);
    private static readonly Lazy<Dictionary<string, CueDynamics>> Dynamics =
        new Lazy<Dictionary<string, CueDynamics>>(() => CueDynamics.Read(Path.Combine(PackDirectory, "CueDynamics.tsv")));

    public static AudioClip Resolve(string resource)
    {
        if (string.IsNullOrEmpty(PackDirectory) || resource == null || !resource.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string clipName = resource.Substring(Prefix.Length);
        string cue = clipName.ToLowerInvariant();
        if (cue.Length == 0 || cue != Path.GetFileName(cue))
            return null;
        ScoreControls.Refresh(true);
        string library = ScoreControls.Library;
        if (library.Length == 0)
            return null;
        string path = Path.Combine(PackDirectory, library, "Cues", cue + ".mp3");
        if (!File.Exists(path))
            return null;
        string key = library + "/" + cue;
        AudioClip clip;
        if (Clips.TryGetValue(key, out clip) && clip != null)
            return clip;

        try
        {

            var pcm = new PcmScoreVoice(path, Dynamics.Value[cue], ClipFrames.Value[cue]);
            try
            {
                clip = AudioClip.Create(clipName, pcm.Frames, Channels, PcmScoreVoice.Rate, true, pcm.Read, pcm.Seek);
                if (clip == null)
                    throw new InvalidOperationException("Unity did not create the clip.");
                ScoreDirector.Register(clip, pcm);
            }
            catch
            {
                pcm.Dispose();
                throw;
            }
            Debug.Log("[ROM Score] Loaded " + key);
        }
        catch (Exception error)
        {
            Debug.LogError("[ROM Score] " + cue + ": " + error.Message);
            return null;
        }

        Clips[key] = clip;
        return clip;
    }

    public static string ResolveVideo(string original)
    {
        if (string.IsNullOrEmpty(PackDirectory) || string.IsNullOrEmpty(CutsceneDirectory) || string.IsNullOrEmpty(original))
            return original;
        ScoreControls.Refresh(true);
        string key = ScoreControls.Library;
        if (key.Length == 0)
            return original;
        string name = Path.GetFileName(original);
        string library = Path.Combine(CutsceneDirectory, key);
        if (!File.Exists(Path.Combine(library, ".ready")))
            return original;
        string replacement = Path.Combine(library, "Videos", name);
        string variant = ScoreDirector.VideoVariant(name, ScoreControls.Flags);
        if (variant.Length != 0)
        {
            string dynamicMix = Path.Combine(library, "DynamicVideos", variant, name);
            if (File.Exists(dynamicMix))
                replacement = dynamicMix;
        }
        if (File.Exists(replacement))
        {
            Debug.Log("[ROM Score] Video mix " + replacement);
            return replacement;
        }
        return original;
    }

    public static void PlaySource(AudioSource source)
    {
        ScoreDirector.Play(source);
    }

    public static void Tick()
    {
        ScoreDirector.Tick();
    }

    private static Dictionary<string, int> ReadFrames()
    {
        var frames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] rows = File.ReadAllLines(Path.Combine(PackDirectory, "CueFrames.tsv"));
        for (int i = 1; i < rows.Length; ++i)
        {
            string[] cue = rows[i].Split('\t');
            frames.Add(cue[0], int.Parse(cue[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        return frames;
    }
}

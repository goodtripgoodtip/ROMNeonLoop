using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

internal static class ScoreDirector
{
    private sealed class Playing
    {
        internal AudioSource Source;
        internal PcmScoreVoice Voice;
    }

    private static readonly Dictionary<AudioClip, PcmScoreVoice> Voices = new Dictionary<AudioClip, PcmScoreVoice>();
    private static readonly List<Playing> Active = new List<Playing>();
    private static readonly Dictionary<string, int> LastTake = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> VideoHigh = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private static readonly float[] Takes = { -1f, -.62f, .62f, 1f };
    private static readonly System.Random Random = new System.Random();
    private static Playing current;
    private static string pendingCue;
    private static float pendingPitch;
    private static float pendingSpace;
    private static float pendingSeconds;
    private static long pendingUntil;
    private static float sceneSpace;
    private static bool audioError;

    internal static void Register(AudioClip clip, PcmScoreVoice voice)
    {
        voice.Clip = clip;
        Voices.Add(clip, voice);
    }

    internal static void Play(AudioSource source)
    {
        PcmScoreVoice voice;
        if (source.clip != null && Voices.TryGetValue(source.clip, out voice))
        {
            try
            {
                Prune();
                float take = NextTake(voice);
                voice.Start(source.timeSamples, source.loop, take, sceneSpace);
                if (string.Equals(pendingCue, voice.Profile.Id, StringComparison.OrdinalIgnoreCase)
                    && Stopwatch.GetTimestamp() <= pendingUntil)
                {
                    voice.Beat(pendingPitch, pendingSpace, pendingSeconds);
                    pendingCue = null;
                }
                var playing = Active.Find(item => item.Source == source);
                if (playing == null)
                {
                    playing = new Playing { Source = source, Voice = voice };
                    Active.Add(playing);
                }
                current = playing;
            }
            catch (Exception error)
            {
                if (!audioError)
                {
                    audioError = true;
                    UnityEngine.Debug.LogError("[ROM Score] Adaptive audio: " + error.Message);
                }
            }
        }
        source.Play();
    }

    internal static void Tick()
    {
        ScoreControls.Refresh(false);
        Prune();
    }

    internal static void Scene(string path)
    {
        string scene = path.ToLowerInvariant();
        if (scene.Contains("parallax") || scene.Contains("chasemap"))
            sceneSpace = .8f;
        else if (scene.Contains("stardust") || scene.Contains("chopshop") || scene.Contains("hassy"))
            sceneSpace = .65f;
        else if (scene.Contains("hospital") || scene.Contains("augmentedeye") || scene.Contains("police"))
            sceneSpace = .35f;
        else
            sceneSpace = 0f;
        foreach (Playing item in Active)
            item.Voice.SetScene(sceneSpace);
    }

    internal static void Pulse(string cue, float pitch, float space, float seconds, bool waitForStart)
    {
        if (!waitForStart && current != null && current.Source != null && current.Source.isPlaying &&
            current.Source.clip == current.Voice.Clip &&
            (string.IsNullOrEmpty(cue) || string.Equals(cue, current.Voice.Profile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            string family = current.Voice.Profile.Family;
            foreach (Playing item in Active)
                if (item == current || (!string.IsNullOrEmpty(family) && item.Voice.Profile.Family == family))
                    item.Voice.Beat(pitch, space, seconds);
            return;
        }
        if (!string.IsNullOrEmpty(cue))
        {
            pendingCue = cue;
            pendingPitch = pitch;
            pendingSpace = space;
            pendingSeconds = seconds;
            pendingUntil = Stopwatch.GetTimestamp() + 4 * Stopwatch.Frequency;
        }
    }

    internal static string VideoVariant(string name, int flags)
    {
        if ((flags & ScoreControls.Pitch) == 0)
            return (flags & ScoreControls.Effects) != 0 ? "effects" : "";
        bool high;
        if (VideoHigh.TryGetValue(name, out high))
            high = !high;
        else
            high = Random.Next(2) != 0;
        VideoHigh[name] = high;
        return ((flags & ScoreControls.Effects) != 0 ? "both-" : "pitch-") + (high ? "high" : "low");
    }

    private static float NextTake(PcmScoreVoice voice)
    {
        string family = voice.Profile.Family;
        if (current != null && current.Voice != voice && !string.IsNullOrEmpty(family) &&
            current.Voice.Profile.Family == family && current.Source != null && current.Source.isPlaying)
        {
            return current.Voice.Take;
        }

        string key = string.IsNullOrEmpty(family) ? voice.Profile.Id : family;
        int index, previous;
        if (LastTake.TryGetValue(key, out previous))
        {
            index = Random.Next(Takes.Length - 1);
            if (index >= previous)
                ++index;
        }
        else
        {
            index = Random.Next(Takes.Length);
        }
        LastTake[key] = index;
        return Takes[index];
    }

    private static void Prune()
    {
        for (int i = Active.Count - 1; i >= 0; --i)
        {
            Playing item = Active[i];
            if (item.Source != null && item.Source.clip == item.Voice.Clip)
                continue;
            Active.RemoveAt(i);
            if (!Active.Exists(other => other.Voice == item.Voice))
                item.Voice.Release();
            if (current == item)
                current = Active.Count == 0 ? null : Active[Active.Count - 1];
        }
    }
}

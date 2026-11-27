using System;
using System.IO;
using UnityEngine;

internal sealed class PcmScoreVoice : IDisposable
{
    internal const int Rate = 44100;
    private const float FadeStep = 1f / (Rate * .09f);
    private readonly object sync = new object();
    private readonly string path;
    private IntPtr reader;
    private IntPtr lookahead;
    private NativeScore native;
    private float[] pitchIn;
    private float[] pitchOut;
    private int cursor;
    private int readAhead;
    private int pitchOffset;
    private int discard;
    private bool primed;
    private bool looping;
    private bool failed;
    private float pitchMix;
    private float take;
    private long readFrames;
    private float accentCents;
    private float accentSpace;
    private int accentLength;
    private long accentStart;
    private float sceneSpace;

    internal int Frames { get; private set; }
    internal CueDynamics Profile { get; private set; }
    internal AudioClip Clip { get; set; }
    internal float Take { get { return take; } }

    internal PcmScoreVoice(string path, CueDynamics profile, int expectedFrames)
    {
        Profile = profile;
        this.path = path;
        int frames;
        reader = Open(out frames);
        if (frames < expectedFrames || frames - expectedFrames > 64)
        {
            NativeScore.RomMp3Close(reader);
            throw new InvalidDataException("Unexpected score MP3 length: " + path);
        }
        Frames = expectedFrames;
    }

    private IntPtr Open(out int frames)
    {
        IntPtr handle = NativeScore.RomMp3Open(path, out frames);
        if (handle == IntPtr.Zero)
            throw new IOException("Unable to open score MP3: " + path);
        return handle;
    }

    internal void Start(int frame, bool loops, float choice, float space)
    {
        lock (sync)
        {
            if (native == null)
            {
                native = new NativeScore(Rate);
                pitchIn = new float[native.Block * 2];
                pitchOut = new float[native.Block * 2];
            }
            looping = loops;
            cursor = Math.Max(0, Math.Min(frame, Frames));
            take = choice;
            sceneSpace = space;
            readFrames = 0;
            accentLength = 0;
            primed = false;
            pitchMix = (ScoreControls.Flags & ScoreControls.Pitch) != 0 ? 1f : 0f;
            native.ResetEffects();
        }
    }

    internal void Release()
    {
        lock (sync)
        {
            if (lookahead != IntPtr.Zero)
            {
                NativeScore.RomMp3Close(lookahead);
                lookahead = IntPtr.Zero;
            }
            if (native != null)
            {
                native.Dispose();
                native = null;
                pitchIn = null;
                pitchOut = null;
                primed = false;
            }
        }
    }

    internal void SetScene(float space)
    {
        lock (sync)
            sceneSpace = space;
    }

    internal void Beat(float cents, float space, float seconds)
    {
        lock (sync)
        {
            accentCents = cents;
            accentSpace = space;
            accentLength = (int)(Rate * seconds);
            accentStart = readFrames;
        }
    }

    private float Accent(long time)
    {
        long elapsed = time - accentStart;
        if (accentLength <= 0 || elapsed < 0 || elapsed >= accentLength)
            return 0f;
        float attack = Math.Min(1f, elapsed / (Rate * .65f));
        float release = Math.Min(1f, (accentLength - elapsed) / (Rate * 1.1f));
        float level = Math.Min(attack, release);
        return level * level * (3f - 2f * level);
    }

    public void Read(float[] samples)
    {
        lock (sync)
        {
            if (failed)
            {
                Array.Clear(samples, 0, samples.Length);
                return;
            }
            try
            {
                int frames = samples.Length / 2;
                Decode(reader, samples, cursor, frames);
                if (native != null)
                {
                    int flags = ScoreControls.Flags;
                    bool changePitch = (flags & ScoreControls.Pitch) != 0;
                    if (changePitch || pitchMix > 0f)
                        MixShift(samples, frames, changePitch);
                    else
                        primed = false;

                    float beat = Accent(readFrames + frames / 2) * accentSpace;
                    float wet = (flags & ScoreControls.Effects) != 0 ? Profile.Wet : 0f;
                    native.Effects(samples, frames, wet,
                        Math.Min(.68f, Profile.Room + beat * .28f + sceneSpace * .09f),
                        Math.Min(.42f, Profile.Echo + beat * .23f),
                        Math.Max(1800f, Profile.Cutoff * (1f - beat * .34f - sceneSpace * .08f)),
                        Math.Min(1.4f, Profile.Width + beat * .14f));
                }
                cursor = Advance(cursor, frames);
                readFrames += frames;
            }
            catch (IOException)
            {
                failed = true;
                Array.Clear(samples, 0, samples.Length);
            }
        }
    }

    private void MixShift(float[] samples, int frames, bool enabled)
    {
        if (lookahead == IntPtr.Zero)
        {
            int length;
            lookahead = Open(out length);
        }
        if (!primed)
        {
            discard = native.Prime(Profile.PitchAt(cursor, take, Accent(readFrames) * accentCents));
            readAhead = cursor;
            pitchOffset = native.Block;
            primed = true;
        }

        for (int n = 0; n < frames; ++n)
        {
            while (pitchOffset >= native.Block)
            {
                Decode(lookahead, pitchIn, readAhead, native.Block);
                float pulse = Accent(readFrames + n + native.Block / 2) * accentCents;
                native.Shift(pitchIn, pitchOut, Profile.PitchAt(Advance(cursor, n), take, pulse));
                readAhead = Advance(readAhead, native.Block);
                pitchOffset = Math.Min(native.Block, discard);
                discard -= pitchOffset;
            }
            pitchMix = enabled ? Math.Min(1f, pitchMix + FadeStep) : Math.Max(0f, pitchMix - FadeStep);
            for (int channel = 0; channel < 2; ++channel)
            {
                int index = 2 * n + channel;
                float wet = pitchOut[2 * pitchOffset + channel];
                if (pitchMix >= 1f)
                    samples[index] = wet;
                else if (pitchMix > 0f)
                    samples[index] += (wet - samples[index]) * pitchMix;
            }
            ++pitchOffset;
        }
    }

    private int Advance(int frame, int count)
    {
        return looping ? (frame + count) % Frames : Math.Min(Frames, frame + count);
    }

    private void Decode(IntPtr decoder, float[] destination, int frame, int frames)
    {
        int sample = 0;
        while (frames > 0 && frame < Frames)
        {
            int available = Math.Min(frames, Frames - frame);
            int read = NativeScore.RomMp3Read(decoder, frame, destination, sample, available);
            if (read != available * 2)
                throw new IOException("Unable to decode score MP3: " + path);
            sample += read;
            frames -= available;
            frame = Advance(frame, available);
        }
        Array.Clear(destination, sample, destination.Length - sample);
    }

    public void Seek(int frame)
    {
        lock (sync)
        {
            frame = Math.Max(0, Math.Min(frame, Frames));
            if (frame == cursor)
                return;
            bool loopRestart = looping && frame == 0 && cursor < 2048;
            cursor = frame;
            primed = false;
            if (native != null && !loopRestart)
            {
                pitchMix = 0f;
                native.ResetEffects();
            }
        }
    }

    public void Dispose()
    {
        Release();
        lock (sync)
        {
            if (reader != IntPtr.Zero)
            {
                NativeScore.RomMp3Close(reader);
                reader = IntPtr.Zero;
                failed = true;
            }
        }
    }
}

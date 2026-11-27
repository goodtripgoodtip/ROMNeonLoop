using System;
using System.Runtime.InteropServices;

internal sealed class NativeScore : IDisposable
{
    private const string Library = "RomScoreDSP.dll";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr RomMp3Open(string path, out int frames);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RomMp3Close(IntPtr state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RomMp3Read(IntPtr state, int frame, [In, Out] float[] output, int offset, int frames);

    private IntPtr handle;

    internal int Block { get; private set; }

    internal NativeScore(int sampleRate)
    {
        handle = RomScoreCreate(sampleRate);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Native score allocation failed.");
        Block = RomScoreBlock(handle);
    }

    internal int Prime(float cents) { return RomScorePrime(handle, cents); }

    internal void Shift(float[] source, float[] output, float cents)
    {
        RomScorePitch(handle, source, output, cents);
    }

    internal void Effects(float[] samples, int frames, float wet, float room, float echo, float cutoff, float width)
    {
        RomScoreEffects(handle, samples, frames, wet, room, echo, cutoff, width);
    }

    internal void ResetEffects() { RomScoreResetEffects(handle); }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            RomScoreDestroy(handle);
            handle = IntPtr.Zero;
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr RomScoreCreate(int rate);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void RomScoreDestroy(IntPtr state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int RomScoreBlock(IntPtr state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int RomScorePrime(IntPtr state, float cents);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void RomScorePitch(IntPtr state, [In] float[] input, [Out] float[] output, float cents);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void RomScoreResetEffects(IntPtr state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void RomScoreEffects(IntPtr state, [In, Out] float[] audio, int frames,
        float wet, float room, float echo, float cutoff, float width);
}

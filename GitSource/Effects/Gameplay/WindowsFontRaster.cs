using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal sealed class WindowsFontRaster : IDisposable
{
    private const uint Error = uint.MaxValue;
    private const uint MarkMissingGlyph = 1;
    private const uint Gray8GlyphIndex = 6 | 128;
    private static readonly IntPtr InvalidObject = new IntPtr(-1);
    private readonly IntPtr context;
    private readonly IntPtr initialFont;
    private readonly List<IntPtr> faces = new List<IntPtr>();
    private IntPtr selected;

    internal sealed class Glyph
    {
        internal readonly int Width;
        internal readonly int Height;
        private readonly int stride;
        private readonly byte[] bitmap;

        internal Glyph(int width, int height, int rowStride, byte[] data)
        {
            Width = width;
            Height = height;
            stride = rowStride;
            bitmap = data;
        }

        internal float Coverage(int x, int y)
        {
            return bitmap[(Height - y - 1) * stride + x] / 64f;
        }
    }

    internal WindowsFontRaster(string[] families, int size)
    {
        context = CreateCompatibleDC(IntPtr.Zero);
        if (context == IntPtr.Zero)
            return;
        initialFont = GetCurrentObject(context, 6);
        foreach (string family in families)
        {
            IntPtr font = CreateFontW(-size, 0, 0, 0, 400, 0, 0, 0, 1, 7, 0, 4, 0, family);
            if (font != IntPtr.Zero)
                faces.Add(font);
        }
    }

    internal Glyph Read(char character)
    {
        foreach (IntPtr font in faces)
        {
            if (!Select(font))
                continue;
            ushort index;
            if (GetGlyphIndicesW(context, character.ToString(), 1, out index, MarkMissingGlyph) != 1 ||
                index == ushort.MaxValue)
                continue;
            var matrix = new Matrix { M11 = 1 << 16, M22 = 1 << 16 };
            Metrics metrics;
            uint size = GetGlyphOutlineW(context, index, Gray8GlyphIndex, out metrics, 0, null, ref matrix);
            if (size == Error || size == 0 || metrics.Width == 0 || metrics.Height == 0 || size > 65536)
                continue;
            int stride = ((int)metrics.Width + 3) & ~3;
            if (stride * metrics.Height != size)
                continue;
            var buffer = new byte[size];
            uint copied = GetGlyphOutlineW(context, index, Gray8GlyphIndex, out metrics, size, buffer, ref matrix);
            if (copied == size)
                return new Glyph((int)metrics.Width, (int)metrics.Height, stride, buffer);
        }
        return null;
    }

    private bool Select(IntPtr font)
    {
        if (selected == font)
            return true;
        IntPtr previous = SelectObject(context, font);
        if (previous == IntPtr.Zero || previous == InvalidObject)
            return false;
        selected = font;
        return true;
    }

    public void Dispose()
    {
        if (context != IntPtr.Zero)
        {
            SelectObject(context, initialFont);
            DeleteDC(context);
        }
        foreach (IntPtr face in faces)
            DeleteObject(face);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Matrix
    {
        internal int M11, M12, M21, M22;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Metrics
    {
        internal uint Width, Height;
        internal int OriginX, OriginY;
        internal short AdvanceX, AdvanceY;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr source);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentObject(IntPtr context, uint type);

    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string family);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr context, IntPtr value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteDC(IntPtr context);

    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr context, string text, int count, out ushort index, uint flags);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern uint GetGlyphOutlineW(IntPtr context, uint index, uint format, out Metrics metrics,
        uint length, [Out] byte[] buffer, ref Matrix transform);
}

using System;
using System.Collections.Generic;
using UnityEngine;

internal static class GameplayFontAtlas
{
    internal static readonly string[] FontFamilies = { "Yu Gothic UI", "Meiryo", "Segoe UI" };
    private const int GlyphSize = 48;
    private const string ControlGlyphs = "§µ¼½¾ÇÕøþŒšƒΩ„†‡∂√∞≈≤◊";

    private struct Slot
    {
        internal char Character;
        internal RectInt Bounds;
    }

    internal static Texture2D Build(object sheet, Texture source)
    {
        int limit = Mathf.Min(SystemInfo.maxTextureSize, 4096);
        int scale = Mathf.Clamp(limit / Mathf.Max(source.width, source.height), 1, 8);
        int width = source.width * scale;
        int height = source.height * scale;
        List<Slot> slots = ReadSlots(sheet, source.width, source.height);
        if (slots.Count == 0)
            return null;

        Color32[] stock = ReadPixels(source);
        Color32[] pixels = Enlarge(stock, source.width, width, height, scale);
        bool changed = false;
        using (var raster = new WindowsFontRaster(FontFamilies, GlyphSize))
        {
            foreach (Slot slot in slots)
            {
                WindowsFontRaster.Glyph glyph = raster.Read(slot.Character);
                if (glyph != null)
                    changed |= FillSlot(slot.Bounds, glyph, stock, source.width, pixels, width, scale);
            }
        }
        return changed ? CreateTexture(pixels, width, height, source) : null;
    }

    private static List<Slot> ReadSlots(object sheet, int width, int height)
    {
        var slots = new List<Slot>();
        var usage = new Dictionary<RectInt, int>();
        var rows = GameAccess.Get<Array>(sheet, "_rows");
        if (rows == null)
            return slots;
        foreach (object row in rows)
        {
            var cells = GameAccess.Get<Array>(row, "_cells");
            if (cells == null)
                continue;
            string value = GameAccess.Get<string>(row, "fontCharacter");
            for (int i = 0; i < cells.Length; ++i)
            {
                Rect uv = GameAccess.Get<Rect>(cells.GetValue(i), "uv");
                int x = Mathf.RoundToInt(uv.xMin * width);
                int y = Mathf.RoundToInt(uv.yMin * height);
                int right = Mathf.RoundToInt(uv.xMax * width);
                int top = Mathf.RoundToInt(uv.yMax * height);
                if (x < 0 || y < 0 || right > width || top > height || right <= x || top <= y)
                    continue;
                var bounds = new RectInt(x, y, right - x, top - y);
                int count;
                usage.TryGetValue(bounds, out count);
                usage[bounds] = count + 1;
                if (i == 0 && value != null && value.Length == 1 && IsText(value[0]))
                    slots.Add(new Slot { Character = value[0], Bounds = bounds });
            }
        }
        slots.RemoveAll(slot => usage[slot.Bounds] != 1);
        return slots;
    }

    private static bool IsText(char value)
    {
        return ControlGlyphs.IndexOf(value) < 0 &&
            !char.IsControl(value) && !char.IsWhiteSpace(value) && !char.IsSurrogate(value) &&
            char.GetUnicodeCategory(value) != System.Globalization.UnicodeCategory.PrivateUse;
    }

    private static Color32[] Enlarge(Color32[] stock, int stockWidth, int width, int height, int scale)
    {
        if (scale == 1)
            return (Color32[])stock.Clone();
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y += scale)
        {
            int row = y * width;
            int source = y / scale * stockWidth;
            for (int x = 0; x < width; ++x)
                pixels[row + x] = stock[source + x / scale];
            for (int dy = 1; dy < scale; ++dy)
                Array.Copy(pixels, row, pixels, row + dy * width, width);
        }
        return pixels;
    }

    private static bool FillSlot(RectInt bounds, WindowsFontRaster.Glyph glyph, Color32[] stock,
        int stockWidth, Color32[] pixels, int width, int scale)
    {
        RectInt ink = InkBounds(bounds, stock, stockWidth);
        if (ink.width == 0 || ink.height == 0)
            return false;

        int left = bounds.xMin * scale, bottom = bounds.yMin * scale;
        int cellWidth = bounds.width * scale, cellHeight = bounds.height * scale;
        int inkWidth = ink.width * scale, drawHeight = ink.height * scale;
        int maxWidth = Mathf.CeilToInt(drawHeight * glyph.Width * 1.25f / glyph.Height);
        int drawWidth = Mathf.Clamp(maxWidth, 1, inkWidth);
        int xOffset = (ink.x - bounds.x) * scale + (inkWidth - drawWidth) / 2;
        int yOffset = (ink.y - bounds.y) * scale;
        var glyphAlpha = new byte[drawWidth * drawHeight];
        bool hasInk = false;
        for (int y = 0; y < drawHeight; ++y)
            for (int x = 0; x < drawWidth; ++x)
            {
                byte alpha = SampleAlpha(glyph, (x + .5f) * glyph.Width / drawWidth,
                    (y + .5f) * glyph.Height / drawHeight);
                glyphAlpha[y * drawWidth + x] = alpha;
                hasInk |= alpha > 32;
            }
        if (!hasInk)
            return false;
        for (int y = 0; y < cellHeight; ++y)
            Array.Clear(pixels, (bottom + y) * width + left, cellWidth);
        for (int y = 0; y < drawHeight; ++y)
            for (int x = 0; x < drawWidth; ++x)
            {
                byte alpha = glyphAlpha[y * drawWidth + x];
                pixels[(bottom + yOffset + y) * width + left + xOffset + x] =
                    new Color32(alpha, alpha, alpha, alpha);
            }
        return true;
    }

    private static RectInt InkBounds(RectInt slot, Color32[] stock, int width)
    {
        int left = slot.xMax, bottom = slot.yMax, right = slot.xMin, top = slot.yMin;
        for (int y = slot.yMin; y < slot.yMax; ++y)
            for (int x = slot.xMin; x < slot.xMax; ++x)
                if (stock[y * width + x].a > 32)
                {
                    left = Mathf.Min(left, x);
                    bottom = Mathf.Min(bottom, y);
                    right = Mathf.Max(right, x + 1);
                    top = Mathf.Max(top, y + 1);
                }
        return right <= left ? new RectInt() : new RectInt(left, bottom, right - left, top - bottom);
    }

    private static byte SampleAlpha(WindowsFontRaster.Glyph glyph, float column, float row)
    {
        float x = Mathf.Clamp(column - .5f, 0, glyph.Width - 1);
        float y = Mathf.Clamp(row - .5f, 0, glyph.Height - 1);
        int x0 = (int)x, x1 = Mathf.Min(x0 + 1, glyph.Width - 1);
        int y0 = (int)y, y1 = Mathf.Min(y0 + 1, glyph.Height - 1);
        float a = Mathf.Lerp(glyph.Coverage(x0, y0), glyph.Coverage(x1, y0), x - x0);
        float b = Mathf.Lerp(glyph.Coverage(x0, y1), glyph.Coverage(x1, y1), x - x0);
        return (byte)Mathf.RoundToInt(Mathf.Lerp(a, b, y - y0) * 255f);
    }

    private static Color32[] ReadPixels(Texture source)
    {
        var readable = source as Texture2D;
        if (readable != null && readable.isReadable)
            return readable.GetPixels32();
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Texture2D copy = null;
        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            return copy.GetPixels32();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (copy != null)
                UnityEngine.Object.Destroy(copy);
        }
    }

    private static Texture2D CreateTexture(Color32[] pixels, int width, int height, Texture source)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "NeonLoop readable " + source.name,
            filterMode = FilterMode.Bilinear,
            wrapMode = source.wrapMode,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }
}

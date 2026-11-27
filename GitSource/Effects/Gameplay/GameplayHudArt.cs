using System.Collections.Generic;
using UnityEngine;

internal static class GameplayHudArt
{
    private static readonly Dictionary<string, Texture2D> Icons = new Dictionary<string, Texture2D>();
    internal static Color SpeakerColor(object scene, object writer)
    {
        object portrait = GameAccess.Get<object>(scene, "profileBox");
        if (!GameAccess.Get<bool>(writer, "isPortrait") || !GameAccess.Get<bool>(portrait, "ProfileVisible"))
            return Color.white;
        string speaker = GameAccess.Get<string>(portrait, "target");
        if (string.IsNullOrWhiteSpace(speaker))
            return Color.white;
        uint hash = 2166136261u;
        foreach (char c in speaker)
            hash = unchecked((hash ^ char.ToUpperInvariant(c)) * 16777619u);
        return Color.HSVToRGB((hash % 360u) / 360f, .31f, 1f);
    }

    internal static Rect Viewport(Camera camera)
    {
        Rect view = camera.pixelRect;
        Rect safe = Screen.safeArea;
        float left = Mathf.Max(view.xMin, safe.xMin);
        float right = Mathf.Min(view.xMax, safe.xMax);
        float top = Screen.height - Mathf.Min(view.yMax, safe.yMax);
        float bottom = Screen.height - Mathf.Max(view.yMin, safe.yMin);
        return Rect.MinMaxRect(left, top, right, bottom);
    }

    internal static bool TryBounds(Camera camera, Component component, out Rect area)
    {
        area = new Rect();
        if (component == null || !component.gameObject.activeInHierarchy)
            return false;
        var renderer = component.GetComponent<Renderer>();
        if (renderer == null || !renderer.enabled)
            return false;
        return TryBounds(camera, renderer.bounds, out area);
    }

    internal static bool TryBounds(Camera camera, Bounds bounds, out Rect area)
    {
        area = new Rect();
        Vector3 a = camera.WorldToScreenPoint(bounds.min);
        Vector3 b = camera.WorldToScreenPoint(bounds.max);
        if (a.z <= 0 || b.z <= 0)
            return false;
        area = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y),
            Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
        return area.width > 1 && area.height > 1 && Viewport(camera).Overlaps(area);
    }

    internal static void Marker(Vector2 center, float size, bool pulse, bool complete)
    {
        Color amber = new Color(1f, .81f, .26f);
        Color cyan = new Color(.25f, 1f, .96f);
        float phase = .5f - .5f * Mathf.Cos(Time.unscaledTime * (2f * Mathf.PI / 1.6f));
        Rect box = new Rect(center.x - size / 2f, center.y - size / 2f, size, size);
        Draw("target", box, new Color(.025f, .018f, .045f), new Color(.025f, .018f, .045f));
        box = new Rect(box.x + size * .12f, box.y + size * .12f, size * .76f, size * .76f);
        Draw(complete ? "check" : "target", box, pulse ? Color.Lerp(cyan, amber, phase) : cyan, Color.black);
    }

    internal static void Action(string verb, Rect box, bool complete)
    {
        Color fill = new Color(.76f, .92f, 1f);
        Draw(verb, box, fill, new Color(.055f, .035f, .11f, .98f));
        if (complete)
        {
            float size = box.width * .49f;
            Check(new Rect(box.xMax - size, box.yMax - size, size, size));
        }
    }

    internal static void Check(Rect box)
    {
        Draw("check", box, new Color(.64f, 1f, .72f), new Color(.055f, .035f, .1f, .98f));
    }

    private static void Draw(string key, Rect box, Color foreground, Color background)
    {
        Color saved = GUI.color;
        try
        {
            GUI.color = background;
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = foreground;
            GUI.DrawTexture(box, Texture(key));
        }
        finally { GUI.color = saved; }
    }

    private static Texture2D Texture(string key)
    {
        if (key != "look" && key != "talk" && key != "use" && key != "item" &&
            key != "scroll" && key != "target" && key != "check")
            key = "use";
        Texture2D texture;
        if (Icons.TryGetValue(key, out texture) && texture != null)
            return texture;
        string[] glyph;
        switch (key)
        {
            case "look":
                glyph = new[] { "..###..", ".#...#.", "#..#..#", "#.###.#", "#..#..#", ".#...#.", "..###.." };
                break;
            case "talk":
                glyph = new[] { ".#####.", "#.....#", "#.#.#.#", "#.....#", ".#####.", "..#....", ".#....." };
                break;
            case "item":
                glyph = new[] { "..###..", ".#...#.", "#######", "#.....#", "#.###.#", "#.....#", "#######" };
                break;
            case "scroll":
                glyph = new[] { ".#####.", ".#...#.", ".###.#.", ".#...#.", ".###.#.", ".#...#.", ".#####." };
                break;
            case "target":
                glyph = new[] { "##...##", "#.....#", ".......", "...#...", ".......", "#.....#", "##...##" };
                break;
            case "check":
                glyph = new[] { ".......", ".....#.", "....##.", "#..##..", "####...", ".##....", "......." };
                break;
            default:
                glyph = new[] { "..##...", "..##...", "..####.", "#.###.#", "##....#", ".#...#.", "..###.." };
                break;
        }
        texture = new Texture2D(9, 9, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.hideFlags = HideFlags.HideAndDontSave;
        var pixels = new Color32[81];
        for (int y = 0; y < glyph.Length; ++y)
            for (int x = 0; x < glyph[y].Length; ++x)
                if (glyph[y][x] == '#')
                    pixels[(7 - y) * 9 + x + 1] = new Color32(255, 255, 255, 255);
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        Icons[key] = texture;
        return texture;
    }
}

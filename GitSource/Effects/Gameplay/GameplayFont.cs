using System;
using System.Collections.Generic;
using UnityEngine;

public static class GameplayFont
{
    private static readonly int MainTex = Shader.PropertyToID("_MainTex");
    private static readonly Dictionary<Component, TextRenderer> Renderers = new Dictionary<Component, TextRenderer>();
    private static readonly Dictionary<UnityEngine.Object, Atlas> Atlases = new Dictionary<UnityEngine.Object, Atlas>();
    private static readonly List<Component> Expired = new List<Component>();
    private static readonly List<UnityEngine.Object> ExpiredAtlases = new List<UnityEngine.Object>();
    private static bool enabled = true;
    private static float nextSweep;
    private static TextRenderer speakerText;

    private sealed class Atlas
    {
        internal Texture Source;
        internal Texture2D Readable;
    }

    private sealed class TextRenderer
    {
        internal readonly Component Text;
        private readonly MeshRenderer renderer;
        private readonly MeshFilter meshFilter;
        private MaterialPropertyBlock originalProperties;
        private Texture2D readable;
        private Mesh tintedMesh;
        private Color32[] originalColors;
        private Color32 appliedColor;

        internal TextRenderer(Component text, MeshRenderer renderer, MeshFilter meshFilter)
        {
            Text = text;
            this.renderer = renderer;
            this.meshFilter = meshFilter;
        }

        internal void Apply()
        {
            if (renderer == null)
                return;
            Texture2D replacement = enabled ? FindAtlas(Text, renderer.sharedMaterial) : null;
            if (readable == replacement)
                return;
            if (originalProperties != null)
            {
                renderer.SetPropertyBlock(originalProperties);
                originalProperties = null;
            }
            if (replacement != null)
            {
                originalProperties = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(originalProperties);
                var properties = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(properties);
                properties.SetTexture(MainTex, replacement);
                renderer.SetPropertyBlock(properties);
            }
            readable = replacement;
        }

        internal void StockColorsWritten()
        {
            tintedMesh = null;
            originalColors = null;
        }

        internal void Tint(Color32 color)
        {
            Mesh mesh = meshFilter == null ? null : meshFilter.sharedMesh;
            if (originalColors != null && tintedMesh == mesh && readable != null && color.Equals(appliedColor))
                return;
            if (tintedMesh != null && originalColors != null && tintedMesh.vertexCount == originalColors.Length)
                tintedMesh.colors32 = originalColors;
            StockColorsWritten();
            if (readable == null || mesh == null || color.Equals((Color32)Color.white))
                return;
            Color32 tint = GameAccess.Get<Color>(Text, "tintColor");
            if (Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b)) - Mathf.Min(tint.r, Mathf.Min(tint.g, tint.b)) > 52 ||
                Mathf.Min(tint.r, Mathf.Min(tint.g, tint.b)) < 140)
                return;
            originalColors = mesh.colors32;
            var colors = (Color32[])originalColors.Clone();
            for (int i = 0; i < colors.Length; ++i)
            {
                Color32 current = colors[i];
                if (current.a != 0 && current.r == tint.r && current.g == tint.g && current.b == tint.b)
                    colors[i] = new Color32(
                        (byte)(current.r * color.r / 255), (byte)(current.g * color.g / 255),
                        (byte)(current.b * color.b / 255), current.a);
            }
            mesh.colors32 = colors;
            tintedMesh = mesh;
            appliedColor = color;
        }
    }

    public static void Refresh(object source)
    {
        try
        {
            var text = source as Component;
            if (text == null)
                return;
            TextRenderer entry;
            if (!Renderers.TryGetValue(text, out entry))
            {
                var renderer = text.GetComponent<MeshRenderer>();
                var mesh = text.GetComponent<MeshFilter>();
                if (renderer == null || mesh == null)
                    return;
                Renderers[text] = entry = new TextRenderer(text, renderer, mesh);
            }
            entry.Apply();
        }
        catch (Exception error) { GameplayHud.Report(error); }
    }

    public static void StockColorsWritten(object source)
    {
        var text = source as Component;
        TextRenderer entry;
        if (text != null && Renderers.TryGetValue(text, out entry))
            entry.StockColorsWritten();
    }

    internal static void Tick(bool useReadableFont)
    {
        if (enabled != useReadableFont)
        {
            enabled = useReadableFont;
            foreach (TextRenderer entry in Renderers.Values)
                if (entry.Text != null)
                    entry.Apply();
        }
        RefreshSpeaker();
        if (Time.unscaledTime >= nextSweep)
        {
            nextSweep = Time.unscaledTime + 5;
            Sweep();
        }
    }

    private static void RefreshSpeaker()
    {
        TextRenderer current = null;
        Color color = Color.white;
        if (enabled)
        {
            object manager = GameAccess.Get<object>(GameAccess.Type("DialogManager"), "_instance");
            object writer = GameAccess.Get<object>(manager, "dialogTypewriter");
            Behaviour text = GameplayHooks.DialogueRenderer(writer);
            if (GameAccess.IsActive(text) && Renderers.TryGetValue(text, out current))
                color = GameplayHudArt.SpeakerColor(GameAccess.Scene, writer);
        }
        if (speakerText != null && speakerText != current)
            speakerText.Tint(Color.white);
        speakerText = current;
        if (speakerText != null)
            speakerText.Tint(color);
    }

    private static Texture2D FindAtlas(Component text, Material material)
    {
        if (material == null || !material.HasProperty(MainTex))
            return null;
        var sheet = GameAccess.Get<UnityEngine.Object>(text, "spriteSheet");
        Texture source = material.GetTexture(MainTex);
        if (sheet == null || source == null)
            return null;
        Atlas atlas;
        if (Atlases.TryGetValue(sheet, out atlas) && atlas.Source == source)
            return atlas.Readable;
        if (atlas != null && atlas.Readable != null)
            UnityEngine.Object.Destroy(atlas.Readable);
        Atlases[sheet] = atlas = new Atlas { Source = source };
        atlas.Readable = GameplayFontAtlas.Build(sheet, source);
        return atlas.Readable;
    }

    private static void Sweep()
    {
        foreach (Component text in Renderers.Keys)
            if (text == null)
                Expired.Add(text);
        foreach (Component text in Expired)
            Renderers.Remove(text);
        Expired.Clear();

        foreach (var pair in Atlases)
            if (pair.Key == null)
            {
                if (pair.Value.Readable != null)
                    UnityEngine.Object.Destroy(pair.Value.Readable);
                ExpiredAtlases.Add(pair.Key);
            }
        foreach (UnityEngine.Object sheet in ExpiredAtlases)
            Atlases.Remove(sheet);
        ExpiredAtlases.Clear();
    }
}

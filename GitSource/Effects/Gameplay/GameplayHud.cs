using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public sealed class GameplayHud : MonoBehaviour
{
    private static readonly HashSet<string> Reported = new HashSet<string>();
    private GUIStyle choice;
    private GUIStyle text;
    private GUIStyle selected;
    private int fontSize;
    private readonly List<string> actions = new List<string>();

    internal static void Report(Exception error)
    {
        string message = error.GetBaseException().Message;
        if (Reported.Count < 12 && Reported.Add(message))
            Debug.LogWarning("NeonLoop gameplay: " + message);
    }

    private void OnGUI()
    {
        if (!GameplayHooks.HasHud || OverlayInput.IsMenuOpen() || !Application.isFocused)
            return;
        bool retry = GameplayHooks.TaxiReady;
        if (Event.current.type != EventType.Repaint && !retry)
            return;
        try
        {
            int depth = GUI.depth;
            GUI.depth = -50;
            try
            {
                EnsureStyles();
                DrawHud(retry);
            }
            finally { GUI.depth = depth; }
        }
        catch (Exception error) { Report(error); }
    }

    private void EnsureStyles()
    {
        int size = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 48f), 12, 27);
        if (text != null && size == fontSize)
            return;
        var font = text == null ? Font.CreateDynamicFontFromOSFont(
            GameplayFontAtlas.FontFamilies, size) : text.font;
        fontSize = size;
        text = new GUIStyle(GUI.skin.label) { font = font, fontSize = size, wordWrap = true, richText = false };
        text.normal.textColor = Color.white;
        choice = new GUIStyle(text) { fontSize = Mathf.RoundToInt(size * 1.42f) };
        selected = new GUIStyle(choice) { fontStyle = FontStyle.Bold };
        selected.normal.textColor = new Color(.85f, 1f, .73f);
    }

    private void DrawHud(bool retry)
    {
        object scene = GameAccess.Scene;
        if (GameplayHooks.Enabled(GameplayHooks.Option.SaveFeedback))
            Banner(GameplaySaves.Notice, Screen.height * .09f, text);
        if (GameplayHooks.Enabled(GameplayHooks.Option.ContinuePreview))
            Banner(GameplaySaves.Preview, Screen.height * .80f, text);
        if (!GameAccess.Playing(scene))
            return;

        object active = GameAccess.ActiveMenu;
        Camera camera = Camera.main;
        DrawDialogue(active, camera);
        if (retry && GUI.Button(new Rect(Screen.width * .72f, Screen.height * .68f, Screen.width * .24f, fontSize * 2.3f),
            "Retry taxi  [F7]"))
            GameplayHooks.Retry();
        if (GameplayHooks.Enabled(GameplayHooks.Option.Objective))
            Banner(string.IsNullOrEmpty(GameplayExplore.Objective) ? null : "Goal: " + GameplayExplore.Objective,
                Screen.height * .04f, text);

        if (camera == null)
            return;
        if (GameplayHooks.Enabled(GameplayHooks.Option.Visited) && IsMenu(active, "MapMenu"))
            DrawVisited(active, camera);
        if (GameplayHooks.Enabled(GameplayHooks.Option.GlyphLabels) && IsMenu(active, "GlyphWidget"))
            DrawGlyphs(active, camera);
        if (GameplayHooks.Enabled(GameplayHooks.Option.TunnelExits) && IsMenu(active, "MaintenanceChase"))
            DrawExits(active);
        object setMenu = GameAccess.Get<object>(scene, "setMenu");
        if (RoomAcceptsHotspots(active, setMenu))
            DrawHotspots(setMenu, camera);
    }

    internal static bool RoomAcceptsHotspots(object active, object room)
    {
        return ReferenceEquals(active, room) && GameAccess.Get<bool>(room, "Enabled");
    }

    private static bool IsMenu(object menu, string type)
    {
        return GameAccess.IsActive(menu) && menu.GetType() == GameAccess.Type(type);
    }

    private void DrawDialogue(object active, Camera camera)
    {
        object manager = GameplayHooks.Dialog ?? GameAccess.Get<object>(GameAccess.Type("DialogManager"), "_instance");
        object choices = GameAccess.Get<object>(manager, "_dialogMenu");
        if (GameAccess.IsActive(choices) && ReferenceEquals(choices, active))
        {
            var items = GameAccess.Get<IList>(choices, "menuItems");
            bool[] marks = GameplayProgress.ObserveChoices(manager, items);
            if (GameplayHooks.Enabled(GameplayHooks.Option.LargeChoices))
                DrawChoices(choices, items, marks);
            else if (camera != null && marks != null)
                DrawChoiceMarks(choices, camera, marks);
        }
    }

    private void DrawChoiceMarks(object choices, Camera camera, bool[] marks)
    {
        object native = GameAccess.Get<object>(choices, "textObject");
        var regions = GameAccess.Get<IDictionary>(native, "TextColliderDictionary");
        if (regions == null)
            return;
        Rect view = GameplayHudArt.Viewport(camera);
        float size = Mathf.Clamp(fontSize, 14f, 26f);
        for (int i = 0; i < marks.Length; ++i)
        {
            if (!marks[i])
                continue;
            var cursor = GameAccess.Get<Component>(regions[i.ToString()], "cursorCollider");
            var collider = cursor == null ? null : cursor.GetComponent<Collider2D>();
            Rect row;
            if (collider != null && GameplayHudArt.TryBounds(camera, collider.bounds, out row) &&
                row.xMax + size + 5 <= view.xMax)
                GameplayHudArt.Check(new Rect(row.xMax + 4, row.yMin, size, size));
        }
    }

    private void DrawChoices(object choices, IList items, bool[] marks)
    {
        if (items == null || items.Count == 0)
            return;
        int highlight = GameAccess.Get<int>(choices, "highlightedIndex");
        float width = Screen.width * .66f;
        var lines = new List<string>();
        var heights = new List<float>();
        float total = 16;
        foreach (object item in items)
        {
            string label = GameplayHooks.Plain(GameAccess.Get<string>(item, "plaintext"));
            if (label.Length == 0)
                label = GameplayHooks.Plain(GameAccess.Get<string>(item, "line"));
            float lineHeight = choice.CalcHeight(new GUIContent(label), width - 24 - fontSize);
            lines.Add(label);
            heights.Add(lineHeight);
            total += lineHeight;
        }
        var panel = new Rect((Screen.width - width) * .5f, Mathf.Max(5, Screen.height * .93f - total), width, total);
        Backdrop(panel);
        float y = panel.y + 8;
        for (int i = 0; i < lines.Count; ++i)
        {
            GUI.Label(new Rect(panel.x + 12, y, width - 24 - fontSize, heights[i]), lines[i],
                i == highlight ? selected : choice);
            if (marks != null && marks[i])
                GameplayHudArt.Check(new Rect(panel.xMax - 12 - fontSize, y + 2, fontSize, fontSize));
            y += heights[i];
        }
    }

    private void DrawHotspots(object menu, Camera camera)
    {
        bool reveal = GameplayHooks.ShowHotspots && GameplayHooks.Enabled(GameplayHooks.Option.Hotspots);
        bool examined = GameplayHooks.ShowHotspots && GameplayHooks.Enabled(GameplayHooks.Option.Examined);
        bool verbs = GameplayHooks.Enabled(GameplayHooks.Option.Verbs);
        if (!reveal && !examined && !verbs)
            return;
        Rect view = GameplayHudArt.Viewport(camera);
        float size = Mathf.Clamp(Screen.height / 60f, 13f, 30f);
        Component hover = null;
        Vector3 hoveredAt = new Vector3();
        foreach (object item in GameplayExplore.Items(menu))
        {
            GameObject target = GameplayExplore.ItemObject(item);
            if (target == null || !target.activeInHierarchy)
                continue;
            Component cursor = target.GetComponent("CursorCollider");
            if (!GameAccess.IsActive(cursor))
                continue;
            Component tag = GameplayExplore.Tag(target);
            if (tag == null)
                continue;
            GameplayExplore.GetActions(menu, tag, actions);
            if (actions.Count == 0)
                continue;
            Vector3 screen = camera.WorldToScreenPoint(GameAccess.Get<Vector3>(cursor, "Center"));
            var point = new Vector2(screen.x, Screen.height - screen.y);
            if (screen.z <= 0 || !view.Contains(point))
                continue;
            bool complete = examined && AllActionsDone(tag);
            if (reveal || complete)
                GameplayHudArt.Marker(point, size, reveal, complete);
            if (verbs && (hover == null || screen.z < hoveredAt.z) &&
                Equals(GameAccess.Call(cursor, "HasMouseOver"), true))
            {
                hover = tag;
                hoveredAt = screen;
            }
        }
        if (hover == null)
            return;
        GameplayExplore.GetActions(menu, hover, actions);
        DrawActionIcons(hover, hoveredAt, view, size);
    }

    private bool AllActionsDone(Component tag)
    {
        foreach (string action in actions)
            if (!GameplayProgress.ActionComplete(tag, action))
                return false;
        return true;
    }

    private void DrawActionIcons(Component tag, Vector3 point, Rect view, float size)
    {
        float tile = size * 1.4f;
        float step = tile + 4;
        float width = step * actions.Count - 4;
        if (width > view.width - 8)
            return;
        float x = Mathf.Clamp(point.x - width * .5f, view.xMin + 4, view.xMax - width - 4);
        float centerY = Screen.height - point.y;
        float y = centerY - tile - size;
        if (y < view.yMin + 4)
            y = centerY + size;
        if (y + tile > view.yMax - 4)
            return;
        bool showCompletion = GameplayHooks.Enabled(GameplayHooks.Option.Examined);
        for (int i = 0; i < actions.Count; ++i)
        {
            string action = actions[i];
            GameplayHudArt.Action(action, new Rect(x + i * step, y, tile, tile),
                showCompletion && GameplayProgress.ActionComplete(tag, action));
        }
    }

    private void DrawVisited(object map, Camera camera)
    {
        var locations = GameAccess.Get<IDictionary>(map, "locationDict");
        if (locations == null)
            return;
        foreach (DictionaryEntry entry in locations)
        {
            var target = GameplayExplore.ItemObject(entry.Value);
            if (target != null && target.activeInHierarchy && entry.Key is string &&
                GameplayExplore.WasVisited((string)entry.Key))
                AtWorld(camera, target.transform.position, "Visited");
        }
    }

    private void DrawGlyphs(object puzzle, Camera camera)
    {
        if (GameAccess.Get<bool>(puzzle, "finished"))
            return;
        DrawGlyphRow(GameAccess.Get<IList>(puzzle, "glyphTargets"), camera);
        DrawGlyphRow(GameAccess.Get<IList>(puzzle, "glyphChoices"), camera);
    }

    private void DrawGlyphRow(IList row, Camera camera)
    {
        if (row == null)
            return;
        for (int i = 0; i < row.Count; ++i)
        {
            var sprite = row[i] as Behaviour;
            if (!GameAccess.IsActive(sprite) || GameAccess.Get<Color>(sprite, "tintColor").a <= .01f)
                continue;
            int cell = GameAccess.Get<int>(sprite, "currentCellKey");
            AtWorld(camera, sprite.transform.position, (i + 1) + " · Glyph " + (cell + 1));
        }
    }

    private void DrawExits(object puzzle)
    {
        var labels = new List<string>();
        foreach (string direction in new[] { "north", "east", "south", "west" })
        {
            object value = GameAccess.Get<object>(puzzle, direction + "State");
            string state = value == null ? "" : value.ToString();
            if (state == "open" || state == "closed" || state == "locked" || state == "destroyed")
                labels.Add(char.ToUpperInvariant(direction[0]) + direction.Substring(1) + " " + state);
        }
        if (labels.Count > 0)
            Banner(string.Join("  ·  ", labels.ToArray()), Screen.height * .66f, text);
    }

    private void AtWorld(Camera camera, Vector3 world, string label)
    {
        Vector3 viewport = camera.WorldToScreenPoint(world);
        if (viewport.z < 0 || viewport.x < 0 || viewport.x > Screen.width ||
            viewport.y < 0 || viewport.y > Screen.height)
            return;
        const float padding = 12;
        var content = new GUIContent(label);
        float width = Mathf.Min(text.CalcSize(content).x + padding, Screen.width * .28f);
        float height = text.CalcHeight(content, width - padding) + 4;
        float x = Mathf.Clamp(viewport.x - width * .5f, 3, Screen.width - width - 3);
        float y = Mathf.Clamp(Screen.height - viewport.y - height - 8, 3, Screen.height - height - 3);
        Paint(new Rect(x, y, width, height), content, text);
    }

    private void Banner(string value, float y, GUIStyle style)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var content = new GUIContent(value);
        float width = Mathf.Min(style.CalcSize(content).x + 24, Screen.width * .8f);
        float height = style.CalcHeight(content, width - 12) + 6;
        Paint(new Rect((Screen.width - width) * .5f, y, width, height), content, style);
    }

    private static void Paint(Rect rect, GUIContent content, GUIStyle style)
    {
        Backdrop(rect);
        GUI.Label(new Rect(rect.x + 6, rect.y + 2, rect.width - 12, rect.height - 4), content, style);
    }

    private static void Backdrop(Rect rect)
    {
        Color original = GUI.color;
        GUI.color = new Color(.035f, .025f, .07f, .98f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = original;
    }
}

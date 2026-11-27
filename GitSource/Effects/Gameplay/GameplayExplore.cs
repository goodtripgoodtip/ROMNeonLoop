using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class GameplayExplore
{
    private static readonly HashSet<string> Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string inventoryId;
    private static object inventory;
    private static bool inventoryVisible;
    private static bool visitedLoaded;
    private static float nextGoal;
    private static MethodInfo toBool;
    internal static string Objective { get; private set; }

    internal static IEnumerable Items(object menu)
    {
        return GameAccess.Get<IEnumerable>(menu, "menuItems") ?? new object[0];
    }

    internal static GameObject ItemObject(object item)
    {
        return GameAccess.Get<GameObject>(item, "item");
    }

    internal static Component Tag(GameObject target)
    {
        return target == null ? null : target.GetComponent("SetTag");
    }

    internal static void GetActions(object menu, Component tag, List<string> actions)
    {
        actions.Clear();
        var scripts = GameAccess.Get<IDictionary>(tag, "ActionList");
        if (scripts == null)
            return;
        if (!GameAccess.Get<bool>(menu, "useInteractionMenu"))
        {
            string direct = GameAccess.Get<string>(menu, "currentTag");
            if (!string.IsNullOrEmpty(direct) && scripts.Contains(direct))
                actions.Add(direct);
            return;
        }
        if (scripts.Contains("scroll"))
        {
            actions.Add("scroll");
            return;
        }
        var verbs = GameAccess.Get<string[]>(menu, "interactions");
        if (verbs == null)
            return;
        foreach (string verb in verbs)
        {
            if (string.IsNullOrEmpty(verb))
                continue;
            if (scripts.Contains(verb))
                actions.Add(verb);
            else if (verb == "item")
                foreach (string key in scripts.Keys)
                    if (HasInventoryAction(key))
                    {
                        actions.Add(verb);
                        break;
                    }
        }
    }

    internal static bool HasInventoryAction(string action)
    {
        return Equals(GameAccess.Call(GameAccess.Type("InventoryMenu"), "HasInventoryForAction", action), true);
    }

    public static void SelectClickedItem(object menu, GameObject target)
    {
        if (!GameplayHooks.Enabled(GameplayHooks.Option.SingleClick))
            return;
        try
        {
            if (target == null || !target.activeInHierarchy || OverlayInput.IsMenuOpen() ||
                !GameAccess.Playing(GameAccess.Scene) || !GameAccess.Get<bool>(menu, "Enabled") ||
                !GameAccess.Get<bool>(menu, "takeInput"))
                return;
            object current = GameAccess.Get<object>(menu, "selected");
            var previous = ItemObject(current);
            if (previous == target || previous != null && previous.transform.position.z < target.transform.position.z &&
                Equals(GameAccess.Call(previous.GetComponent("CursorCollider"), "HasMouseOver"), true))
                return;
            foreach (object item in Items(menu))
            {
                if (ItemObject(item) != target)
                    continue;
                GameAccess.Call(menu, "set_Selected", item);
                GameAccess.Call(menu, "HighlightSelection", true);
                return;
            }
        }
        catch (Exception ex) { GameplayHud.Report(ex); }
    }

    internal static string Identity(Component tag)
    {
        var root = GameAccess.Get<GameObject>(GameAccess.Scene, "_setRoot");
        string identity = tag.name + "#" + tag.transform.GetSiblingIndex();
        for (Transform parent = tag.transform.parent; parent != null && (root == null || parent != root.transform);
            parent = parent.parent)
            identity = parent.name + "#" + parent.GetSiblingIndex() + "/" + identity;
        return (root != null ? root.name : "") + "/" + identity;
    }

    public static void CompleteSet(string set)
    {
        if (!GameplayHooks.Enabled(GameplayHooks.Option.Visited) || string.IsNullOrWhiteSpace(set))
            return;
        try
        {
            LoadVisited();
            string file = VisitedFile();
            if (Visited.Add(set) && file != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.AppendAllText(file, set + Environment.NewLine);
            }
        }
        catch (Exception ex) { GameplayHud.Report(ex); }
    }

    private static string VisitedFile()
    {
        string data = Environment.GetEnvironmentVariable("ROM_GAMEPLAY_DATA");
        return string.IsNullOrEmpty(data) ? null : Path.Combine(data, "Visited.txt");
    }

    private static void LoadVisited()
    {
        if (visitedLoaded)
            return;
        visitedLoaded = true;
        string path = VisitedFile();
        if (path != null && File.Exists(path))
            foreach (string set in File.ReadLines(path))
                Visited.Add(set);
    }

    internal static bool WasVisited(string set)
    {
        LoadVisited();
        return Visited.Contains(set);
    }

    internal static void Tick()
    {
        UpdateInventory();
        if (!GameplayHooks.Enabled(GameplayHooks.Option.Objective) || !GameAccess.Playing(GameAccess.Scene))
        {
            Objective = null;
            return;
        }
        if (Time.unscaledTime >= nextGoal)
        {
            nextGoal = Time.unscaledTime + .9f;
            RefreshGoals();
        }
    }

    private static void UpdateInventory()
    {
        if (!GameplayHooks.Enabled(GameplayHooks.Option.Inventory))
        {
            inventoryVisible = false;
            return;
        }
        object active = GameAccess.Get<object>(GameAccess.Type("InventoryMenu"), "singleton");
        bool visible = GameAccess.Get<bool>(active, "isVisible") && GameAccess.Get<bool>(active, "takeInput");
        if (!visible)
        {
            inventoryVisible = false;
            return;
        }
        var entries = GameAccess.Get<IDictionary>(active, "menuDictionary");
        if (entries == null)
            return;
        if (!inventoryVisible || !ReferenceEquals(inventory, active))
        {
            inventoryVisible = true;
            inventory = active;
            foreach (DictionaryEntry entry in entries)
                if (inventoryId != null && inventoryId == GameAccess.Get<string>(entry.Value, "id") &&
                    ItemObject(entry.Key) != null && ItemObject(entry.Key).activeInHierarchy)
                {
                    GameAccess.Call(active, "set_Selected", entry.Key);
                    GameAccess.Call(active, "HighlightSelection", true);
                    break;
                }
        }
        object selection = GameAccess.Get<object>(active, "selected");
        if (selection != null && entries.Contains(selection))
            inventoryId = GameAccess.Get<string>(entries[selection], "id") ?? inventoryId;
    }

    private static void RefreshGoals()
    {
        Objective = null;
        Type markerType = GameAccess.Type("MapMarker");
        Type scriptValue = GameAccess.Type("ScriptValue");
        if (markerType == null || scriptValue == null)
            return;
        if (toBool == null)
            foreach (MethodInfo method in scriptValue.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (method.Name == "op_Implicit" && method.ReturnType == typeof(bool))
                {
                    toBool = method;
                    break;
                }
        if (toBool == null)
            return;
        var names = new List<string>();
        var memory = GameAccess.Type("Memory");
        foreach (var marker in Resources.FindObjectsOfTypeAll(markerType))
        {
            var set = GameAccess.Get<GameObject>(marker, "set");
            string label = GameplayHooks.Plain(GameAccess.Get<string>(marker, "displayName"));
            if (set == null || label.Length == 0 || names.Contains(label))
                continue;
            object state = GameAccess.Call(memory, "Get", "$" + set.name + "_goal", false);
            if (state != null && Equals(toBool.Invoke(null, new[] { state }), true))
                names.Add(label);
        }
        if (names.Count > 0)
            Objective = string.Join("  /  ", names.ToArray());
    }
}

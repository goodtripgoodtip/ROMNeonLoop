using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

internal static class GameAccess
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> Members =
        new Dictionary<Type, Dictionary<string, MemberInfo>>();
    private static Assembly game;

    internal static Type Type(string name)
    {
        if (game == null)
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "Assembly-CSharp")
                {
                    game = assembly;
                    break;
                }
        return game == null ? null : game.GetType(name);
    }

    internal static bool Alive(object value)
    {
        return value != null && (!(value is UnityEngine.Object) || (UnityEngine.Object)value != null);
    }

    private static MemberInfo Member(Type type, string key, Func<Type, MemberInfo> find)
    {
        Dictionary<string, MemberInfo> cache;
        if (!Members.TryGetValue(type, out cache))
            Members[type] = cache = new Dictionary<string, MemberInfo>();
        MemberInfo result;
        if (!cache.TryGetValue(key, out result))
        {
            for (Type current = type; current != null && result == null; current = current.BaseType)
                result = find(current);
            cache[key] = result;
        }
        return result;
    }

    internal static T Get<T>(object target, string name)
    {
        if (!Alive(target))
            return default(T);
        Type type = target as Type ?? target.GetType();
        var member = Member(type, name, t => (MemberInfo)t.GetField(name, Flags) ?? t.GetProperty(name, Flags));
        object instance = target is Type ? null : target;
        object value = member is FieldInfo ? ((FieldInfo)member).GetValue(instance) :
            member is PropertyInfo ? ((PropertyInfo)member).GetValue(instance, null) : null;
        return value is T ? (T)value : default(T);
    }

    internal static void Set(object target, string name, object value)
    {
        if (!Alive(target))
            return;
        var field = Member(target.GetType(), name, t => t.GetField(name, Flags)) as FieldInfo;
        if (field != null)
            field.SetValue(target, value);
    }

    internal static object Call(object target, string name, params object[] args)
    {
        if (!Alive(target))
            return null;
        Type type = target as Type ?? target.GetType();
        string key = name + "/" + args.Length;
        foreach (object argument in args)
            key += "/" + (argument == null ? "" : argument.GetType().FullName);
        var method = Member(type, key, t => FindMethod(t, name, args)) as MethodInfo;
        return method == null ? null : method.Invoke(target is Type ? null : target, args);
    }

    private static MethodInfo FindMethod(Type type, string name, object[] arguments)
    {
        foreach (MethodInfo method in type.GetMethods(Flags))
        {
            if (method.Name != name || method.ContainsGenericParameters)
                continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != arguments.Length)
                continue;
            bool compatible = true;
            for (int i = 0; i < parameters.Length && compatible; i++)
                compatible = arguments[i] == null ? !parameters[i].ParameterType.IsValueType :
                    parameters[i].ParameterType.IsInstanceOfType(arguments[i]);
            if (compatible)
                return method;
        }
        return null;
    }

    internal static object Scene { get { return Get<object>(Type("SceneManager"), "_instance"); } }
    internal static object ActiveMenu { get { return Call(Type("MenuManager"), "ActiveMenu"); } }

    internal static bool IsActive(object value)
    {
        var component = value as Behaviour;
        return component != null && component.isActiveAndEnabled;
    }

    internal static bool Playing(object scene)
    {
        return Alive(scene) && !Get<bool>(scene, "inMainMenu") && !Get<bool>(scene, "Paused") &&
            !Get<bool>(scene, "PlayingCutscene") && !Get<bool>(scene, "inOptions") &&
            !Get<bool>(scene, "playingTransition");
    }
}

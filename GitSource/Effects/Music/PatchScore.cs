using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchScore
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: PatchScore <stock game DLL> <replacement helper DLL> <output DLL>");
            return 2;
        }

        try
        {
            Patch(args[0], args[1], args[2]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    internal static void Patch(string stock, string score, string output)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.RemoveSearchDirectory(".");
        resolver.RemoveSearchDirectory("bin");
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(stock)));
        var reader = new ReaderParameters { AssemblyResolver = resolver };
        using (var game = ModuleDefinition.ReadModule(stock, reader))
        using (var helper = ModuleDefinition.ReadModule(score, reader))
        {
            var audio = game.GetType("AudioManager");
            var load = audio.Methods.Single(method => method.Name == "LoadClip" &&
                method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.String" &&
                method.ReturnType.FullName == "UnityEngine.AudioClip");
            if (!load.Body.Instructions.Any(instr => instr.Operand is MethodReference &&
                ((MethodReference)instr.Operand).DeclaringType.FullName == "UnityEngine.Resources"))
                throw new InvalidDataException("The game's clip loading code did not match the expected method.");

            var resolve = Import(game, helper, "ReplacementScore", "Resolve");
            var il = load.Body.GetILProcessor();
            var original = load.Body.Instructions[0];
            var proceed = il.Create(OpCodes.Pop);
            il.InsertBefore(original, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(original, il.Create(OpCodes.Call, resolve));
            il.InsertBefore(original, il.Create(OpCodes.Dup));
            il.InsertBefore(original, il.Create(OpCodes.Brfalse_S, proceed));
            il.InsertBefore(original, il.Create(OpCodes.Ret));
            il.InsertBefore(original, proceed);

            var video = game.GetType("Midboss.ROM.VideoPlayerWrapper").Methods.Single(m => m.Name == "PlayVideo");
            var assign = video.Body.Instructions.Single(instr => instr.Operand is MethodReference &&
                ((MethodReference)instr.Operand).FullName == "System.Void UnityEngine.Video.VideoPlayer::set_url(System.String)");
            var resolveVideo = Import(game, helper, "ReplacementScore", "ResolveVideo");
            video.Body.GetILProcessor().InsertBefore(assign, Instruction.Create(OpCodes.Call, resolveVideo));

            var play = Import(game, helper, "ReplacementScore", "PlaySource");
            ReplacePlay(audio.Methods.Single(m => m.Name == "playClipOnChannel"), play);
            var duck = audio.NestedTypes.Single(t => t.Name.StartsWith("<DuckVolumeRoutine>", StringComparison.Ordinal));
            ReplacePlay(duck.Methods.Single(m => m.Name == "MoveNext"), play);
            GuardMusic(audio, Import(game, helper, "StoryScore", "Route"));
            PatchDialogue(game.GetType("DialogManager"), Import(game, helper, "StoryScore", "Transform"));

            var input = game.GetType("InputManager");
            var tick = input.Methods.Single(m => m.Name == "LateUpdate");
            tick.Body.GetILProcessor().InsertBefore(tick.Body.Instructions[0],
                Instruction.Create(OpCodes.Call, Import(game, helper, "ReplacementScore", "Tick")));
            var menuOpen = Import(game, helper, "OverlayInput", "IsMenuOpen");
            GuardInput(game.GetType("Rewired.Integration.UnityUI.RewiredStandaloneInputModule"), "Process", menuOpen);
            GuardInput(input, "GetRewiredInput", menuOpen);
            new GameplayPatch(game, helper).Apply();
            game.Write(output);
        }
    }

    private static MethodReference Import(ModuleDefinition game, ModuleDefinition helper, string type, string name)
    {
        return game.ImportReference(helper.GetType(type).Methods.Single(m => m.Name == name));
    }

    private static void ReplacePlay(MethodDefinition method, MethodReference play)
    {
        var call = method.Body.Instructions.Single(i => i.Operand is MethodReference &&
            ((MethodReference)i.Operand).FullName == "System.Void UnityEngine.AudioSource::Play()");
        call.OpCode = OpCodes.Call;
        call.Operand = play;
    }

    private static void GuardMusic(TypeDefinition audio, MethodReference route)
    {
        var method = audio.Methods.Single(m => m.Name == "playMusic");
        var name = method.Parameters[0];
        if (name.ParameterType.FullName != "System.String")
            throw new InvalidDataException("Unexpected music cue argument.");
        var il = method.Body.GetILProcessor();
        var temporary = new VariableDefinition(audio.Module.TypeSystem.Boolean);
        method.Body.Variables.Add(temporary);
        var original = method.Body.Instructions.Single(i => i.Operand == method.Parameters.Single(p => p.Name == "fromMenu"));
        var volume = method.Body.Instructions.Single(i => i.Operand is MethodReference &&
            ((MethodReference)i.Operand).Name == "get_MusicVolume").Previous.Previous;
        var first = il.Create(OpCodes.Ldarga_S, name);
        foreach (var incoming in method.Body.Instructions.Where(i => i.Operand == original).ToArray())
            incoming.Operand = first;
        var skipMemory = il.Create(OpCodes.Ldloc, temporary);
        il.InsertBefore(original, first);
        il.InsertBefore(original, il.Create(OpCodes.Ldloca, temporary));
        il.InsertBefore(original, il.Create(OpCodes.Call, route));
        il.InsertBefore(original, il.Create(OpCodes.Brtrue_S, skipMemory));
        il.InsertBefore(original, il.Create(OpCodes.Ret));
        il.InsertBefore(original, skipMemory);
        il.InsertBefore(original, il.Create(OpCodes.Brtrue, volume));

        var register = method.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "music_"));
        var schedule = (Instruction)register.Previous.Operand;
        il.InsertBefore(register, il.Create(OpCodes.Ldloc, temporary));
        il.InsertBefore(register, il.Create(OpCodes.Brtrue_S, schedule));
    }

    private static void PatchDialogue(TypeDefinition dialog, MethodReference transform)
    {
        var method = dialog.Methods.Single(m => m.Name == "LoadDialog");
        var parse = method.Body.Instructions.Single(i => i.Operand is MethodReference &&
            ((MethodReference)i.Operand).FullName == "Tale Tale::ParseTaleFile(System.String)");
        var path = method.Body.Variables[0];
        if (path.VariableType.FullName != "System.String")
            throw new InvalidDataException("Unexpected dialogue resource path.");
        var il = method.Body.GetILProcessor();
        il.InsertBefore(parse, il.Create(OpCodes.Ldloc, path));
        il.InsertBefore(parse, il.Create(OpCodes.Call, transform));
    }

    private static void GuardInput(TypeDefinition type, string name, MethodReference menuOpen)
    {
        var method = type.Methods.Single(m => m.Name == name && m.ReturnType.FullName == "System.Void");
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, menuOpen));
        il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse_S, first));
        il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    }
}

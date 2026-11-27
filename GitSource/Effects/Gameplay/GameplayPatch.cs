using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal sealed class GameplayPatch
{
    private readonly ModuleDefinition game;
    private readonly ModuleDefinition helper;

    internal GameplayPatch(ModuleDefinition game, ModuleDefinition helper)
    {
        this.game = game;
        this.helper = helper;
    }

    internal void Apply()
    {
        var tick = Method("InputManager", "LateUpdate");
        Before(tick, tick.Body.Instructions[0], false,
            Instruction.Create(OpCodes.Call, Hook("GameplayHooks", "Tick")));
        var title = Iterator("SceneManager", "FadeToMainMenuRoutine");
        var titleFlag = title.DeclaringType.Fields.Single(field => field.Name == "titlescreen");
        var titleFadeComplete = title.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret).Previous;
        Before(title, titleFadeComplete, true, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, titleFlag), Instruction.Create(OpCodes.Call,
                Hook("OverlayInput", "TitleScreenShown")));
        Dialogue();
        Font();
        Saves();
        Exploration();
        Entry("MouseFollow", "Update", "GameplayHooks", "KeepPointer");
        var taxi = Method("ChaseWidget", "FailText");
        var prompt = taxi.Body.Instructions.Single(i => i.Operand is FieldReference &&
            ((FieldReference)i.Operand).Name == "onFinishCallback" && i.OpCode == OpCodes.Stfld);
        Before(taxi, prompt.Next, false, Instruction.Create(OpCodes.Call, Hook("GameplayHooks", "TaxiFailed")));
    }

    private void Font()
    {
        var refresh = Method("RagePixelText", "refreshMesh");
        var end = refresh.Body.Instructions.Single(i => i.OpCode == OpCodes.Ret);
        Before(refresh, end, true, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, Hook("GameplayFont", "Refresh")));
        var generate = Method("RagePixelText", "GenerateMesh");
        var colors = generate.Body.Instructions.Single(i => Calls(i, "UnityEngine.Mesh", "set_colors"));
        Before(generate, colors.Next, false, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, Hook("GameplayFont", "StockColorsWritten")));
    }

    private void Dialogue()
    {
        var show = Method("DialogManager", "showCurrentDialog");
        var set = show.Body.Instructions.Single(i => Calls(i, "Typewriter", "SetText"));
        Before(show, set, true, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Call, Hook("GameplayHooks", "BeginLine")));
        Entry("DialogManager", "finishedTyping", "GameplayHooks", "CompleteLine");

        var choice = Method("DialogManager", "dialogMenuOptionChosen");
        var choiceKey = new VariableDefinition(game.TypeSystem.String);
        choice.Body.Variables.Add(choiceKey);
        Before(choice, choice.Body.Instructions[0], false, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "BeginChoice")),
            Instruction.Create(OpCodes.Stloc, choiceKey));
        foreach (var end in choice.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            Before(choice, end, true, Instruction.Create(OpCodes.Ldloc, choiceKey),
                Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "EndChoice")));

        var finish = Method("DialogManager", "finishedDialog");
        foreach (var end in finish.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            Before(finish, end, true, Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "DialogueEnded")));

        var input = Method("Typewriter", "HandleInput");
        int skip = (int)game.GetType("InputFlag").Fields.Single(f => f.Name == "SKIP").Constant;
        var gates = input.Body.Instructions.Where(i => Calls(i, "InputEvent", "HasFlag") &&
            Equals(i.Previous.Operand, skip)).ToArray();
        if (gates.Length != 2)
            throw new InvalidOperationException("The stock dialogue skip gates changed.");
        foreach (Instruction gate in gates)
            Before(input, gate.Next, false, Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Call, Hook("GameplayHooks", "AllowSkip")));

        var type = Iterator("Typewriter", "Type");
        var speed = type.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
            ((FieldReference)i.Operand).Name.StartsWith("<thisTPC>", StringComparison.Ordinal));
        Before(type, speed, true, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, Hook("GameplayHooks", "CharacterTime")));
    }

    private void Saves()
    {
        var start = Method("Memory", "SaveGame");
        Before(start, start.Body.Instructions[0], false, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, Hook("GameplaySaves", "StartSlot")));
        var save = Iterator("Memory", "SaveGame");
        var copy = save.Body.Instructions.Single(i => Calls(i, "System.IO.File", "Copy"));
        copy.Operand = Hook("GameplaySaves", "CopySlot");

        var metadata = Method("Memory", "SaveDisplayInfoDesktop");
        var end = metadata.Body.Instructions.Single(i => i.OpCode == OpCodes.Ret);
        Before(metadata, end, true, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, Hook("GameplaySaves", "FinishSlot")),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "Save")));

        var load = Iterator("Memory", "LoadGame");
        var refresh = load.Body.Instructions.Single(i => Calls(i, "Memory", "RefreshFromStorage"));
        var configOnly = load.DeclaringType.Fields.Single(field => field.Name == "configOnly");
        var savePath = load.Body.Variables[2];
        if (savePath.VariableType.FullName != "System.String" ||
            !load.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldloc_2 && Calls(i.Next.Next, "System.IO.File", "Copy")))
            throw new InvalidOperationException("The stock save path used by LoadGame changed.");
        Before(load, refresh.Next, false, Instruction.Create(OpCodes.Ldloc, savePath),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, configOnly),
            Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "Load")));

        var newGame = Method("SceneManager", "NewGame");
        Before(newGame, newGame.Body.Instructions[0], false, Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "Reset")));
    }

    private void Exploration()
    {
        Click("InventoryMenu", "<BuildSetMenu>b__27_2");
        Click("InteractionMenu", "<Launch>b__13_2");

        var interact = Method("SetTag", "Trigger");
        var action = new VariableDefinition(game.TypeSystem.String);
        interact.Body.Variables.Add(action);
        Before(interact, interact.Body.Instructions[0], false, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "BeginAction")),
            Instruction.Create(OpCodes.Stloc, action));
        foreach (var end in interact.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            Before(interact, end, true, Instruction.Create(OpCodes.Ldloc, action), Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, Hook("GameplayProgress", "EndAction")));

        var set = Iterator("SceneManager", "LoadSetRoutine");
        var finished = set.Body.Instructions.First(i => i.OpCode == OpCodes.Ldfld &&
            ((FieldReference)i.Operand).Name == "onComplete").Previous;
        var location = set.DeclaringType.Fields.Single(f => f.Name == "set");
        Before(set, finished, true, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, location),
            Instruction.Create(OpCodes.Call, Hook("GameplayExplore", "CompleteSet")));
    }

    private void Click(string type, string name)
    {
        var click = Method(type, name);
        Before(click, click.Body.Instructions[0], false, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call,
                Hook("GameplayExplore", "SelectClickedItem")));
    }

    private void Entry(string type, string name, string hookType, string hook)
    {
        var method = Method(type, name);
        Before(method, method.Body.Instructions[0], false, Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, Hook(hookType, hook)));
    }

    private MethodDefinition Method(string type, string name)
    {
        return game.GetType(type).Methods.Single(m => m.Name == name);
    }

    private MethodDefinition Iterator(string type, string name)
    {
        return game.GetType(type).NestedTypes.Single(t => t.Name.StartsWith("<" + name + ">", StringComparison.Ordinal))
            .Methods.Single(m => m.Name == "MoveNext");
    }

    private MethodReference Hook(string type, string name)
    {
        return game.ImportReference(helper.GetType(type).Methods.Single(m => m.Name == name));
    }

    private static bool Calls(Instruction instruction, string type, string name)
    {
        var target = instruction == null ? null : instruction.Operand as MethodReference;
        return target != null && target.Name == name && target.DeclaringType.FullName == type;
    }

    private static void Before(MethodDefinition method, Instruction anchor, bool redirect, params Instruction[] added)
    {
        var il = method.Body.GetILProcessor();
        foreach (Instruction old in method.Body.Instructions)
        {
            Widen(old);
            if (!redirect)
                continue;
            if (old.Operand == anchor)
                old.Operand = added[0];
            var targets = old.Operand as Instruction[];
            if (targets != null)
                for (int i = 0; i < targets.Length; i++)
                    if (targets[i] == anchor)
                        targets[i] = added[0];
        }
        foreach (Instruction addition in added)
            il.InsertBefore(anchor, addition);
    }

    private static void Widen(Instruction instruction)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
            case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
            case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
            case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
            case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
            case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
            case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
            case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
            case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
            case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
            case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
            case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
            case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
            case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
        }
    }
}

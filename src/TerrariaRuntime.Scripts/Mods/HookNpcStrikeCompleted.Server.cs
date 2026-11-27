using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using TerrariaRuntime.PatchEngine;

/// <summary>Observes a completed native NPC strike while preserving the original return value and finally block.</summary>
[Patch(PatchStage.PreMerge, "Observing completed native NPC strikes", PatchPriority.Late, new[] { "NpcStrikeArgs" })]
[MonoMod.MonoModIgnore]
void HookNpcStrikeCompleted(AssemblyPatcher modder)
{
    var strike = modder.Module.GetType("Terraria.NPC").Methods.Single(method =>
        method.Name == "StrikeNPC" && method.ReturnType.FullName == "System.Int32" && method.HasBody);
    strike.Body.SimplifyMacros();
    var inner = strike.Body.Instructions.Single(instruction => (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
        instruction.Operand is MethodReference method && method.Name == "StrikeNPC_Inner" &&
        method.DeclaringType.FullName == "Terraria.NPC");
    var result = inner.Next;
    var completedReturn = strike.Body.Instructions.Last(instruction => instruction.OpCode == OpCodes.Ret);
    if (result.OpCode != OpCodes.Stloc || completedReturn.Previous.OpCode != OpCodes.Ldloc ||
        !ReferenceEquals(result.Operand, completedReturn.Previous.Operand) ||
        strike.Body.ExceptionHandlers.Single(handler => handler.HandlerType == ExceptionHandlerType.Finally).HandlerEnd != completedReturn.Previous ||
        strike.Body.Instructions.Count(instruction => instruction.OpCode == OpCodes.Ret) != 4)
        throw new InvalidOperationException("Unexpected Terraria 1.4.6.1 StrikeNPC completion shape.");

    // The evaluation stack holds the returned native damage. The helper returns exactly
    // that integer. Early zero returns and exceptions never pass this post-finally boundary.
    // PatchNpcStrikeArgs adds the optional native caller Entity before this patch.
    // Keep that value as observational context, not proof of an authenticated attacker.
    var source = strike.Parameters.Single(parameter => parameter.Name == "entity" && parameter.ParameterType.FullName == "Terraria.Entity");
    var callback = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.NPC.NotifyStrikeCompletedWithSource(default, default, default, default, default));
    var il = strike.Body.GetILProcessor();
    completedReturn.OpCode = OpCodes.Nop;
    var anchor = completedReturn;
    foreach (var instruction in new[]
    {
        Instruction.Create(OpCodes.Ldarg_0),
        Instruction.Create(OpCodes.Ldarg, strike.Parameters.Single(parameter => parameter.Name == "Damage")),
        Instruction.Create(OpCodes.Ldarg, strike.Parameters.Single(parameter => parameter.Name == "crit")),
        Instruction.Create(OpCodes.Ldarg, source),
        Instruction.Create(OpCodes.Call, callback),
        Instruction.Create(OpCodes.Ret)
    })
    {
        il.InsertAfter(anchor, instruction);
        anchor = instruction;
    }
    strike.Body.OptimizeMacros();
}

namespace TerrariaRuntime
{
    public static partial class Hooks
    {
        public static partial class NPC
        {
            /// <summary>After native StrikeNPC's successful completion. Damage is the native return value, not a life-delta claim.</summary>
            public static event Action<Terraria.NPC, int, int, bool>? StrikeCompleted;

            /// <summary>After native StrikeNPC completion, including the patched caller context. Null means no source was supplied; a source does not certify attacker identity.</summary>
            public static event Action<Terraria.NPC, int, int, bool, Terraria.Entity?>? StrikeCompletedWithSource;

            public static int NotifyStrikeCompletedWithSource(int damage, Terraria.NPC npc, int requestedDamage, bool critical, Terraria.Entity? source)
            {
                if (damage <= 0) return damage;
                var observers = StrikeCompletedWithSource;
                if (observers is not null)
                {
                    foreach (var observer in Delegate.EnumerateInvocationList(observers))
                    {
                        try { observer(npc, damage, requestedDamage, critical, source); }
                        catch (Exception) { /* Keep notification faults isolated from native damage and other observers. */ }
                    }
                }
                return NotifyStrikeCompleted(damage, npc, requestedDamage, critical);
            }

            public static int NotifyStrikeCompleted(int damage, Terraria.NPC npc, int requestedDamage, bool critical)
            {
                var observers = StrikeCompleted;
                if (damage <= 0 || observers is null) return damage;
                foreach (var observer in Delegate.EnumerateInvocationList(observers))
                {
                    try { observer(npc, damage, requestedDamage, critical); }
                    catch (Exception) { /* Observation cannot change the result or unwind a completed native strike. */ }
                }
                return damage;
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using TerrariaRuntime.PatchEngine;

/// <summary>
/// @doc Avoids an extra copy of the serialized local world when the native writer accepts an explicit byte count.
/// </summary>
[Patch(PatchStage.PreMerge, "Avoiding local world save buffer copy")]
[MonoMod.MonoModIgnore]
void PatchWorldSaveBuffer(AssemblyPatcher modder)
{
    var save = modder.Module.GetType("Terraria.IO.WorldFile").Methods.Single(method =>
        method.Name == "InternalSaveWorld" && method.IsStatic && method.Parameters.Count == 3);
    save.Body.SimplifyMacros();

    var cloud = save.Parameters.Single(parameter => parameter.Name == "useCloudSaving" &&
        parameter.ParameterType.FullName == "System.Boolean");
    var copy = save.Body.Instructions.Single(instruction => Calls(instruction, "System.IO.MemoryStream", "ToArray"));
    var storeBuffer = copy.Next;
    var loadLength = storeBuffer.Next;
    var arrayLength = loadLength.Next;
    var toInt = arrayLength.Next;
    var storeCount = toInt.Next;

    if (copy.Previous.OpCode != OpCodes.Ldloc || copy.Previous.Operand is not VariableDefinition stream ||
        stream.VariableType.FullName != "System.IO.MemoryStream" ||
        storeBuffer.OpCode != OpCodes.Stloc || storeBuffer.Operand is not VariableDefinition buffer ||
        buffer.VariableType.FullName != "System.Byte[]" ||
        loadLength.OpCode != OpCodes.Ldloc || !ReferenceEquals(loadLength.Operand, buffer) ||
        arrayLength.OpCode != OpCodes.Ldlen || toInt.OpCode != OpCodes.Conv_I4 ||
        storeCount.OpCode != OpCodes.Stloc || storeCount.Operand is not VariableDefinition count ||
        count.VariableType.FullName != "System.Int32")
        throw new InvalidOperationException("Unexpected native world save buffer extraction.");

    // Require the exact native write/re-read shape: before the buffer local is overwritten by the
    // readback, its only consumer is FileUtilities.Write(path, bytes, count, cloud). Neither
    // rollback nor validation may see the unused capacity of the serialization buffer.
    var write = save.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.Utilities.FileUtilities", "Write") &&
        instruction.Operand is MethodReference method && !method.HasThis && method.Parameters.Count == 4 &&
        method.Parameters[0].ParameterType.FullName == "System.String" &&
        method.Parameters[1].ParameterType.FullName == "System.Byte[]" &&
        method.Parameters[2].ParameterType.FullName == "System.Int32" &&
        method.Parameters[3].ParameterType.FullName == "System.Boolean");
    var readback = save.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.Utilities.FileUtilities", "ReadAllBytes") &&
        instruction.Next.OpCode == OpCodes.Stloc && ReferenceEquals(instruction.Next.Operand, buffer));
    var bytesArg = write.Previous.Previous.Previous;
    var bufferFlow = save.Body.Instructions.SkipWhile(instruction => instruction != storeBuffer)
        .Skip(1).TakeWhile(instruction => instruction != readback).ToArray();
    var consumers = bufferFlow.Where(instruction => ReferenceEquals(instruction.Operand, buffer)).ToArray();
    var changedSequence = new[] { copy, storeBuffer, loadLength, arrayLength, toInt, storeCount };
    bool hasEntryIntoSequence = save.Body.Instructions.Any(instruction => instruction.Operand switch
    {
        Instruction target => changedSequence.Contains(target),
        Instruction[] targets => targets.Any(changedSequence.Contains),
        _ => false
    });
    if (bytesArg.OpCode != OpCodes.Ldloc || !ReferenceEquals(bytesArg.Operand, buffer) ||
        bytesArg.Next.OpCode != OpCodes.Ldloc || !ReferenceEquals(bytesArg.Next.Operand, count) ||
        write.Previous.OpCode != OpCodes.Ldarg || !ReferenceEquals(write.Previous.Operand, cloud) ||
        consumers.Length != 2 || !consumers.Contains(loadLength) || !consumers.Contains(bytesArg) ||
        hasEntryIntoSequence || bufferFlow.Any(instruction => instruction != storeCount &&
            ReferenceEquals(instruction.Operand, count) && instruction.OpCode != OpCodes.Ldloc) ||
        !save.Body.Instructions.Any(instruction => Calls(instruction, "Terraria.IO.WorldFile", "ValidateWorld")) ||
        !save.Body.Instructions.Any(instruction => Calls(instruction, "Terraria.Utilities.FileUtilities", "WriteAllBytes")))
        throw new InvalidOperationException("Unexpected native world save write, rollback or validation flow.");

    int ignoredCount = 0;
    var getBuffer = modder.GetMethodDefinition(() => TerrariaRuntime.WorldSaveBuffer.Extract(default, default, out ignoredCount));
    var il = save.Body.GetILProcessor();
    il.InsertBefore(copy, Instruction.Create(OpCodes.Ldarg, cloud));
    il.InsertBefore(copy, Instruction.Create(OpCodes.Ldloca, count));
    copy.OpCode = OpCodes.Call;
    copy.Operand = getBuffer;
    // Keep instruction identities for exception-handler/branch boundaries.
    foreach (var instruction in new[] { loadLength, arrayLength, toInt, storeCount })
    {
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
    }
    save.Body.OptimizeMacros();

    static bool Calls(Instruction instruction, string type, string name) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
        instruction.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name;
}

namespace TerrariaRuntime
{
    internal static class WorldSaveBuffer
    {
        internal static byte[] Extract(MemoryStream stream, bool useCloudSaving, out int count)
        {
            // BinaryWriter has already disposed the stream. TryGetBuffer remains valid after
            // disposal; Count is the written length rather than the backing array's capacity.
            // The native cloud path retains its original exact-array behavior.
            if (!useCloudSaving && stream.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Array is { } bytes)
            {
                count = segment.Count;
                return bytes;
            }
            var copy = stream.ToArray();
            count = copy.Length;
            return copy;
        }
    }
}

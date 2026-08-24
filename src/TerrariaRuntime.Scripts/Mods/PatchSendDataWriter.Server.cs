/*
Copyright (C) 2020 DeathCradle

This file is part of Open Terraria API v3 (OTAPI)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using TerrariaRuntime.PatchEngine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using System.Linq;
using Mono.Cecil.Rocks;
using System.IO;
using System.IO.Compression;

/// <summary>
/// @doc Replaces a global write buffer to a local variable buffer, used in conjunction of the SendDataLocks patch
/// </summary>
[Patch(PatchStage.PreMerge, "Removing NetMessage.SendData write buffer")]
[MonoMod.MonoModIgnore]
void PatchSendDataWriter(MonoModder modder)
{
    var SendData = modder.GetILCursor(() => Terraria.NetMessage.SendData(default, default, default, default, default, default, default, default, default, default, default));

    // make it easier to track down variables
    SendData.Body.SimplifyMacros();

    var stack = SendData.Method.GetStack();

    // remove `buffer[num].writer;` instructions
    VariableDefinition binaryWriter;
    {
        binaryWriter = SendData.Body.Variables.Single(v => v.VariableType.FullName == typeof(System.IO.BinaryWriter).FullName);

        var setters = SendData.Body.Instructions.Where(i => i.OpCode == OpCodes.Stloc && i.Operand == binaryWriter);

        if (setters.Count() != 2)
            throw new Exception($"{SendData.Method.FullName} expected 2 binaryWriter set instructions.");

        // the second set is only if this one yielded null; which it will never be after this
        var setter = setters.First();

        var stackOffset = stack.Single(s => s.Ins == setter);
        var initialVariable = stackOffset.FindRoot();

        SendData.Goto(initialVariable.Ins);

        SendData.RemoveWhile(i => i != setter, false);
    }

    /*
     * Insert :
	    MemoryStream output = new MemoryStream();
	    BinaryWriter binaryWriter = new BinaryWriter(output);
     */
    var type = modder.GetDefinition<System.IO.MemoryStream>();
    var memoryStream = new VariableDefinition(type);
    {
        SendData.Body.Variables.Add(memoryStream);
        var ms_ctor = modder.GetReference(() => new System.IO.MemoryStream());
        SendData.Emit(OpCodes.Newobj, SendData.Module.ImportReference(ms_ctor));
        SendData.Emit(OpCodes.Stloc, memoryStream);

        var pkt = modder.GetDefinition<TerrariaRuntime.PacketWriter>();
        //SendData.Emit(OpCodes.Ldloc, memoryStream);
        //SendData.Emit(OpCodes.Newobj, SendData.Module.ImportReference(bw_ctor));
        SendData.Emit(OpCodes.Ldloc, memoryStream);
        SendData.EmitDelegate(TerrariaRuntime.Hooks.NetMessage.InvokeCreatePacketWriter);
        (SendData.Previous.Operand as MethodReference).DeclaringType.Name = "NetMessage";
        binaryWriter.VariableType = SendData.Module.ImportReference(pkt);
    }

    // patch in a new custom compress tile block method that writes directly to the binary writer

    // replace all writeBuffer calls with output.ToArray()
    {
        var writeBuffers = SendData.Body.Instructions.Where(i => i.Operand is FieldReference fref
            && fref.DeclaringType.FullName == typeof(Terraria.MessageBuffer).FullName
            && fref.Name == nameof(Terraria.MessageBuffer.writeBuffer)
        ).ToArray();

        foreach (var writeBuffer in writeBuffers)
        {
            SendData.Goto(writeBuffer);

            SendData.GotoPrev(ins => ins.Operand is FieldReference fref
                && fref.DeclaringType.FullName == typeof(Terraria.NetMessage).FullName
                && fref.Name == nameof(Terraria.NetMessage.buffer)
            );

            SendData.Next.OpCode = OpCodes.Ldloc;
            SendData.Next.Operand = memoryStream;

            SendData.Goto(SendData.Next.Next);

            var toArray = modder.GetReference(() => new System.IO.MemoryStream().ToArray());
            SendData.Emit(OpCodes.Call, toArray);

            SendData.RemoveWhile(ins => ins != writeBuffer);
        }
    }

    // reapply
    SendData.Body.OptimizeMacros();
}


namespace TerrariaRuntime
{
    public class PacketWriter : BinaryWriter
    {
        public PacketWriter(Stream output) : base(output)
        {

        }
    }

    public static partial class Hooks
    {
        public static partial class NetMessage
        {
            public class CreatePacketWriterEventArgs : EventArgs
            {
                public PacketWriter PacketWriter { get; set; }
            }
            public static event EventHandler<CreatePacketWriterEventArgs> CreatePacketWriter;

            public static PacketWriter InvokeCreatePacketWriter(MemoryStream output)
            {
                var args = new CreatePacketWriterEventArgs();
                CreatePacketWriter?.Invoke(null, args);
                return args.PacketWriter ?? new PacketWriter(output);
            }
        }
    }
}

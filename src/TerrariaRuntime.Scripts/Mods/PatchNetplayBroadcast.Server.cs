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

using TerrariaRuntime.PatchEngine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using System.Linq;
using System.Threading;

/// <summary>
/// @doc Fix Terraria.Netplay.BroadcastThread infinite loop.
/// </summary>
[Patch(PatchStage.PostPatch, "Patching Netplay Broadcast")]
[MonoMod.MonoModIgnore]
void PatchNetplayBroadcast(MonoModder modder)
{
    var BroadcastThread = modder.GetILCursor(() => Terraria.Netplay.BroadcastThread());
    var isCurrentThread = BroadcastThread.Method.DeclaringType.Methods.Single(
        method => method.Name == nameof(Terraria.patch_Netplay.IsCurrentBroadcastThread));

    BroadcastThread.GotoNext(
        i => i.OpCode == OpCodes.Call
            && i.Operand is MethodReference methodReference
            && methodReference.DeclaringType.FullName == "System.Threading.Thread"
            && methodReference.Name == "Sleep"
            && (i.Next?.OpCode == OpCodes.Br_S || i.Next?.OpCode == OpCodes.Br)
    );

    BroadcastThread.Index++;

    BroadcastThread.Emit(OpCodes.Call, isCurrentThread);
    BroadcastThread.Next.OpCode = OpCodes.Brtrue;
    BroadcastThread.Index++;
    BroadcastThread.Emit(OpCodes.Ret);
}

namespace Terraria
{
    partial class patch_Netplay : Terraria.Netplay
    {
        public static bool IsCurrentBroadcastThread() =>
            ReferenceEquals(Thread.CurrentThread, Volatile.Read(ref Terraria.Netplay.broadcastThread));

        public extern static void orig_StopBroadCasting();
        public static void StopBroadCasting()
        {
            Interlocked.Exchange(ref Terraria.Netplay.broadcastThread, null)?.Join();
            orig_StopBroadCasting();
        }
    }
}

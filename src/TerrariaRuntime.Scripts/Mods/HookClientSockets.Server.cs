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
using System;
using Terraria.Net.Sockets;

/// <summary>
/// @doc Creates Hooks.Netplay.CreateTcpListener. Allows custom TCP implementations to be used.
/// </summary>
[Patch(PatchStage.PreMerge, "Hooking tcp interfaces")]
[MonoMod.MonoModIgnore]
void HookClientSockets(AssemblyPatcher modder)
{
    var callback = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Netplay.InvokeCreateTcpListener());
    var tcpSocket = modder.GetDefinition<TcpSocket>();
    modder.OnRewritingMethodBody += (MonoModder modder, MethodBody body, Instruction instr, int instri) =>
    {
        if (instr.OpCode == OpCodes.Newobj
            && instr.Operand is MethodReference mref
            && mref.DeclaringType.FullName == tcpSocket.FullName
            && body.Method.DeclaringType.FullName != tcpSocket.FullName
            && body.Method.Name != nameof(TerrariaRuntime.Hooks.Netplay.InvokeCreateTcpListener)
        )
        {
            if (mref.Parameters.Count != 0)
                throw new Exception($"Expected no parameters for {tcpSocket.FullName} in {body.Method.FullName}");

            instr.OpCode = OpCodes.Call;
            instr.Operand = callback;
        }
    };
}

namespace TerrariaRuntime
{
    public static partial class Hooks
    {
        public static partial class Netplay
        {
            public class CreateTcpListenerEventArgs : EventArgs
            {
                public ISocket Result { get; set; }
            }
            public static event EventHandler<CreateTcpListenerEventArgs> CreateTcpListener;

            public static ISocket InvokeCreateTcpListener()
            {
                var args = new CreateTcpListenerEventArgs();
                CreateTcpListener?.Invoke(null, args);
                return args.Result ?? new TcpSocket();
            }
        }
    }
}


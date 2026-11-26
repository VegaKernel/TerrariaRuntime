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
using MonoMod.Cil;
using System;
using System.Linq;

/// <summary>
/// @doc Creates Hooks.NetMessage.SendBytes. Allows plugins to intercept the sending of data.
/// </summary>
[Patch(PatchStage.PreMerge, "Hooking Terraria.NetMessage.SendData")]
[MonoMod.MonoModIgnore]
void HookClientSendBytes(MonoModder modder)
{
    var SendData = modder.GetILCursor(() => Terraria.NetMessage.SendData(default, default, default, default, default, default, default, default, default, default, default));

    while (SendData.TryGotoNext(
        i => i.OpCode == OpCodes.Callvirt
            && i.Operand is MethodReference methodReference
            && methodReference.Name == "AsyncSend"
    ))
    {
        SendData.FindNext(out ILCursor[] cursors, i => i.OpCode == OpCodes.Pop && (i.Previous?.OpCode == OpCodes.Leave || i.Previous?.OpCode == OpCodes.Leave_S));

        if (cursors.Length != 1) throw new System.Exception($"Expected to be within a try/catch block");

        var handler = SendData.Method.Body.ExceptionHandlers.Single(x => x.TryEnd == cursors[0].Next);

        if (handler.TryStart.Next.OpCode == OpCodes.Ldloc_S || handler.TryStart.Next.OpCode == OpCodes.Ldarg_1)
        {
            SendData.Emit(handler.TryStart.Next.OpCode, handler.TryStart.Next.Operand);
            SendData.EmitDelegate(TerrariaRuntime.Hooks.NetMessage.InvokeSendBytes);
            SendData.Remove();
        }
    }

        var SendPacket = modder.GetILCursor(() => Terraria.NetMessage.SendPacket(default, default));
        // find each AsyncSend, replace with TerrariaRuntime.Hooks.NetMessage.InvokeSendBytes, and inject arg2 (remoteClient) as well.
        while (SendPacket.TryGotoNext(
            i => i.OpCode == OpCodes.Callvirt
                && i.Operand is MethodReference methodReference
                && methodReference.Name == "AsyncSend"
        ))
        {            
            SendPacket.Emit(OpCodes.Ldarg_1); // remoteClient
            SendPacket.EmitDelegate(TerrariaRuntime.Hooks.NetMessage.InvokeSendBytes);
            SendPacket.Remove();
        }

        // Vanilla constructs the same instance-method delegate for each recipient.
        // Cache it on the RemoteClient, never by slot/socket, so slot reuse does not
        // carry a callback to another client object. Concurrent first callers may
        // construct equivalent delegates, but subsequent sends reuse the field.
        var callbackConstruction = SendPacket.Method.Body.Instructions.Single(i =>
            i.OpCode == OpCodes.Newobj && i.Operand is MethodReference constructor &&
            constructor.DeclaringType.FullName == "Terraria.Net.Sockets.SocketSendCallback" &&
            i.Previous?.Operand is MethodReference method && i.Previous.OpCode == OpCodes.Ldftn &&
            method.DeclaringType.FullName == "Terraria.RemoteClient" && method.Name == "ServerWriteCallBack");
        var callbackConstructor = (MethodReference)callbackConstruction.Operand;
        var targetMethod = (MethodReference)callbackConstruction.Previous.Operand;
        var clientType = targetMethod.DeclaringType.Resolve();
        var callbackField = new FieldDefinition("terrariaRuntimeServerWriteCallback", FieldAttributes.Private, callbackConstructor.DeclaringType);
        clientType.Fields.Add(callbackField);
        var getCallback = new MethodDefinition("TerrariaRuntimeGetServerWriteCallback", MethodAttributes.Assembly | MethodAttributes.HideBySig, callbackConstructor.DeclaringType);
        clientType.Methods.Add(getCallback);
        var il = getCallback.Body.GetILProcessor();
        var ret = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, callbackField);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue_S, ret);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, targetMethod);
        il.Emit(OpCodes.Newobj, callbackConstructor);
        il.Emit(OpCodes.Stfld, callbackField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, callbackField);
        il.Append(ret);
        callbackConstruction.Previous.OpCode = OpCodes.Nop;
        callbackConstruction.Previous.Operand = null;
        callbackConstruction.OpCode = OpCodes.Call;
        callbackConstruction.Operand = getCallback;
}

namespace TerrariaRuntime
{
    public static partial class Hooks
    {
        public static partial class NetMessage
        {
            public class SendBytesEventArgs : EventArgs
            {
                public HookResult? Result { get; set; }

                public Terraria.Net.Sockets.ISocket Socket { get; set; }
                public int RemoteClient { get; set; }
                public byte[] Data { get; set; }
                public int Offset { get; set; }
                public int Size { get; set; }
                public global::Terraria.Net.Sockets.SocketSendCallback Callback { get; set; }
                public object State { get; set; }
            }
            public static event EventHandler<SendBytesEventArgs> SendBytes;

            // Stack-local context for synchronous policies/observers. The byte buffer is
            // borrowed from the native sender and must not be retained after the call.
            public struct SendBytesContext
            {
                public SendBytesContext(Terraria.Net.Sockets.ISocket socket, byte[] data, int offset, int size,
                    global::Terraria.Net.Sockets.SocketSendCallback callback, object state, int remoteClient)
                {
                    Socket = socket;
                    Data = data;
                    Offset = offset;
                    Size = size;
                    Callback = callback;
                    State = state;
                    RemoteClient = remoteClient;
                    Cancelled = false;
                }

                public Terraria.Net.Sockets.ISocket Socket { get; }
                public int RemoteClient { get; }
                public byte[] Data { get; }
                public int Offset { get; }
                public int Size { get; }
                public global::Terraria.Net.Sockets.SocketSendCallback Callback { get; }
                public object State { get; }
                public bool Cancelled { get; private set; }
                public void Cancel() => Cancelled = true;
            }

            public delegate void SendBytesFastHandler(ref SendBytesContext context);
            public static event SendBytesFastHandler SendBytesFast;

            public static void InvokeSendBytes(Terraria.Net.Sockets.ISocket socket, byte[] data, int offset, int size, global::Terraria.Net.Sockets.SocketSendCallback callback, object state, int remoteClient)
            {
                // Legacy hooks retain their original multicast and cancellation semantics.
                // Do not allocate an EventArgs for every recipient when no legacy hook exists.
                var legacy = SendBytes;
                var fast = SendBytesFast;
                if (legacy != null)
                {
                    var args = new SendBytesEventArgs()
                    {
                        Socket = socket,
                        Data = data,
                        Offset = offset,
                        Size = size,
                        Callback = callback,
                        State = state,
                        RemoteClient = remoteClient
                    };
                    legacy(null, args);
                    if (args.Result == HookResult.Cancel) return;
                }
                if (fast != null)
                {
                    var context = new SendBytesContext(socket, data, offset, size, callback, state, remoteClient);
                    fast(ref context);
                    if (context.Cancelled) return;
                }
                socket.AsyncSend(data, offset, size, callback, state);
            }
        }
    }
}

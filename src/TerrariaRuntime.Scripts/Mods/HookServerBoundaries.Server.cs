using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Terraria.Net.Sockets;
using TerrariaRuntime.PatchEngine;

[Patch(PatchStage.PreMerge, "Adding dedicated server host boundaries")]
[MonoMod.MonoModIgnore]
void HookServerBoundaries(AssemblyPatcher modder)
{
    var checkBytes = GetMethod("Terraria.NetMessage", "CheckBytes");
    var buffer = checkBytes.DeclaringType.Fields.Single(field => field.Name == "buffer");
    var getData = checkBytes.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.MessageBuffer", "GetData"));
    var compact = checkBytes.Body.Instructions.Single(instruction => Field(instruction, OpCodes.Ldfld, "Terraria.MessageBuffer", "totalData")
        && instruction.Next.OpCode == OpCodes.Beq);
    var loop = checkBytes.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Bge
        && instruction.Offset > getData.Offset && instruction.Offset < compact.Offset);
    var condition = loop.Previous.Previous;
    var remaining = (VariableDefinition)condition.Operand;
    var offset = (VariableDefinition)condition.Previous.Operand;
    if (condition.Previous.OpCode != OpCodes.Stloc || loop.Next.OpCode != OpCodes.Leave)
        throw new InvalidOperationException("Unexpected CheckBytes loop.");
    var budget = new VariableDefinition(modder.Module.TypeSystem.Int32);
    checkBytes.Body.Variables.Add(budget);
    int budgetArgument = 0;
    var stop = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.NetMessage.StopAfterServerFrame(default, default, default, ref budgetArgument));
    Before(checkBytes, checkBytes.Body.Instructions[0], Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.NetMessage.GetServerFrameBudget(default))), Instruction.Create(OpCodes.Stloc, budget));
    var checkIl = checkBytes.Body.GetILProcessor();
    var stopInstructions = new[] { Instruction.Create(OpCodes.Ldloc, budget), Instruction.Create(OpCodes.Ldc_I4_0),
        Instruction.Create(OpCodes.Ble, condition), Instruction.Create(OpCodes.Ldsfld, buffer), Instruction.Create(OpCodes.Ldarg_0),
        Instruction.Create(OpCodes.Ldelem_Ref), Instruction.Create(OpCodes.Ldloc, offset), Instruction.Create(OpCodes.Ldloc, remaining),
        Instruction.Create(OpCodes.Ldloca, budget), Instruction.Create(OpCodes.Call, stop), Instruction.Create(OpCodes.Brtrue, loop.Next) };
    foreach (var instruction in stopInstructions)
        checkIl.InsertBefore(condition, instruction);
    checkBytes.Body.OptimizeMacros();

    var accepted = GetMethod("Terraria.Netplay", "OnConnectionAccepted");
    var enqueue = accepted.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method && method.Name == "Enqueue");
    enqueue.OpCode = OpCodes.Call;
    enqueue.Operand = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Netplay.EnqueueServerConnection(default, default));

    var activate = GetMethod("Terraria.RemoteClient", "Activate");
    Before(activate, LastReturn(activate), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1),
        Instruction.Create(OpCodes.Call, modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Netplay.NotifyServerConnectionActivated(default, default))));
    activate.Body.OptimizeMacros();
    var reset = GetMethod("Terraria.RemoteClient", "Reset");
    var oldSocket = new VariableDefinition(modder.Module.ImportReference(typeof(ISocket)));
    reset.Body.Variables.Add(oldSocket);
    Before(reset, reset.Body.Instructions[0], Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Netplay.GetServerConnectionSocket(default))), Instruction.Create(OpCodes.Stloc, oldSocket));
    Before(reset, LastReturn(reset), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldloc, oldSocket), Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Netplay.NotifyServerConnectionClosed(default, default))));
    reset.Body.OptimizeMacros();

    var packet = GetMethod("Terraria.MessageBuffer", "GetData");
    var packetIl = packet.Body.GetILProcessor();
    // A stock client sends initial character data before RequestWorldData. The
    // first server transition State 1 -> 2 is directly followed by WorldData.
    // Additional world-info sends bypass the state store and must bypass this hook.
    var beforeWorldData = packet.Body.Instructions.Single(instruction =>
        Field(instruction, OpCodes.Stfld, "Terraria.RemoteClient", "State")
        && instruction.Previous.OpCode == OpCodes.Ldc_I4 && (int)instruction.Previous.Operand == 2);
    var sendWorldData = beforeWorldData.Next;
    var skipInitialWorldData = packet.Body.Instructions.Single(instruction =>
        instruction.OpCode == OpCodes.Bne_Un && ReferenceEquals(instruction.Operand, sendWorldData));
    var worldDataCall = packet.Body.Instructions.SkipWhile(instruction => instruction != sendWorldData)
        .First(instruction => instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt);
    if (!Field(skipInitialWorldData.Previous.Previous, OpCodes.Ldfld, "Terraria.RemoteClient", "State") ||
        skipInitialWorldData.Previous.OpCode != OpCodes.Ldc_I4 || (int)skipInitialWorldData.Previous.Operand != 1 ||
        sendWorldData.OpCode != OpCodes.Ldc_I4 || (int)sendWorldData.Operand != 7 ||
        !Calls(worldDataCall, "Terraria.NetMessage", "TrySendData"))
        throw new InvalidOperationException("Unexpected initial world-data transition.");
    var worldDataBuffer = Instruction.Create(OpCodes.Ldarg_0);
    var worldDataSlot = Instruction.Create(OpCodes.Ldfld, packet.DeclaringType.Fields.Single(field => field.Name == "whoAmI"));
    packetIl.InsertAfter(beforeWorldData, worldDataBuffer);
    packetIl.InsertAfter(worldDataBuffer, worldDataSlot);
    packetIl.InsertAfter(worldDataSlot, Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerBeforeWorldData(default))));

    // The dedicated player's first world entry is the vanilla transition to
    // State 10. Notify after the state store but before the initial sync/greeting.
    // Branches which bypass this store must also bypass the notification.
    var enteringWorld = packet.Body.Instructions.Single(instruction =>
        Field(instruction, OpCodes.Stfld, "Terraria.RemoteClient", "State")
        && instruction.Previous.OpCode == OpCodes.Ldc_I4 && (int)instruction.Previous.Operand == 10);
    var enteringWorldBuffer = Instruction.Create(OpCodes.Ldarg_0);
    var enteringWorldSlot = Instruction.Create(OpCodes.Ldfld, packet.DeclaringType.Fields.Single(field => field.Name == "whoAmI"));
    packetIl.InsertAfter(enteringWorld, enteringWorldBuffer);
    packetIl.InsertAfter(enteringWorldBuffer, enteringWorldSlot);
    packetIl.InsertAfter(enteringWorldSlot, Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(default))));

    var strike = packet.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.NPC", "StrikeNPC"));
    var interaction = packet.Body.Instructions.Last(instruction => instruction.Offset < strike.Offset
        && Calls(instruction, "Terraria.NPC", "PlayerInteraction"));
    var generation = packet.Body.Instructions.Last(instruction => instruction.Offset < interaction.Offset
        && Calls(instruction, "Terraria.NPC", "get_generation"));
    var target = (VariableDefinition)generation.Previous.Operand;
    var reject = (Instruction)generation.Next.Next.Operand;
    var hit = interaction.Previous.Previous.Previous;
    var damage = (VariableDefinition)hit.Previous.Operand;
    if (target.Index != damage.Index + 4 || hit.Operand != target || generation.Next.Next.OpCode != OpCodes.Bne_Un)
        throw new InvalidOperationException("Unexpected packet 28 strike locals.");
    int damageArgument = 0, directionArgument = 0;
    float knockbackArgument = 0;
    byte criticalArgument = 0;
    var resolveHit = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.MessageBuffer.ResolveServerNpcHit(default, default,
        ref damageArgument, ref knockbackArgument, ref directionArgument, ref criticalArgument));
    Before(packet, hit, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldloc, target),
        Instruction.Create(OpCodes.Ldloca, damage), Instruction.Create(OpCodes.Ldloca, packet.Body.Variables[damage.Index + 1]),
        Instruction.Create(OpCodes.Ldloca, packet.Body.Variables[damage.Index + 2]), Instruction.Create(OpCodes.Ldloca, packet.Body.Variables[damage.Index + 3]),
        Instruction.Create(OpCodes.Call, resolveHit), Instruction.Create(OpCodes.Brfalse, reject));

    var projectileSpawn = packet.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.Projectile", "SetDefaultsFromSpawnValues"));
    var projectile = (VariableDefinition)projectileSpawn.Previous.Operand;
    Before(packet, projectileSpawn.Next, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldloc, projectile),
        Instruction.Create(OpCodes.Call, modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.MessageBuffer.NotifyServerProjectileSync(default, default))));
    var projectileKill = packet.Body.Instructions.First(instruction => instruction.Offset > strike.Offset
        && Calls(instruction, "Terraria.Projectile", "Kill"));
    var removedProjectile = (VariableDefinition)projectileKill.Previous.Operand;
    var removalJoin = (Instruction)projectileKill.Next.Operand;
    if (projectileKill.Next.OpCode != OpCodes.Br || removedProjectile.VariableType.FullName != "Terraria.Projectile"
        || !Field(removalJoin.Previous, OpCodes.Stfld, "Terraria.Projectile", "active"))
        throw new InvalidOperationException("Unexpected packet 29 projectile removal branches.");
    var notifyRemoval = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.MessageBuffer.NotifyServerProjectileRemoval(default, default));
    Before(packet, projectileKill.Next, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldloc, removedProjectile), Instruction.Create(OpCodes.Call, notifyRemoval));
    var deactivate = removalJoin.Previous;
    var loadOwner = Instruction.Create(OpCodes.Ldarg_0);
    var loadProjectile = Instruction.Create(OpCodes.Ldloc, removedProjectile);
    packetIl.InsertAfter(deactivate, loadOwner);
    packetIl.InsertAfter(loadOwner, loadProjectile);
    packetIl.InsertAfter(loadProjectile, Instruction.Create(OpCodes.Call, notifyRemoval));
    packet.Body.OptimizeMacros();

    var compress = GetMethod("Terraria.NetMessage", "CompressTileBlock");
    var originalCompression = compress.Body.Instructions[0];
    var compressIl = compress.Body.GetILProcessor();
    foreach (var parameter in compress.Parameters)
        compressIl.InsertBefore(originalCompression, Instruction.Create(OpCodes.Ldarg, parameter));
    compressIl.InsertBefore(originalCompression, Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.NetMessage.TryWriteServerTileBlock(default, default, default, default, default))));
    compressIl.InsertBefore(originalCompression, Instruction.Create(OpCodes.Brfalse, originalCompression));
    compressIl.InsertBefore(originalCompression, Instruction.Create(OpCodes.Ret));
    compress.Body.OptimizeMacros();

    var dedServ = GetMethod("Terraria.Main", "DedServ");
    var startServer = dedServ.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.Netplay", "StartServer"));
    var gameMenu = startServer.Next.Next;
    if (!Field(gameMenu, OpCodes.Stsfld, "Terraria.Main", "gameMenu"))
        throw new InvalidOperationException("Expected the headless server to leave the menu after starting the network.");
    Before(dedServ, gameMenu.Next, Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldReady())));
    var elapsed = dedServ.Body.Instructions.Last(instruction => Calls(instruction, "System.Diagnostics.Stopwatch", "get_Elapsed"));
    if (elapsed.Previous.Operand is not VariableDefinition stopwatch)
        throw new InvalidOperationException("Expected a dedicated tick stopwatch.");
    Before(dedServ, elapsed.Previous, Instruction.Create(OpCodes.Ldloc, stopwatch), Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Main.NotifyDedicatedTickCompleted(default))));
    var saveOnExit = dedServ.Body.Instructions.Single(instruction => Field(instruction, OpCodes.Ldsfld, "Terraria.Netplay", "SaveOnServerExit"));
    Before(dedServ, saveOnExit, Instruction.Create(OpCodes.Call,
        modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldStopping())));
    dedServ.Body.OptimizeMacros();

    var save = GetMethod("Terraria.IO.WorldFile", "InternalSaveWorld");
    var write = save.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.Utilities.FileUtilities", "Write"));
    var validation = save.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.IO.WorldFile", "ValidateWorld"));
    var backupWrite = save.Body.Instructions.Single(instruction => Calls(instruction, "Terraria.Utilities.FileUtilities", "WriteAllBytes"));
    var completed = new VariableDefinition(modder.Module.TypeSystem.Boolean);
    save.Body.Variables.Add(completed);
    if (write.Next.OpCode != OpCodes.Pop || backupWrite.Next.OpCode != OpCodes.Pop || validation.Next.OpCode != OpCodes.Brfalse)
        throw new InvalidOperationException("Unexpected dedicated world-save branches.");
    write.Next.OpCode = OpCodes.Stloc;
    write.Next.Operand = completed;
    Before(save, (Instruction)validation.Next.Operand, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, completed));
    Before(save, backupWrite.Next, Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldloc, completed),
        Instruction.Create(OpCodes.And), Instruction.Create(OpCodes.Stloc, completed));
    var notifySave = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveCompleted(default, default));
    Before(save, LastReturn(save), Instruction.Create(OpCodes.Ldloc, completed), Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Call, notifySave));
    save.Body.OptimizeMacros();

    var saveWrapper = GetMethod("Terraria.IO.WorldFile", "SaveWorld");
    var handler = saveWrapper.Body.ExceptionHandlers.Single(item => item.HandlerType == ExceptionHandlerType.Catch);
    var notifySaveFailure = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveFailure(default));
    Before(saveWrapper, handler.HandlerStart, Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Call, notifySaveFailure));
    saveWrapper.Body.OptimizeMacros();

    MethodDefinition GetMethod(string type, string name)
    {
        var method = modder.Module.GetType(type).Methods.Single(candidate => candidate.Name == name && candidate.HasBody);
        method.Body.SimplifyMacros();
        return method;
    }

    static bool Calls(Instruction instruction, string type, string name) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        && instruction.Operand is MethodReference method && method.Name == name && method.DeclaringType.FullName == type;

    static bool Field(Instruction instruction, OpCode opcode, string type, string name) =>
        instruction.OpCode == opcode && instruction.Operand is FieldReference field
        && field.Name == name && field.DeclaringType.FullName == type;

    static Instruction LastReturn(MethodDefinition method) => method.Body.Instructions.Last(instruction => instruction.OpCode == OpCodes.Ret);

    static void Before(MethodDefinition method, Instruction anchor, params Instruction[] instructions)
    {
        var original = Instruction.Create(OpCodes.Nop);
        original.OpCode = anchor.OpCode;
        original.Operand = anchor.Operand;
        anchor.OpCode = OpCodes.Nop;
        anchor.Operand = null;
        var il = method.Body.GetILProcessor();
        foreach (var instruction in instructions)
        {
            il.InsertAfter(anchor, instruction);
            anchor = instruction;
        }
        il.InsertAfter(anchor, original);
    }
}

namespace TerrariaRuntime
{
    public static partial class Hooks
    {
        public static partial class Netplay
        {
            public static int ServerPendingConnectionLimit { get; set; }
            public static Func<ISocket, bool> ServerConnectionAdmission { get; set; }
            public static event Action<int, ISocket> ServerConnectionActivated;
            public static event Action<int, ISocket> ServerConnectionClosed;
            /// <summary>Notifies once on the initial world-data transition, before sending the world's information.</summary>
            public static event Action<int, ISocket> ServerPlayerBeforeWorldData;
            /// <summary>Notifies after the server's initial connected-state transition, before initial player sync.</summary>
            public static event Action<int, ISocket> ServerPlayerEnteringWorld;

            public static void EnqueueServerConnection(ConcurrentQueue<ISocket> queue, ISocket socket)
            {
                var admission = ServerConnectionAdmission;
                int limit = ServerPendingConnectionLimit;
                if (admission is null && limit <= 0)
                {
                    queue.Enqueue(socket);
                    return;
                }
                if (admission is null || admission(socket))
                {
                    lock (queue)
                    {
                        if (limit <= 0 || queue.Count < limit)
                        {
                            queue.Enqueue(socket);
                            return;
                        }
                    }
                }
                socket.Close();
            }

            public static void NotifyServerConnectionActivated(global::Terraria.RemoteClient client, ISocket socket)
            {
                if (global::Terraria.Main.dedServ)
                    ServerConnectionActivated?.Invoke(client.Id, socket);
            }

            public static ISocket GetServerConnectionSocket(global::Terraria.RemoteClient client) => client.Socket;

            public static void NotifyServerPlayerBeforeWorldData(int slot)
            {
                if (global::Terraria.Main.dedServ && global::Terraria.Main.netMode == 2
                    && (uint)slot < (uint)global::Terraria.Netplay.Clients.Length
                    && global::Terraria.Netplay.Clients[slot] is { State: 2, Socket: { } socket })
                    NotifyLifecycle(ServerPlayerBeforeWorldData, slot, socket);
            }

            public static void NotifyServerPlayerEnteringWorld(int slot)
            {
                if (global::Terraria.Main.dedServ && global::Terraria.Main.netMode == 2
                    && (uint)slot < (uint)global::Terraria.Netplay.Clients.Length
                    && global::Terraria.Netplay.Clients[slot] is { State: 10, Socket: { } socket })
                    NotifyLifecycle(ServerPlayerEnteringWorld, slot, socket);
            }

            // Observers cannot interrupt vanilla's transition/cleanup. These
            // notifications are not vetoes; failures belong to the host policy.
            static void NotifyLifecycle(Action<int, ISocket> subscribers, int slot, ISocket socket)
            {
                if (subscribers is null)
                    return;
                foreach (Action<int, ISocket> subscriber in subscribers.GetInvocationList())
                {
                    try { subscriber(slot, socket); }
                    catch (Exception ex) { Console.Error.WriteLine($"TerrariaRuntime lifecycle observer failed: {ex}"); }
                }
            }

            public static void NotifyServerConnectionClosed(global::Terraria.RemoteClient client, ISocket socket)
            {
                if (global::Terraria.Main.dedServ && socket is not null)
                ServerConnectionClosed?.Invoke(client.Id, socket);
            }
        }

        public static partial class NetMessage
        {
            public delegate bool ServerTileBlockWriter(int x, int y, short width, short height, Stream destination);
            public static Func<int, int> ServerFrameBudget { get; set; }
            public static ServerTileBlockWriter WriteServerTileBlock { get; set; }

            public static int GetServerFrameBudget(int slot) => global::Terraria.Main.dedServ ? ServerFrameBudget?.Invoke(slot) ?? 0 : 0;

            public static bool StopAfterServerFrame(global::Terraria.MessageBuffer buffer, int offset, int remaining, ref int budget)
            {
                if (budget <= 0 || --budget != 0 || remaining < 2)
                    return false;
                int nextLength = buffer.readBuffer[offset] | buffer.readBuffer[offset + 1] << 8;
                if (nextLength > remaining)
                    return false;
                buffer.checkBytes = true;
                return true;
            }

            public static bool TryWriteServerTileBlock(int x, int y, short width, short height, Stream destination) =>
                global::Terraria.Main.dedServ && WriteServerTileBlock?.Invoke(x, y, width, height, destination) == true;
        }

        public static partial class MessageBuffer
        {
            public delegate bool ServerNpcHitResolver(int attackerSlot, global::Terraria.NPC target,
                ref int damage, ref float knockback, ref int direction, ref byte critical);
            public static ServerNpcHitResolver ServerNpcHit { get; set; }
            public static event Action<int, global::Terraria.Projectile> ServerProjectileSynced;
            public static event Action<int, global::Terraria.Projectile> ServerProjectileRemoved;

            public static bool ResolveServerNpcHit(global::Terraria.MessageBuffer buffer, global::Terraria.NPC target,
                ref int damage, ref float knockback, ref int direction, ref byte critical)
            {
                var resolver = ServerNpcHit;
                if (resolver is null || !global::Terraria.Main.dedServ)
                    return true;
                return resolver(buffer.whoAmI, target, ref damage, ref knockback, ref direction, ref critical)
                    && damage is >= 0 and <= short.MaxValue && float.IsFinite(knockback) && direction is >= -1 and <= 1 && critical <= 1;
            }

            public static void NotifyServerProjectileSync(global::Terraria.MessageBuffer buffer, global::Terraria.Projectile projectile)
            {
                if (global::Terraria.Main.dedServ)
                    ServerProjectileSynced?.Invoke(buffer.whoAmI, projectile);
            }

            public static void NotifyServerProjectileRemoval(global::Terraria.MessageBuffer buffer, global::Terraria.Projectile projectile)
            {
                if (global::Terraria.Main.dedServ && projectile is not null)
                    ServerProjectileRemoved?.Invoke(buffer.whoAmI, projectile);
            }
        }

        public static partial class Main
        {
            public static event Action DedicatedWorldReady;
            public static event Action<TimeSpan> DedicatedTickCompleted;
            public static event Action DedicatedWorldStopping;
            public static event Action<bool, Exception> DedicatedWorldSaveCompleted;

            public static void NotifyDedicatedWorldReady()
            {
                if (!global::Terraria.Netplay.Disconnect)
                    DedicatedWorldReady?.Invoke();
            }
            public static void NotifyDedicatedTickCompleted(Stopwatch stopwatch) => DedicatedTickCompleted?.Invoke(stopwatch.Elapsed);
            public static void NotifyDedicatedWorldStopping() => DedicatedWorldStopping?.Invoke();
            public static void NotifyDedicatedWorldSaveCompleted(bool success, Exception error)
            {
                if (global::Terraria.Main.dedServ)
                    DedicatedWorldSaveCompleted?.Invoke(success, error);
            }
            public static void NotifyDedicatedWorldSaveFailure(Exception error) => NotifyDedicatedWorldSaveCompleted(false, error);
        }
    }
}

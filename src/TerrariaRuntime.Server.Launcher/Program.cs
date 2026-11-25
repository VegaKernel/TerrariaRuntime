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
using MonoMod.RuntimeDetour;
using TerrariaRuntime;
using ReLogic.OS;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

Console.WriteLine($"TerrariaRuntime.Server.Launcher Arch: {RuntimeInformation.ProcessArchitecture}");
bool testInit = args.Any(x => string.Equals(x, "-test-init", StringComparison.OrdinalIgnoreCase));
bool validationPassed = false;

static Assembly GetTerrariaAssembly() => typeof(Terraria.Animation).Assembly;

static void Program_LaunchGame(object? sender, HookEvents.Terraria.Program.LaunchGameEventArgs e)
{
    var savePath = typeof(Terraria.Program).GetField("SavePath");
    if (savePath is not null)
    {
        savePath.SetValue(
            null,
            Terraria.Program.LaunchParameters.ContainsKey("-savedirectory")
                ? Terraria.Program.LaunchParameters["-savedirectory"]
                : Platform.Get<IPathService>().GetStoragePath("Terraria"));
    }

    HookEvents.Terraria.Main.Initialize += (_, args) =>
    {
        args.ContinueExecution = false;
        args.OriginalMethod();

        Terraria.Main.instance._achievements = new Terraria.Achievements.AchievementManager();
        Hooks.Initializers.AchievementInitializerLoad += (_, loadArgs) => loadArgs.ShouldLoad = true;
        Terraria.Initializers.AchievementInitializer.Load();
    };

    if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
    {
        typeof(Terraria.Main).GetField("SkipAssemblyLoad")?.SetValue(null, true);
        return;
    }

    Console.WriteLine("Preloading assembly...");

    if (!string.Equals(GetTerrariaAssembly().EntryPoint?.DeclaringType?.Name, "MonoLaunch", StringComparison.Ordinal))
    {
        Terraria.Main.dedServ = true;
        Terraria.Program.ForceLoadAssembly(GetTerrariaAssembly(), initializeStaticMembers: true);
    }
}

static void DiagnosticHooks()
{
    var calls = new Dictionary<string, int>();
    DateTime lastUpdate = DateTime.Now;

    void Print<TSender, TArgs>(TSender sender, TArgs args)
    {
        dynamic evt = args!;
        var name = evt.OriginalMethod.Method.Name;
        calls[name] = calls.TryGetValue(name, out int count) ? count + 1 : 1;

        if ((DateTime.Now - lastUpdate).TotalSeconds < 1)
            return;

        Console.WriteLine(string.Join(
            Environment.NewLine,
            calls.Keys.OrderByDescending(x => calls[x]).Select(name => $"\t- {name}:{calls[name]}")));
        lastUpdate = DateTime.Now;
    }

    HookEvents.Terraria.Main.Update += Print;
    HookEvents.Terraria.Main.Initialize += Print;
    HookEvents.Terraria.Main.checkXMas += Print;
    HookEvents.Terraria.Main.checkHalloween += Print;
    HookEvents.Terraria.Main.startDedInput += Print;
    HookEvents.Terraria.Item.netDefaults += Print;
    HookEvents.Terraria.NetMessage.greetPlayer += Print;
    HookEvents.Terraria.Netplay.StartServer += Print;
    HookEvents.Terraria.Netplay.OnConnectionAccepted += Print;
    HookEvents.Terraria.NPC.SetDefaults += Print;
    HookEvents.Terraria.NPC.SetDefaultsFromNetId += Print;
    HookEvents.Terraria.NPC.StrikeNPC += Print;
    HookEvents.Terraria.NPC.Transform += Print;
    HookEvents.Terraria.NPC.AI += Print;
    HookEvents.Terraria.WorldGen.StartHardmode += Print;
    HookEvents.Terraria.WorldGen.SpreadGrass += Print;
    HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage += Print;
    HookEvents.Terraria.IO.WorldFile.SaveWorld += Print;
    HookEvents.Terraria.Net.NetManager.SendData += Print;
    HookEvents.Terraria.Projectile.SetDefaults += Print;
    HookEvents.Terraria.Projectile.AI += Print;
    HookEvents.Terraria.RemoteClient.Reset += Print;
}

static void Program_OnLaunched(object? sender, EventArgs e)
{
    HookEvents.Terraria.Main.DedServ += (_, _) => Console.WriteLine("DedServEvent");

    On.Terraria.Main.ctor += Main_ctor;

    Hooks.MessageBuffer.ClientUUIDReceived += (_, args) =>
        Console.WriteLine($"ClientUUIDReceived {Terraria.Netplay.Clients[args.Instance.whoAmI].ClientUUID}");

    Hooks.NPC.MechSpawn += (_, args) =>
        Console.WriteLine($"Hooks.NPC.MechSpawn x={args.X}, y={args.Y}, type={args.Type}, num={args.Num}, num2={args.Num2}, num3={args.Num3}");

    Hooks.Item.MechSpawn += (_, args) =>
        Console.WriteLine($"Hooks.Item.MechSpawn x={args.X}, y={args.Y}, type={args.Type}, num={args.Num}, num2={args.Num2}, num3={args.Num3}");

    On.Terraria.RemoteClient.Update += (orig, rc) => orig(rc);
    On.Terraria.RemoteClient.Reset += (orig, rc) => orig(rc);
}

static void Main_ctor(On.Terraria.Main.orig_ctor orig, Terraria.Main self)
{
    orig(self);
    Terraria.Main.SkipAssemblyLoad = true;
    Console.WriteLine($"Main invoked, random number: {Terraria.Main.rand.Next()}");
}

void Main_DedServ(object? sender, HookEvents.Terraria.Main.DedServEventArgs e)
{
    Console.WriteLine("Server init process successful");

    if (testInit)
    {
        e.ContinueExecution = false;
        ValidateNetworkHooks();
        validationPassed = true;
    }
}

static void ValidateNetworkHooks()
{
    if (Terraria.Netplay.UseGNS)
        throw new InvalidOperationException("The package must start without native GNS libraries.");

    var legacyListener = new Terraria.Net.Sockets.TcpSocket();
    var serverListener = new Terraria.Net.Sockets.TcpSocket();
    void CreateLegacyListener(object? sender, Hooks.Netplay.CreateTcpListenerEventArgs args) => args.Result = legacyListener;

    int packetPassCalls = 0;
    Hooks.Netplay.CreateTcpListener += CreateLegacyListener;
    try
    {
        Hooks.Netplay.ServerTcpListenerFactory = () => serverListener;
        if (!ReferenceEquals(Hooks.Netplay.InvokeCreateServerTcpListener(), serverListener))
            throw new InvalidOperationException("Dedicated TCP listener override was not used.");

        Hooks.Netplay.ServerTcpListenerFactory = null;
        if (!ReferenceEquals(Hooks.Netplay.InvokeCreateServerTcpListener(), legacyListener))
            throw new InvalidOperationException("Legacy TCP listener fallback was not used.");

        Hooks.Netplay.ServerPacketPass = () => packetPassCalls++;
        Terraria.Netplay.UpdateServerInMainThread();
        Hooks.Netplay.ServerPacketPass = null;
        if (packetPassCalls != 1 || Hooks.Netplay.TryInvokeServerPacketPass())
            throw new InvalidOperationException("Server packet pass override did not replace exactly one pass.");

        ValidateServerFrameBudget();
        ValidateConnectionAdmission();
    }
    finally
    {
        Hooks.Netplay.ServerTcpListenerFactory = null;
        Hooks.Netplay.CreateTcpListener -= CreateLegacyListener;
        Hooks.Netplay.ServerPacketPass = null;
    }
    Console.WriteLine("Server network hooks successful");
}

static void ValidateConnectionAdmission()
{
    var queue = new System.Collections.Concurrent.ConcurrentQueue<Terraria.Net.Sockets.ISocket>();
    Terraria.Net.Sockets.ISocket accepted = new Terraria.Net.Sockets.TcpSocket();
    Terraria.Net.Sockets.ISocket excess = new Terraria.Net.Sockets.TcpSocket();
    Terraria.Net.Sockets.ISocket denied = new Terraria.Net.Sockets.TcpSocket();
    try
    {
        Hooks.Netplay.ServerPendingConnectionLimit = 1;
        Hooks.Netplay.ServerConnectionAdmission = socket => !ReferenceEquals(socket, denied);
        Hooks.Netplay.EnqueueServerConnection(queue, accepted);
        Hooks.Netplay.EnqueueServerConnection(queue, excess);
        if (!queue.TryDequeue(out var entry) || !ReferenceEquals(entry, accepted) || !queue.IsEmpty)
            throw new InvalidOperationException("The pending connection queue must retain only the admitted socket.");
        Hooks.Netplay.EnqueueServerConnection(queue, denied);
        if (!queue.IsEmpty)
            throw new InvalidOperationException("A rejected socket must not enter the pending queue.");
    }
    finally
    {
        Hooks.Netplay.ServerPendingConnectionLimit = 0;
        Hooks.Netplay.ServerConnectionAdmission = null;
        accepted.Close();
        excess.Close();
        denied.Close();
    }
}

static void ValidateServerFrameBudget()
{
    const int slot = 0;
    var oldClient = Terraria.Netplay.Clients[slot];
    var oldBuffer = Terraria.NetMessage.buffer[slot];
    var oldPlayer = Terraria.Main.player[slot];
    var oldNetMode = Terraria.Main.netMode;
    var client = new Terraria.RemoteClient { Id = slot };
    var frames = new List<byte>();
    var socket = new Terraria.Net.Sockets.TcpSocket();
    int activated = 0, closing = 0, closed = 0, entering = 0;
    bool preservedConnection = false;
    var order = new List<string>();
    void OnActivated(int id, Terraria.Net.Sockets.ISocket connection)
    {
        if (id == slot && ReferenceEquals(connection, socket)) { activated++; order.Add("activated"); }
    }
    void OnEnteringWorld(int id, Terraria.Net.Sockets.ISocket connection)
    {
        if (id == slot && ReferenceEquals(connection, socket) && client.State == 10)
        { entering++; order.Add("entering"); }
    }
    void OnClosing(int id, Terraria.Net.Sockets.ISocket connection)
    {
        if (id == slot && ReferenceEquals(connection, socket))
        {
            closing++;
            preservedConnection = ReferenceEquals(client.Socket, socket) && client.State == 10 && client.Data.ContainsKey("lifecycle-smoke");
            order.Add("closing");
        }
    }
    void OnClosed(int id, Terraria.Net.Sockets.ISocket connection)
    {
        if (id == slot && ReferenceEquals(connection, socket)) { closed++; order.Add("closed"); }
    }
    void RecordFrame(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        frames.Add(args.PacketId);
        args.Result = HookResult.Cancel;
    }

    try
    {
        Terraria.Main.netMode = 2;
        Terraria.Netplay.Clients[slot] = client;
        Terraria.NetMessage.buffer[slot] = new Terraria.MessageBuffer { whoAmI = slot };
        client.Reset();
        Hooks.Netplay.ServerConnectionActivated += OnActivated;
        Hooks.Netplay.ServerPlayerEnteringWorld += OnEnteringWorld;
        Hooks.Netplay.ServerConnectionClosing += OnClosing;
        Hooks.Netplay.ServerConnectionClosed += OnClosed;
        client.Activate(socket);
        Hooks.MessageBuffer.GetData += RecordFrame;
        Hooks.NetMessage.ServerFrameBudget = _ => 1;
        Terraria.NetMessage.ReceiveBytes(new byte[] { 3, 0, 1, 3, 0, 2, 3, 0 }, 8, slot);
        Terraria.NetMessage.CheckBytes(slot);
        if (!frames.SequenceEqual(new byte[] { 1 }) || !Terraria.NetMessage.buffer[slot].checkBytes)
            throw new InvalidOperationException("The first pass must defer the second complete frame.");
        Terraria.NetMessage.CheckBytes(slot);
        if (!frames.SequenceEqual(new byte[] { 1, 2 }) || Terraria.NetMessage.buffer[slot].checkBytes)
            throw new InvalidOperationException("A partial frame must wait for more data.");
        Terraria.NetMessage.ReceiveBytes(new byte[] { 3 }, 1, slot);
        Terraria.NetMessage.CheckBytes(slot);
        if (!frames.SequenceEqual(new byte[] { 1, 2, 3 }))
            throw new InvalidOperationException("Deferred frames must keep their wire order.");
        Hooks.Netplay.NotifyServerPlayerEnteringWorld(slot);
        if (entering != 0)
            throw new InvalidOperationException("An unconnected slot must not emit a world entry notification.");
        client.State = 10;
        Hooks.Netplay.NotifyServerPlayerEnteringWorld(slot);
        client.Data["lifecycle-smoke"] = true;
        client.Reset();
        if (activated != 1 || entering != 1 || closing != 1 || closed != 1 || !preservedConnection || client.Data.ContainsKey("lifecycle-smoke") ||
            !order.SequenceEqual(new[] { "activated", "entering", "closing", "closed" }))
            throw new InvalidOperationException("Connection lifecycle must expose the original connection before reset, then close in order.");
    }
    finally
    {
        Hooks.NetMessage.ServerFrameBudget = null;
        Hooks.MessageBuffer.GetData -= RecordFrame;
        Hooks.Netplay.ServerConnectionActivated -= OnActivated;
        Hooks.Netplay.ServerPlayerEnteringWorld -= OnEnteringWorld;
        Hooks.Netplay.ServerConnectionClosing -= OnClosing;
        Hooks.Netplay.ServerConnectionClosed -= OnClosed;
        if (client.Socket is not null) client.Reset();
        Terraria.NetMessage.buffer[slot] = oldBuffer;
        Terraria.Main.player[slot] = oldPlayer;
        Terraria.Netplay.Clients[slot] = oldClient;
        Terraria.Main.netMode = oldNetMode;
    }
}

HookEvents.Terraria.Main.DedServ += Main_DedServ;
HookEvents.Terraria.Program.LaunchGame += Program_LaunchGame;
if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
    Terraria.Program.OnLaunched += Program_OnLaunched;
else
    DiagnosticHooks();

var terrariaAssembly = GetTerrariaAssembly();

AppDomain.CurrentDomain.AssemblyResolve += (_, resolveArgs) =>
{
    var assemblyName = new AssemblyName(resolveArgs.Name).Name;
    if (string.IsNullOrWhiteSpace(assemblyName))
        return null;

    var resourceName = assemblyName + ".dll";
    var embeddedName = Array.Find(
        terrariaAssembly.GetManifestResourceNames(),
        element => element.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
    if (embeddedName is null)
        return null;

    using var stream = terrariaAssembly.GetManifestResourceStream(embeddedName);
    if (stream is null)
        return null;

    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return Assembly.Load(buffer.ToArray());
};

var entryPoint = terrariaAssembly.EntryPoint
    ?? throw new InvalidOperationException("Patched Terraria assembly does not expose an entry point.");
entryPoint.Invoke(null, new object[] { args });
return testInit && !validationPassed ? 1 : 0;

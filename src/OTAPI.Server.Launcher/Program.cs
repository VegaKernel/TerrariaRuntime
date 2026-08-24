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
using OTAPI;
using ReLogic.OS;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

Console.WriteLine($"OTAPI.Server.Launcher Arch: {RuntimeInformation.ProcessArchitecture}");

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

static void Main_DedServ(object? sender, HookEvents.Terraria.Main.DedServEventArgs e)
{
    Console.WriteLine("Server init process successful");

    if (Environment.GetCommandLineArgs().Any(x => string.Equals(x, "-test-init", StringComparison.OrdinalIgnoreCase)))
        e.ContinueExecution = false;
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

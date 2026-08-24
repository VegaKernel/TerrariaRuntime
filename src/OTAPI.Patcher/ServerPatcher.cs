using OTAPI.PatchEngine;
using OTAPI.PatchEngine.CSharp;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using static OTAPI.PatchEngine.PatchContext;

namespace OTAPI.Patcher;

[MonoMod.MonoModIgnore]
internal sealed class ServerPatcher
{
    public PatchContext PatchContext { get; } = new("Terraria");

    public string DisplayText => "OTAPI PC Server";

    public string InstallDestination { get; } = Environment.CurrentDirectory;

    public NugetPackageBuilder NugetPackager { get; } = new("OTAPI.nupkg", Path.Combine(AppContext.BaseDirectory, "../../../../../docs/OTAPI.nuspec"));
    public MarkdownDocumentor MarkdownDocumentor { get; } = new("OTAPI.PC.Server.patch.md");

    public string ArtifactName { get; } = "artifact-pc";

    public bool PublicEverything => true;
    public bool GenerateSymbols => false;

    public string GetEmbeddedResourcesDirectory(string fileinput)
    {
        return Path.Combine(PatchContext.BaseDirectory, Path.GetDirectoryName(fileinput) ?? string.Empty);
    }

    public async Task<string> DownloadServerAsync()
    {
        const string serverUrl = "https://terraria.org/api/download/pc-dedicated-server/terraria-server-1458.zip";
        var zipPath = await Common.DownloadZipAsync(serverUrl);
        var extracted = Common.ExtractZip(zipPath);

        return Directory.EnumerateFiles(extracted, "TerrariaServer.exe", SearchOption.AllDirectories)
            .Single(path => string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)),
                "Windows",
                StringComparison.OrdinalIgnoreCase));
    }

    public void MergeReLogic(AssemblyPatcher modder, string embeddedResources)
    {
        var path = Directory.GetFiles(embeddedResources)
            .Single(x => Path.GetFileName(x).Equals("ReLogic.dll", StringComparison.OrdinalIgnoreCase));

        modder.ReadMod(path);
        modder.RelinkAssembly("ReLogic");
    }

    public void AddVersion(AssemblyPatcher modder)
    {
        modder.AddVersion(Common.GetVersion());
    }

    public void CompileAndReadShims(AssemblyPatcher modder)
    {
        var ldr = new CSharpLoader(PatchContext)
            .SetAutoLoadAssemblies(true)
            .SetMarkdownDocumentor(MarkdownDocumentor)
            .SetModder(modder);
        ldr.AssemblyContext = PatchContext.PluginLoader.AssemblyLoader;
        var md = ldr.CreateMetaData();
        _ = ldr.LoadModules(md, "shims").ToArray();
    }

    public void LoadPatches(AssemblyPatcher patcher)
    {
        _ = new CSharpLoader(PatchContext)
            .SetAutoLoadAssemblies(true)
            .SetMarkdownDocumentor(MarkdownDocumentor)
            .SetModder(patcher)
            .LoadModifications();
    }

    public void RemovePatchScaffolding(AssemblyPatcher patcher)
    {
        var generatedPrograms = patcher.Module.Types
            .Where(type => string.IsNullOrEmpty(type.Namespace)
                && type.Name == "Program"
                && type.Methods.Any(method => method.Name.Contains("<Main>$", StringComparison.Ordinal)))
            .ToArray();
        foreach (var type in generatedPrograms)
            patcher.Module.Types.Remove(type);

        var patchTimeReferences = new[]
        {
            "OTAPI.PatchEngine",
            "MonoMod.Patcher",
            "Mono.Cecil",
            "Mono.Cecil.Rocks",
        };
        foreach (var reference in patcher.Module.AssemblyReferences.ToArray())
            if (patchTimeReferences.Contains(reference.Name, StringComparer.Ordinal))
                patcher.Module.AssemblyReferences.Remove(reference);
    }

    public async Task PatchAsync()
    {
        Console.WriteLine($"Open Terraria API v{Common.GetVersion()}");

        Common.AddMarkdownFormatter();

        var vanillaDllPath = await DownloadServerAsync();
            Console.WriteLine("[OTAPI] Extracting embedded binaries and packing into one binary...");
            var embeddedResources = this.ExtractResources(vanillaDllPath);

            var outputsPath = Path.Combine(PatchContext.BaseDirectory, "outputs");
            Directory.CreateDirectory(outputsPath);

            var terrariaServerDllDestination = Path.Combine(outputsPath, "TerrariaServer.dll");
            var otapiDllDestination = Path.Combine(PatchContext.BaseDirectory, "OTAPI.dll");
            var otapiRuntimeDllDestination = Path.Combine(PatchContext.BaseDirectory, "OTAPI.Runtime.dll");

            PatchContext.ReferenceFiles.AddRange(new[]
            {
                "OTAPI.PatchEngine.dll",
                "MonoMod.Patcher.dll",
                "MonoMod.Utils.dll",
                "MonoMod.RuntimeDetour.dll",
                "Mono.Cecil.dll",
                "Mono.Cecil.Rocks.dll",
                "Steamworks.NET.dll",
            });

            this.Patch("Merging and pregenerating files", vanillaDllPath, terrariaServerDllDestination, PublicEverything, (modType, mm) =>
            {
                if (mm is not null)
                {
                    if (modType == PatchStage.PreRead)
                    {
                        mm.AssemblyResolver.AddSearchDirectory(embeddedResources);
                    }
                    else if (modType == PatchStage.Read)
                    {
                        mm.ReadMod(typeof(HookEvent).Assembly.Location);

                        Console.WriteLine("[OTAPI] Changing to AnyCPU (x64 preferred)");
                        mm.SetAnyCPU();

                        CompileAndReadShims(mm);
                        MergeReLogic(mm, embeddedResources);
                        this.AddPatchMetadata(mm, mm.Module.Name, vanillaDllPath);
                    }
                }
                return EApplyResult.Continue;
            });

            PatchContext.ReferenceFiles.Add(terrariaServerDllDestination);

            var asm = Assembly.LoadFile(terrariaServerDllDestination);
            var cache = new Dictionary<string, Assembly>();

            Assembly? ResolvingFile(AssemblyLoadContext _, AssemblyName args)
            {
                if (args.Name is null)
                    return null;
                if (cache.TryGetValue(args.Name, out Assembly? resolved))
                    return resolved;

                if (string.Equals(args.Name, asm.GetName().Name, StringComparison.Ordinal))
                {
                    cache[args.Name] = asm;
                    return asm;
                }

                var dll = $"{args.Name}.dll";

                if (File.Exists(dll))
                {
                    resolved = Assembly.Load(File.ReadAllBytes(dll));
                    cache[args.Name] = resolved;
                    return resolved;
                }

                return null;
            }

            AssemblyLoadContext.Default.Resolving += ResolvingFile;
            try
            {
                this.Patch("Applying modifications", terrariaServerDllDestination, otapiDllDestination, false, (modType, mm) =>
                {
                    if (mm is not null)
                    {
                        if (modType == PatchStage.PreRead)
                        {
                            mm.AssemblyResolver.AddSearchDirectory(embeddedResources);
                        }
                        else if (modType == PatchStage.Read)
                        {
                            LoadPatches(mm);
                        }
                        else if (modType == PatchStage.PreWrite)
                        {
                            mm.PatchContext.TargetAssemblyName = "OTAPI";
                            AddVersion(mm);
                            this.AddPatchMetadata(mm);
                            mm.AddEnvMetadata();

                            Console.Write("[OTAPI] Generating static hooks...");
                            mm.Module.GetType("Terraria.Main").CreateHooks(mm);
                            mm.Module.GetType("Terraria.Item").CreateHooks(mm);
                            mm.Module.GetType("Terraria.NetMessage").CreateHooks(mm);
                            mm.Module.GetType("Terraria.Netplay").CreateHooks(mm);
                            mm.Module.GetType("Terraria.NPC").CreateHooks(mm);
                            mm.Module.GetType("Terraria.WorldGen").CreateHooks(mm);
                            mm.Module.GetType("Terraria.Chat.ChatHelper").CreateHooks(mm);
                            mm.Module.GetType("Terraria.GameContent.ItemDropRules.CommonCode").CreateHooks(mm);
                            mm.Module.GetType("Terraria.IO.WorldFile").CreateHooks(mm);
                            mm.Module.GetType("Terraria.Net.NetManager").CreateHooks(mm);
                            mm.Module.GetType("Terraria.Projectile").CreateHooks(mm);
                            mm.Module.GetType("Terraria.RemoteClient").CreateHooks(mm);
                            mm.Module.GetType("Terraria.Program").CreateHooks(mm);
                            mm.Module.GetType("Terraria.Wiring").CreateHooks(mm);
                            mm.Module.GetType("Terraria.GameContent.PressurePlateHelper").CreateHooks(mm);
                            mm.Module.GetType("Terraria.GameContent.CraftingRequests").CreateHooks(mm);
                            Console.WriteLine("Done");

                            RemovePatchScaffolding(mm);
                        }
                        else if (modType == PatchStage.Write)
                        {
                            mm.PatchContext = new("OTAPI.Runtime");
                            mm.CreateRuntimeHooks(otapiRuntimeDllDestination);

                            Console.WriteLine("[OTAPI] Building NuGet package...");
                            NugetPackager.Build(mm);
                        }
                    }
                    return EApplyResult.Continue;
                });
            }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= ResolvingFile;
        }

        Console.WriteLine("[OTAPI] Building markdown documentation...");
        MarkdownDocumentor.Write();

        Console.WriteLine("[OTAPI] Building artifacts...");
        this.WriteCIArtifacts(ArtifactName);

        Console.WriteLine("Patching has completed.");
    }

}

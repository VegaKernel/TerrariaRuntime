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

using ModFramework;
using ModFramework.Relinker;
using Mono.Cecil;
using OTAPI.Patcher.Resolvers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static ModFramework.ModContext;

namespace OTAPI.Patcher.Targets;

[MonoMod.MonoModIgnore]
public interface IPatchTarget
{
    string DisplayText { get; }
    string InstallDestination { get; }
    Task PatchAsync();
}

[MonoMod.MonoModIgnore]
public interface IModPatchTarget : IPatchTarget
{
    ModContext ModContext { get; }
    string GetEmbeddedResourcesDirectory(string fileinput);
    void AddSearchDirectories(ModFwModder modder);
    MarkdownDocumentor MarkdownDocumentor { get; }
    bool GenerateSymbols { get; }
}

[MonoMod.MonoModIgnore]
public interface IServerPatchTarget : IModPatchTarget
{
    IFileResolver FileResolver { get; }
}

[MonoMod.MonoModIgnore]
public interface IClientPatchTarget : IModPatchTarget
{
}

[MonoMod.MonoModIgnore]
public static partial class PatchTargetExtensions
{
    public static string ExtractResources(this IModPatchTarget target, string fileinput)
    {
        var dir = target.GetEmbeddedResourcesDirectory(fileinput);
        var extractor = new ResourceExtractor();
        return extractor.Extract(fileinput, dir);
    }

    public static void AddPatchMetadata(this IPatchTarget target, ModFwModder modder,
        string? initialModuleName = null,
        string? inputName = null)
    {
        modder.AddMetadata("OTAPI.Target", target.DisplayText);
        if (initialModuleName is not null)
            modder.AddMetadata("OTAPI.ModuleName", initialModuleName);
        if (inputName is not null)
            modder.AddMetadata("OTAPI.Input", inputName);
    }

    public static void WriteCIArtifacts(this IPatchTarget target, string outputFolder)
    {
        if (Directory.Exists(outputFolder))
            Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);

        File.Copy(Path.Combine(AppContext.BaseDirectory, "../../../../COPYING.txt"), Path.Combine(outputFolder, "COPYING.txt"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "OTAPI.dll"), Path.Combine(outputFolder, "OTAPI.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "OTAPI.Runtime.dll"), Path.Combine(outputFolder, "OTAPI.Runtime.dll"));
    }

    public static void AddEnvMetadata(this ModFwModder modder)
    {
        var commitSha = Common.GetGitCommitSha();
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER")?.Trim();

        if (!string.IsNullOrWhiteSpace(commitSha))
            modder.AddMetadata("GitHub.Commit", commitSha);

        if (!string.IsNullOrWhiteSpace(run))
            modder.AddMetadata("GitHub.Action.RunNo", run);
    }

    public static void CreateRuntimeHooks(this ModFwModder modder, string output)
    {
        modder.Log("[OTAPI] Generating OTAPI.Runtime.dll");
        var generator = new MonoMod.RuntimeDetour.HookGen.HookGenerator(modder, "OTAPI.Runtime.dll");
        using var generated = new MemoryStream();
        using (ModuleDefinition outputModule = generator.OutputModule)
        {
            generator.Generate();
            outputModule.Write(generated);
        }

        generated.Position = 0;
        var fileName = Path.GetFileName(output);
        using var runtimeModder = new ModFwModder(new("OTAPI.Runtime"))
        {
            Input = generated,
            OutputPath = output,
            MissingDependencyThrow = false,
            GACPaths = Array.Empty<string>(),
        };
        runtimeModder.Log($"[OTAPI] Relinking runtime hook assembly core libraries: {fileName}");

        runtimeModder.Read();
        runtimeModder.AddTask<CoreLibRelinker>();
        runtimeModder.MapDependencies();
        runtimeModder.AutoPatch();
        runtimeModder.Write();
    }

    public static void AddConstants(this IModPatchTarget target, string inputName, ModFwModder modder)
    {
        var version = modder.Module.Assembly.Name.Version;
        var majorConstant = $"{inputName}_V{version.Major}_{version.Minor}";
        var fullNameConstant = $"{inputName}_{version.ToString().Replace(".", "_", StringComparison.Ordinal)}";
        var sendDataPatchConstant = $"{inputName}_SendDataNumber{(version == new Version("1.4.3.0") ? "8" : "7")}";
        var entitySourceConstant = $"{inputName}_EntitySources{(version >= new Version("1.4.3.3") ? "Active" : "Inactive")}";

        List<string> constants = new()
        {
            inputName,
            majorConstant,
            fullNameConstant,
            sendDataPatchConstant,
            entitySourceConstant,
        };

        if (version >= new Version("1.4.4.1"))
            constants.Add($"{inputName}_1441_OrAbove");
        if (version >= new Version("1.4.4.2"))
            constants.Add($"{inputName}_1442_OrAbove");
        if (version >= new Version("1.4.4.8"))
            constants.Add($"{inputName}_1448_OrAbove");
        if (version >= new Version("1.4.5"))
            constants.Add($"{inputName}_1450_OrAbove");
        if (version >= new Version("1.4.5.5"))
            constants.Add($"{inputName}_1455_OrAbove");
        if (version >= new Version("1.4.5.7"))
            constants.Add($"{inputName}_1457_OrAbove");
        if (version >= new Version("1.4.5.8"))
            constants.Add($"{inputName}_1458_OrAbove");

        target.ModContext.ReferenceConstants.AddRange(constants.Select(x => $"#define {x}"));

        File.WriteAllText("AutoGenerated.target", @$"<!-- DO NOT EDIT THIS FILE! It was auto generated by the setup project -->
<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <DefineConstants>{string.Join(';', constants)}</DefineConstants>
  </PropertyGroup>
</Project>");
    }

    public static string Patch(this IModPatchTarget target, string status, string input, string output, bool publicEverything,
        Func<ModType, ModFwModder?, EApplyResult> onApplying,
        Action<ModFwModder, string>? print = null)
    {
        print ??= (_, str) => Console.WriteLine(str);

        EApplyResult OnApplying(ModType modType, ModFwModder? modder) => onApplying(modType, modder);

        target.ModContext.OnApply += OnApplying;
        try
        {
            using var modder = new ModFwModder(target.ModContext)
            {
                InputPath = input,
                OutputPath = output,
                MissingDependencyThrow = false,
                PublicEverything = publicEverything,
                LogVerboseEnabled = true,
                GACPaths = Array.Empty<string>(),
                MarkdownDocumentor = target.MarkdownDocumentor,
            };

            target.AddSearchDirectories(modder);
            modder.AddTask<CoreLibRelinker>();

            modder.Read();

            print(modder, $"[OTAPI] Mapping dependencies: {status}");
            modder.MapDependencies();

            print(modder, $"[OTAPI] Patching: {status}");
            modder.AutoPatch();

            print(modder, $"[OTAPI] Writing: {status}, Path={new Uri(Environment.CurrentDirectory).MakeRelativeUri(new Uri(modder.OutputPath))}");

            if (!target.GenerateSymbols)
            {
                modder.WriterParameters.SymbolWriterProvider = null;
                modder.WriterParameters.WriteSymbols = false;
            }

            modder.Write();
            return modder.OutputPath;
        }
        finally
        {
            target.ModContext.OnApply -= OnApplying;
        }
    }
}

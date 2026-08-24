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

using OTAPI.PatchEngine;
using OTAPI.PatchEngine.Relinking;
using Mono.Cecil;
using System;
using System.IO;
using System.Linq;
using static OTAPI.PatchEngine.PatchContext;

namespace OTAPI.Patcher;

[MonoMod.MonoModIgnore]
internal static class ServerPatchOperations
{
    public static string ExtractResources(this ServerPatcher target, string fileinput)
    {
        var dir = target.GetEmbeddedResourcesDirectory(fileinput);
        var extractor = new ResourceExtractor();
        return extractor.Extract(fileinput, dir);
    }

    public static void AddPatchMetadata(this ServerPatcher target, AssemblyPatcher modder,
        string? initialModuleName = null,
        string? inputName = null)
    {
        modder.AddMetadata("OTAPI.Target", target.DisplayText);
        if (initialModuleName is not null)
            modder.AddMetadata("OTAPI.ModuleName", initialModuleName);
        if (inputName is not null)
            modder.AddMetadata("OTAPI.Input", inputName);
    }

    public static void WriteCIArtifacts(this ServerPatcher target, string outputFolder)
    {
        if (Directory.Exists(outputFolder))
            Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);

        File.Copy(Path.Combine(AppContext.BaseDirectory, "../../../../../LICENSE"), Path.Combine(outputFolder, "LICENSE"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "OTAPI.dll"), Path.Combine(outputFolder, "OTAPI.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "OTAPI.Runtime.dll"), Path.Combine(outputFolder, "OTAPI.Runtime.dll"));
    }

    public static void AddEnvMetadata(this AssemblyPatcher modder)
    {
        var commitSha = Common.GetGitCommitSha();
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER")?.Trim();

        if (!string.IsNullOrWhiteSpace(commitSha))
            modder.AddMetadata("GitHub.Commit", commitSha);

        if (!string.IsNullOrWhiteSpace(run))
            modder.AddMetadata("GitHub.Action.RunNo", run);
    }

    public static void CreateRuntimeHooks(this AssemblyPatcher modder, string output)
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
        using var runtimeModder = new AssemblyPatcher(new("OTAPI.Runtime"))
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

    public static string Patch(this ServerPatcher target, string status, string input, string output, bool publicEverything,
        Func<PatchStage, AssemblyPatcher?, EApplyResult> onApplying,
        Action<AssemblyPatcher, string>? print = null)
    {
        print ??= (_, str) => Console.WriteLine(str);

        EApplyResult OnApplying(PatchStage modType, AssemblyPatcher? modder) => onApplying(modType, modder);

        target.PatchContext.OnApply += OnApplying;
        try
        {
            using var modder = new AssemblyPatcher(target.PatchContext)
            {
                InputPath = input,
                OutputPath = output,
                MissingDependencyThrow = false,
                PublicEverything = publicEverything,
                LogVerboseEnabled = true,
                GACPaths = Array.Empty<string>(),
                MarkdownDocumentor = target.MarkdownDocumentor,
            };

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
            target.PatchContext.OnApply -= OnApplying;
        }
    }
}

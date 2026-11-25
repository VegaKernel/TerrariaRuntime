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
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace TerrariaRuntime.Patcher;

[MonoMod.MonoModIgnore]
public class NugetPackageBuilder
{
    private static readonly string[] ForbiddenRuntimeReferences =
    {
        "ModFramework",
        "TerrariaRuntime.PatchEngine",
        "MonoMod.Patcher",
        "MonoMod",
        "Mono.Cecil",
        "Mono.Cecil.Rocks",
        "FNA",
        "TerrariaRuntime.Client.Launcher",
    };

    private static readonly string[] ForbiddenServerResources =
    {
        "Terraria.IO.Data.ResourcePacksDefaultInfo.tsv",
    };

    private static string GetNugetVersionFromAssembly(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
            version = assembly.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"Unable to determine package version for '{assembly.GetName().Name}'.");

        return version.Split('+')[0];
    }

    private static string GetNugetVersionFromAssembly<TType>()
        => GetNugetVersionFromAssembly(typeof(TType).Assembly);

    private static string GetTargetFramework()
    {
        var frameworkName = typeof(NugetPackageBuilder).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        if (string.IsNullOrWhiteSpace(frameworkName))
            throw new InvalidOperationException("Unable to determine the TerrariaRuntime target framework.");

        var framework = new FrameworkName(frameworkName);
        if (!string.Equals(framework.Identifier, ".NETCoreApp", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported TerrariaRuntime target framework '{frameworkName}'.");

        return $"net{framework.Version.Major}.{framework.Version.Minor}";
    }

    private static void ValidateRuntimeAssembly(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var forbiddenReferences = assembly.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => ForbiddenRuntimeReferences.Contains(name, StringComparer.Ordinal))
            .Concat(assembly.MainModule.GetTypeReferences()
                .Select(reference => reference.Scope?.Name)
                .Where(name => name is not null
                    && ForbiddenRuntimeReferences.Contains(name, StringComparer.Ordinal))!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (forbiddenReferences.Length != 0)
        {
            throw new InvalidOperationException(
                $"Generated runtime assembly '{Path.GetFileName(path)}' leaked patch-time dependencies: {string.Join(", ", forbiddenReferences)}.");
        }

        var forbiddenResources = assembly.MainModule.Resources
            .Select(resource => resource.Name)
            .Where(name => ForbiddenServerResources.Contains(name, StringComparer.Ordinal))
            .ToArray();
        if (forbiddenResources.Length != 0)
        {
            throw new InvalidOperationException(
                $"Generated server assembly '{Path.GetFileName(path)}' retained client-only resources: {string.Join(", ", forbiddenResources)}.");
        }
    }

    public string PackageName { get; set; }
    public string NuspecPath { get; set; }

    public NugetPackageBuilder(string packageName, string nuspecPath)
    {
        PackageName = packageName;
        NuspecPath = nuspecPath;
    }

    public void Build(AssemblyPatcher modder)
    {
        var serverAssemblyPath = Path.Combine(AppContext.BaseDirectory, "TerrariaRuntime.dll");
        var runtimeAssemblyPath = Path.Combine(AppContext.BaseDirectory, "TerrariaRuntime.Hooks.dll");
        ValidateRuntimeAssembly(serverAssemblyPath);
        ValidateRuntimeAssembly(runtimeAssemblyPath);

        var nuspecXml = File.ReadAllText(NuspecPath);
        nuspecXml = nuspecXml.Replace(
            "[INJECT_VERSION]",
            GetNugetVersionFromAssembly(typeof(NugetPackageBuilder).Assembly),
            StringComparison.Ordinal);

        var commitSha = Common.GetGitCommitSha();
        nuspecXml = nuspecXml.Replace(
            "[INJECT_GIT_HASH]",
            string.IsNullOrWhiteSpace(commitSha) ? string.Empty : $" git#{commitSha}",
            StringComparison.Ordinal);

        var platforms = new[] { GetTargetFramework() };
        var steamworks = modder.Module.AssemblyReferences.First(x => x.Name == "Steamworks.NET");
        var newtonsoft = modder.Module.AssemblyReferences.First(x => x.Name == "Newtonsoft.Json");

        // The TerrariaRuntime-owned patch engine and MonoMod patcher are build-time tools only. The generated server package
        // should depend exclusively on assemblies required by the patched Terraria runtime.
        var dependencies = new[]
        {
            (Name: typeof(MonoMod.RuntimeDetour.DetourBase).Assembly.GetName().Name!, Version: GetNugetVersionFromAssembly<MonoMod.RuntimeDetour.DetourBase>()),
            (Name: steamworks.Name, Version: steamworks.Version.ToString()),
            (Name: newtonsoft.Name, Version: "13.0.4"),
            (Name: "System.IO.Packaging", Version: GetNugetVersionFromAssembly<System.IO.Packaging.ZipPackage>()),
            (Name: "System.Configuration.ConfigurationManager", Version: GetNugetVersionFromAssembly<System.Configuration.ConfigurationErrorsException>()),
            (Name: "System.Drawing.Common", Version: GetNugetVersionFromAssembly(Assembly.Load("System.Drawing.Common"))),
        };

        var xmlDependency = string.Join(string.Empty, dependencies.Select(dep => $"\n\t    <dependency id=\"{dep.Name}\" version=\"{dep.Version}\" />"));
        var xmlGroup = string.Join(string.Empty, platforms.Select(platform => $"\n\t<group targetFramework=\"{platform}\">{xmlDependency}\n\t</group>"));
        var xmlDependencies = $"<dependencies>{xmlGroup}\n    </dependencies>";

        nuspecXml = nuspecXml.Replace("[INJECT_DEPENDENCIES]", xmlDependencies, StringComparison.Ordinal);
        nuspecXml = nuspecXml.Replace("[INJECT_YEAR]", DateTime.UtcNow.Year.ToString(), StringComparison.Ordinal);

        using var nuspec = new MemoryStream(Encoding.UTF8.GetBytes(nuspecXml));
        var manifest = NuGet.Packaging.Manifest.ReadFrom(nuspec, validateSchema: true);
        var packageBuilder = new NuGet.Packaging.PackageBuilder();
        packageBuilder.Populate(manifest.Metadata);

        var basePath = Path.Combine(AppContext.BaseDirectory, "../../../../../");
        packageBuilder.AddFiles(basePath, "LICENSE", "LICENSE");

        foreach (var platform in platforms)
        {
            var destination = Path.Combine("lib", platform);
            packageBuilder.AddFiles(AppContext.BaseDirectory, "TerrariaRuntime.dll", destination);
            packageBuilder.AddFiles(AppContext.BaseDirectory, "TerrariaRuntime.Hooks.dll", destination);
        }

        var packageFileName = string.IsNullOrWhiteSpace(manifest.Metadata.Id)
            ? PackageName
            : $"{manifest.Metadata.Id}.nupkg";
        var output = Path.Combine(AppContext.BaseDirectory, packageFileName);
        if (File.Exists(output))
            File.Delete(output);

        using var packageStream = File.OpenWrite(output);
        packageBuilder.Save(packageStream);
    }
}

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
using Mono.Cecil;
using MonoMod;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static TerrariaRuntime.PatchEngine.PatchContext;

namespace TerrariaRuntime.PatchEngine.Relinking;

[MonoMod.MonoModIgnore]
public class SystemType
{
    public string? FilePath { get; set; }
    public AssemblyDefinition? Assembly { get; set; }
    public ExportedType? Type { get; set; }

    public AssemblyNameReference? AsNameReference() => Assembly?.AsNameReference();

    public override string ToString() => Type?.ToString() ?? "";
}

public static partial class Extensions
{
    public static AssemblyNameReference AsNameReference(this AssemblyDefinition assembly)
    {
        var name = assembly.Name;
        return new(name.Name, name.Version)
        {
            PublicKey = name.PublicKey,
            PublicKeyToken = name.PublicKeyToken,
            Culture = name.Culture,
            Hash = name.Hash,
            HashAlgorithm = name.HashAlgorithm,
            Attributes = name.Attributes
        };
    }
}

[MonoMod.MonoModIgnore]
public delegate AssemblyNameReference ResolveCoreLibHandler(TypeReference target);

[MonoMod.MonoModIgnore]
public class CoreLibRelinker : TypeRelinker
{
    private static readonly HashSet<string> LegacyFrameworkAssemblies = new(StringComparer.Ordinal)
    {
        "mscorlib",
        "netstandard",
        "System.Private.CoreLib",
        "System",
        "System.Core",
        "System.Drawing",
        "System.Runtime.Serialization",
        "WindowsBase",
    };

    private readonly Dictionary<string, AssemblyNameReference> _frameworkTypes = new(StringComparer.Ordinal);

    protected CoreLibRelinker(AssemblyPatcher modder) : base(modder)
    {
    }

    public IFrameworkResolver FrameworkResolver { get; set; } = new DefaultFrameworkResolver();

    public event ResolveCoreLibHandler? Resolve;

    public bool ThrowResolveFailure { get; set; }

    private EApplyResult OnApplying(PatchStage modType, AssemblyPatcher? modder)
    {
        if (modder is not null)
        {
            if (modType == PatchStage.Shutdown)
            {
                modder.PatchContext.OnApply -= OnApplying;
            }
            else if (modType == PatchStage.PreWrite)
            {
                for (var i = modder.Module.AssemblyReferences.Count - 1; i >= 0; i--)
                {
                    var aref = modder.Module.AssemblyReferences[i];
                    if (aref.Name == "mscorlib"
                        || aref.Name == "System.Private.CoreLib"
                    )
                    {
                        modder.Module.AssemblyReferences.RemoveAt(i);
                    }
                }
            }
        }

        return EApplyResult.Continue;
    }

    void PatchTargetFramework()
    {
        if (Modder is null) throw new ArgumentNullException(nameof(Modder));
        var tfa = Modder.Module.Assembly.GetTargetFrameworkAttribute();

        // remove existing, if any
        if (tfa != null)
        {
            Modder.Module.Assembly.CustomAttributes.Remove(tfa);
        }

        // add a new one
        var ctor = Modder.Module.ImportReference(Modder.FindType("System.Runtime.Versioning.TargetFrameworkAttribute").Resolve().FindMethod("System.Void .ctor(System.String)"));
        Modder.Module.Assembly.CustomAttributes.Add(new(ctor)
        {
            ConstructorArguments =
                {
                    new (Modder.Module.TypeSystem.String, $".NETCoreApp,Version=v{Environment.Version.Major}.{Environment.Version.Minor}") // runtimeinformation could be utilised, but we don't expect to run on legacy .net so should be consistent values
                },
            Properties =
                {
                    new ("FrameworkDisplayName", new (Modder.Module.TypeSystem.String, ""))
                }
        });
    }

    public override void Registered()
    {
        // prepare target framework. cannot use the shim libraries otherwise you end up with System.Private.CoreLib refs
        var fw = FrameworkResolver.FindFramework();
        Modder.AssemblyResolver.AddSearchDirectory(fw); // allow monomod to resolve System.Runtime
        IndexFrameworkTypes(fw);
        IndexReferenceAssemblies(AppContext.BaseDirectory);

        base.Registered();

        Modder.PatchContext.OnApply += OnApplying;
    }

    private void IndexReferenceAssemblies(string directory)
    {
        // Only framework compatibility packages are needed from the patcher's output.
        // Indexing every DLL also re-opens a previous TerrariaRuntime output while it is being
        // replaced, which can keep the generated assembly locked on Windows.
        foreach (var path in Directory.GetFiles(directory, "System.*.dll"))
        {
            try
            {
                using var assembly = AssemblyDefinition.ReadAssembly(path);
                var reference = assembly.AsNameReference();
                foreach (var type in EnumerateTypes(assembly.MainModule.Types))
                    _frameworkTypes.TryAdd(type.FullName, reference);
                foreach (var type in assembly.MainModule.ExportedTypes)
                    _frameworkTypes.TryAdd(type.FullName, reference);
            }
            catch (BadImageFormatException)
            {
                // Native runtime assets are not metadata reference assemblies.
            }
        }
    }

    private void IndexFrameworkTypes(string frameworkDirectory)
    {
        var files = Directory.GetFiles(frameworkDirectory, "*.dll")
            .Where(path => !LegacyFrameworkAssemblies.Contains(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                "System.Runtime",
                StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal);

        foreach (var path in files)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var reference = assembly.AsNameReference();

            foreach (var type in EnumerateTypes(assembly.MainModule.Types))
                _frameworkTypes.TryAdd(type.FullName, reference);
            foreach (var type in assembly.MainModule.ExportedTypes)
                _frameworkTypes.TryAdd(type.FullName, reference);
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in EnumerateTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private AssemblyNameReference? ResolveFrameworkAssembly(TypeReference type)
    {
        var element = type.GetElementType();
        if (_frameworkTypes.TryGetValue(element.FullName, out var reference))
            return reference;

        for (var declaring = element.DeclaringType; declaring is not null; declaring = declaring.DeclaringType)
            if (_frameworkTypes.TryGetValue(declaring.FullName, out reference))
                return reference;

        if (string.IsNullOrEmpty(element.Namespace)
            && string.Equals(element.Name, "SpecialFolder", StringComparison.Ordinal)
            && _frameworkTypes.TryGetValue("System.Environment", out reference))
            return reference;

        return null;
    }

    public override void PreWrite()
    {
        RelinkLegacyTypeReferences();
        PatchTargetFramework();
        base.PreWrite();
    }

    private void RelinkLegacyTypeReferences()
    {
        if (Modder is null) throw new ArgumentNullException(nameof(Modder));

        foreach (var type in Modder.Module.GetTypeReferences())
        {
            var element = type.GetElementType();
            var knownSystemType = (string.Equals(element.Namespace, "System", StringComparison.Ordinal)
                    || element.Namespace.StartsWith("System.", StringComparison.Ordinal))
                && _frameworkTypes.ContainsKey(element.FullName);
            var selfScopedSystemType = type.Scope is ModuleDefinition
                && element.Namespace.StartsWith("System.", StringComparison.Ordinal)
                && Modder.Module.GetType(element.FullName) is null;
            var frameworkAssembly = LegacyFrameworkAssemblies.Contains(type.Scope.Name)
                || type.Scope.Name.StartsWith("System.", StringComparison.Ordinal)
                || selfScopedSystemType
                || knownSystemType;
            if (!frameworkAssembly)
                continue;

            var replacement = ResolveFrameworkAssembly(type);
            if (replacement is null)
                continue;

            if (string.IsNullOrEmpty(type.Namespace)
                && string.Equals(type.Name, "SpecialFolder", StringComparison.Ordinal)
                && type.DeclaringType is null)
            {
                type.DeclaringType = new TypeReference(
                    "System",
                    "Environment",
                    Modder.Module,
                    replacement);
            }

            var existing = Modder.Module.AssemblyReferences.FirstOrDefault(reference =>
                string.Equals(reference.Name, replacement.Name, StringComparison.Ordinal)
                && reference.Version == replacement.Version);
            if (existing is null)
            {
                existing = replacement;
                Modder.Module.AssemblyReferences.Add(existing);
            }

            type.Scope = existing;
        }

        var usedScopes = Modder.Module.GetTypeReferences()
            .Select(type => type.Scope.ToString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var reference in Modder.Module.AssemblyReferences.ToArray())
            if ((LegacyFrameworkAssemblies.Contains(reference.Name)
                    || reference.Name.StartsWith("System.", StringComparison.Ordinal))
                && (!usedScopes.Contains(reference.ToString())
                    || string.Equals(reference.Name, "WindowsBase", StringComparison.Ordinal)))
                Modder.Module.AssemblyReferences.Remove(reference);

        foreach (var group in Modder.Module.AssemblyReferences
            .GroupBy(reference => reference.FullName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            var canonical = group.First();
            foreach (var type in Modder.Module.GetTypeReferences())
                if (type.Scope is AssemblyNameReference scope
                    && string.Equals(scope.FullName, canonical.FullName, StringComparison.Ordinal))
                    type.Scope = canonical;

            foreach (var duplicate in group.Skip(1).ToArray())
                Modder.Module.AssemblyReferences.Remove(duplicate);
        }
    }

    AssemblyNameReference? ResolveAssembly(TypeReference type)
    {
        var frameworkReference = ResolveFrameworkAssembly(type);
        if (frameworkReference is not null)
            return frameworkReference;

        var res = Resolve?.Invoke(type);
        if (res is null)
        {
            if (type.Scope is AssemblyNameReference anr)
            {
                var dependencyMatch = Modder.ResolveDependency(type);
                if (dependencyMatch is not null)
                    return dependencyMatch;

                if (ThrowResolveFailure)
                    throw new Exception($"Relink failed. Unable to resolve {type.FullName}");
            }
            else throw new Exception($"{type.Scope.GetType().FullName} is not handled.");
        }

        if (res?.Name == "mscorlib" || res?.Name == "System.Private.CoreLib")
            throw new Exception($"Relink failed, must not be corelib");

        return res;
    }

    public override bool RelinkType<TRef>(ref TRef type)
    {
        if (LegacyFrameworkAssemblies.Contains(type.Scope.Name))
        {
            var asm = ResolveAssembly(type);
            if (asm is not null)
            {
                // transition period (until i get the new roslyn package working wrt nullattrs)
                // we must find the highest package, because System.Runtime can have v5 + v6
                var existing = type.Module.AssemblyReferences
                    .OrderByDescending(x => x.Version)
                    .FirstOrDefault(x => x.Name == asm.Name);
                if (existing != null)
                {
                    type.Scope = existing;
                }
                else
                {
                    type.Scope = asm;
                    type.Module.AssemblyReferences.Add(asm);
                }
                return true;
            }
        }
        return false;
    }
}

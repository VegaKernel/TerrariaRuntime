using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace TerrariaRuntime.PatchEngine;

public class ModPluginLoader
{
    public AssemblyLoadContext AssemblyLoader { get; set; } = AssemblyLoadContext.Default;
    private List<Assembly> _assemblies = new();

    public delegate bool OnModFileLoadingHandler(string filePath);
    public event OnModFileLoadingHandler? OnModFileLoading;

    public delegate bool OnModAssemblyLoadingHandler(Assembly assembly);
    public event OnModAssemblyLoadingHandler? OnModAssemblyLoading;

    public bool CanAddFile(string filePath)
        => OnModFileLoading?.Invoke(filePath) != false;

    public bool CanAddAssembly(Assembly assembly)
        => OnModAssemblyLoading?.Invoke(assembly) != false;

    public void AddAssembly(Assembly assembly)
    {
        if (!CanAddAssembly(assembly))
            return;

        _assemblies.Add(assembly);
    }

    public void AddFile(string path)
    {
        if (!CanAddFile(path))
            return;

        var full = Path.GetFullPath(path);
        var asm = AssemblyLoader.LoadFromAssemblyPath(full);
        AddAssembly(asm);
    }

    public void AddFromFolder(string path, string searchPattern = "*.dll", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (var file in Directory.EnumerateFiles(path, searchPattern, searchOption))
            AddFile(file);
    }

    public IList<PatchAttribute> DiscoverPatchAttributes(PatchStage? modType = null, IEnumerable<Assembly>? assemblies = null)
    {
        var modifications = PatchAttribute.Discover(assemblies ?? _assemblies);
        var requested = modType is null ? modifications : modifications.Where(x => x.Type == modType);
        return requested.ToList();
    }
}

public class PatchContext
{
    public delegate void OnContextCreated(PatchContext context);
    public static event OnContextCreated? ContextCreated;

    public static String DefaultBaseDirectory => AppContext.BaseDirectory;
    public string BaseDirectory { get; set; } = DefaultBaseDirectory;

    public ModPluginLoader PluginLoader { get; set; }

    public List<object> Parameters { get; } = new();
    public List<string> ReferenceFiles { get; } = new();

    public string TargetAssemblyName { get; set; }

    public PatchContext(string targetAssemblyName)
    {
        PluginLoader = new();
        TargetAssemblyName = targetAssemblyName;
        ContextCreated?.Invoke(this);
    }

    private void Invoke(PatchAttribute modification, PatchStage modType, MonoMod.MonoModder? modder, List<object> availableParameters)
    {
        Console.WriteLine($"[PatchEngine:{modType}] {modification.Description}");
        if (modification?.MethodBase?.DeclaringType is null) throw new Exception($"Failed to determine ctor");

        MethodBase modCtor = modification.MethodBase; //.DeclaringType.GetConstructors().Single();
        var modCtorParams = modCtor.GetParameters();

        // bind arguments
        var args = new object[modCtorParams.Length];
        {
            for (var i = 0; i < modCtorParams.Length; i++)
            {
                var param = modCtorParams[i];
                var paramValue = availableParameters.SingleOrDefault(p =>
                    param.ParameterType.IsAssignableFrom(p.GetType())
                );

                if (paramValue != null)
                    args[i] = paramValue;
                else throw new Exception($"No valid for parameter `{param.Name}` in modification {modification.MethodBase.DeclaringType.FullName}");
            }
        }

        if (modification.MethodBase.IsConstructor)
            Activator.CreateInstance(modification.MethodBase.DeclaringType, args, null);
        else modification.MethodBase.Invoke(modification.Instance, args);
    }

    /* mainly to expose an Apply function without the need of MonoMod */
    public void Apply(PatchStage modType) => Apply(modType, null, null);
    public void Apply(PatchStage modType, IEnumerable<Assembly> assemblies) => Apply(modType, null, assemblies);

    public delegate EApplyResult OnApplyingHandler(PatchStage modType, AssemblyPatcher? modder);
    public event OnApplyingHandler? OnApply;

    public enum EApplyResult
    {
        Continue,
        Cancel,
    }

    public void Apply(PatchStage modType, AssemblyPatcher? modder, IEnumerable<Assembly>? assemblies = null)
    {
        if (OnApply is null || OnApply(modType, modder) != EApplyResult.Cancel)
            _Apply(modType, modder, assemblies);
    }

    private void _Apply(PatchStage modType, AssemblyPatcher? modder, IEnumerable<Assembly>? assemblies = null)
    {
        if (modder?.LogVerboseEnabled == true) Console.WriteLine($"[PatchEngine:{modType}] Applying mods...");
        List<object> availableParameters = new()
        {
            modType,
            this,
        };

        if (modder != null) availableParameters.Add(modder);
        availableParameters.AddRange(Parameters);

        var modifications = PluginLoader.DiscoverPatchAttributes(modType, assemblies);

        List<PatchAttribute> completed = new();
        //Dictionary<PatchAttribute, Task> tasks = new();
        Dictionary<PatchAttribute, int> counter = new();

        void QueueFreeTasks()
        {
            foreach (var attr in modifications.ToArray())
            {
                var deps = attr.Dependencies ?? Enumerable.Empty<string>();
                var pending = deps.Where(dep =>
                    !completed.Any(d =>
                        d.MethodBase?.DeclaringType?.Name == dep
                        || (d.UniqueName != null && d.UniqueName == dep)
                    )
                );

                if (!pending.Any())
                {
                    modifications.Remove(attr);
                    //tasks.Add(attr, Invoke(attr, modType, modder, availableParameters));
                    Invoke(attr, modType, modder, availableParameters);
                    completed.Add(attr);
                }
                else Console.WriteLine($"[PatchEngine] Awaiting dependencies for {attr.MethodBase?.DeclaringType?.FullName} ({attr.Description}) needs: {String.Join(",", pending)}");
            }
        }

        while (modifications.Any())
        {
            QueueFreeTasks();

            //Task.WhenAll(tasks.Values).Wait();

            //foreach(var task in tasks)
            //{
            //    completed.Add(task.Key);
            //}
            //tasks.Clear();
        }
    }
}
